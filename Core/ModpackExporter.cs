using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>导出结果</summary>
public sealed class ExportResult
{
    public string Path { get; init; } = "";
    /// <summary>在线匹配上、只写链接不打包的 mod 数</summary>
    public int Matched { get; init; }
    /// <summary>实际打进 overrides 的文件数</summary>
    public int Packed { get; init; }
    public double SizeMb { get; init; }
}

/// <summary>
/// 版本导出为整合包，输出 Modrinth 的 <c>.mrpack</c>。
/// 默认开启在线匹配：按 jar 的 SHA1 查 Modrinth，能匹配到的只写 <c>files[]</c> 链接，
/// 匹配不到的才打进 <c>overrides/</c>。实测 62 个 Mod 的包从 518 MB 降到 186 MB。
/// 关掉联网匹配就全部走 overrides，包大但一定装得回去。
/// </summary>
public static class ModpackExporter
{
    /// <summary>不往整合包里塞的东西（日志、存档、缓存这些装了也没用）</summary>
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "crash-reports", "saves", "screenshots", "backups", "natives",
        "downloads", ".cache", "cache", "temp", "tmp", "modpack-export",
    };

    /// <summary>
    /// 导出。返回导出的结果（路径 + 匹配统计）。
    /// <paramref name="matchOnline"/> = true 时会拿 mod 的 SHA1 去 Modrinth 查，
    /// 匹配上的只写链接不打包（包体积能小一个数量级）。
    /// </summary>
    public static async Task<ExportResult> ExportAsync(
        LauncherConfig cfg, MinecraftVersion ver, string outPath,
        string packName, string packVersion, bool matchOnline,
        TaskItem task, CancellationToken ct)
    {
        // mods 目录的上一级就是游戏目录（隔离和不隔离两种情况都对）
        var modsDir = VersionSettings.InstalledModsDir(cfg, ver);
        var gameDir = Path.GetDirectoryName(modsDir) ?? ver.Dir;

        var (kind, loaderVer) = LoaderInfoOf(ver);
        var baseMc = VersionHelper.ResolveBaseVersion(ver, VersionScanner.Scan(cfg.MinecraftPath));
        if (string.IsNullOrEmpty(baseMc))
            throw new InvalidOperationException("算不出这个版本的基础 MC 版本,导不了。");

        task.LogLine($"游戏目录: {gameDir}");
        task.LogLine($"基础版本: MC {baseMc}{(kind is null ? "" : $" + {kind} {loaderVer}")}");

        // ---- 要打包的文件清单（先算好，进度才准）----
        var allFiles = CollectFiles(gameDir, ver, task, ct);
        task.LogLine($"待打包 {allFiles.Count} 个文件");

        // ---- 联网匹配：只有 mods/*.jar 有戏，配置/资源包匹配不了 ----
        var modJars = allFiles
            .Where(f => f.Rel.StartsWith("mods" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && f.Rel.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var online = new List<OnlineEntry>();
        var packed = new List<(string Full, string Rel)>(allFiles);
        if (matchOnline && modJars.Count > 0)
        {
            task.LogLine($"联网匹配 {modJars.Count} 个 mod(算 SHA1 → 查 Modrinth)...");
            var hashes = new Dictionary<string, (string Full, string Rel)>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            foreach (var (full, rel) in modJars)
            {
                ct.ThrowIfCancellationRequested();
                var sha1 = await Sha1OfAsync(full, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(sha1))
                    hashes[sha1] = (full, rel);
                i++;
                if (i % 10 == 0 || i == modJars.Count)
                {
                    task.Report(0.05 + 0.25 * i / modJars.Count);
                    task.LogLine($"算哈希 {i}/{modJars.Count}");
                }
            }

            var matched = await MatchModrinthAsync(hashes.Keys.ToList(), task, ct).ConfigureAwait(false);
            task.LogLine($"在线匹配成功 {matched.Count}/{hashes.Count}");

            foreach (var (sha1, (full, rel)) in hashes)
            {
                if (!matched.TryGetValue(sha1, out var m))
                    continue;
                online.Add(new OnlineEntry
                {
                    Rel = rel.Replace(Path.DirectorySeparatorChar, '/'),
                    Sha1 = sha1,
                    Sha512 = m.Sha512,
                    Url = m.Url,
                    Size = m.Size,
                });
                packed.RemoveAll(p => p.Full == full);
            }
            task.LogLine($"匹配上的 {online.Count} 个不再打包,实际打包 {packed.Count} 个");
        }
        else if (!matchOnline)
        {
            task.LogLine("已关闭联网匹配,全部内容打包进 overrides");
        }

        // ---- 写 ----
        var parent = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        TryDelete(outPath);

        try
        {
            using var zip = ZipFile.Open(outPath, ZipArchiveMode.Create);

            // 1) modrinth.index.json
            var index = BuildIndexJson(packName, packVersion, baseMc, kind, loaderVer, online);
            var indexEntry = zip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
            await using (var s = indexEntry.Open())
            await using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                await w.WriteAsync(index.AsMemory(), ct).ConfigureAwait(false);

            // 2) overrides/*
            var done = 0;
            foreach (var (fullPath, relPath) in packed)
            {
                ct.ThrowIfCancellationRequested();
                var entryName = "overrides/" + relPath.Replace(Path.DirectorySeparatorChar, '/');
                zip.CreateEntryFromFile(fullPath, entryName, CompressionLevel.Optimal);
                done++;
                if (done % 20 == 0 || done == packed.Count)
                {
                    task.Report(0.3 + 0.7 * done / Math.Max(packed.Count, 1));
                    task.LogLine($"打包 {done}/{packed.Count}");
                }
            }

            task.Report(1.0);
            var sizeMb = new FileInfo(outPath).Length / 1048576.0;
            task.LogLine($"导出完成: {outPath} ({sizeMb:0.0} MB, 打包 {packed.Count} 个文件,在线匹配 {online.Count} 个)");
            return new ExportResult
            {
                Path = outPath,
                Matched = online.Count,
                Packed = packed.Count,
                SizeMb = sizeMb,
            };
        }
        catch
        {
            TryDelete(outPath); // 半截文件别留着误导人
            throw;
        }
    }

    /// <summary>算文件的 SHA1（小写十六进制）；读不了返回 null</summary>
    private static async Task<string?> Sha1OfAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var hash = await System.Security.Cryptography.SHA1.HashDataAsync(fs, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"算 {Path.GetFileName(path)} 的哈希失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 批量问 Modrinth「这些 SHA1 对应哪些版本文件」。
    /// 接口是 <c>POST /v2/version_files</c>，body 里塞哈希数组，返回 hash→version 的字典。
    /// 单批上限 1000，这里 500 一批稳一点。失败不致命（那批 mod 就照常打包）。
    /// </summary>
    private static async Task<Dictionary<string, OnlineMatch>> MatchModrinthAsync(
        List<string> hashes, TaskItem task, CancellationToken ct)
    {
        var result = new Dictionary<string, OnlineMatch>(StringComparer.OrdinalIgnoreCase);
        const int BatchSize = 500;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LargeHardLiteLauncher/0.1 (modpack export)");

        for (var offset = 0; offset < hashes.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = hashes.Skip(offset).Take(BatchSize).ToList();

            // 手拼 JSON：匿名类型序列化在 AOT 下要额外 metadata，不值得
            var sb = new StringBuilder("{\"hashes\":[");
            for (var i = 0; i < batch.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append('"').Append(batch[i]).Append('"');
            }
            sb.Append("],\"algorithm\":\"sha1\"}");

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.modrinth.com/v2/version_files")
                {
                    Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json"),
                };
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    // prop.Name 就是查进去的 sha1；值是 version 对象
                    if (prop.Value.ValueKind != JsonValueKind.Object
                        || !prop.Value.TryGetProperty("files", out var files)
                        || files.ValueKind != JsonValueKind.Array)
                        continue;

                    JsonElement? pick = null;
                    foreach (var f in files.EnumerateArray())
                    {
                        if (f.ValueKind != JsonValueKind.Object)
                            continue;
                        pick ??= f;
                        if (f.TryGetProperty("primary", out var pr) && pr.ValueKind == JsonValueKind.True)
                        {
                            pick = f;
                            break;
                        }
                    }
                    if (pick is not { } pf)
                        continue;

                    var url = Str(pf, "url");
                    if (string.IsNullOrEmpty(url))
                        continue;

                    string? sha512 = null;
                    if (pf.TryGetProperty("hashes", out var hs) && hs.ValueKind == JsonValueKind.Object)
                        sha512 = Str(hs, "sha512");

                    result[prop.Name] = new OnlineMatch
                    {
                        Url = url!,
                        Sha512 = sha512,
                        Size = Long(pf, "size"),
                    };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                task.LogLine($"[在线匹配失败] {Net.Brief(ex)} ,本批 {batch.Count} 个 Mod 仍按文件打包");
            }
        }

        return result;
    }

    /// <summary>Modrinth 查回来的一个匹配结果</summary>
    private sealed class OnlineMatch
    {
        public string Url { get; init; } = "";
        public string? Sha512 { get; init; }
        public long Size { get; init; }
    }

    /// <summary>要写进 index.json 的一条在线文件</summary>
    private sealed class OnlineEntry
    {
        public string Rel { get; init; } = "";
        public string Sha1 { get; init; } = "";
        public string? Sha512 { get; init; }
        public string Url { get; init; } = "";
        public long Size { get; init; }
    }

    /// <summary>收集要打包的文件（相对路径 + 绝对路径）</summary>
    private static List<(string Full, string Rel)> CollectFiles(
        string gameDir, MinecraftVersion ver, TaskItem task, CancellationToken ct)
    {
        var result = new List<(string, string)>();
        if (!Directory.Exists(gameDir))
            return result;

        var selfJson = ver.Id + ".json";
        var selfJar = ver.Id + ".jar";

        foreach (var entry in Directory.EnumerateFileSystemEntries(gameDir))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);

            if (Directory.Exists(entry))
            {
                if (ExcludedDirs.Contains(name))
                    continue;
                // 版本目录自己（隔离模式下的 versions/<id> 里没有，这里只防万一）
                if (name.Equals("versions", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var f in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    // 禁用的 mod/资源包不带走（装了也不生效，白占体积）
                    if (f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        continue;
                    result.Add((f, Path.GetRelativePath(gameDir, f)));
                }
            }
            else
            {
                // 根目录的散文件：options.txt / servers.dat 要带，版本自己的 jar/json 不带
                if (name.Equals(selfJson, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(selfJar, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add((entry, name));
            }
        }

        task.LogLine($"收集完成: {result.Count} 个文件");
        return result;
    }

    private static string BuildIndexJson(
        string packName, string packVersion, string baseMc,
        string? loaderKind, string? loaderVersion, List<OnlineEntry> online)
    {
        static string Esc(string s) => s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n");

        var deps = new StringBuilder();
        deps.Append($"    \"minecraft\": \"{Esc(baseMc)}\"");
        if (loaderKind is not null && !string.IsNullOrEmpty(loaderVersion))
        {
            // mrpack 的键名：forge / neoforge / fabric-loader / quilt-loader
            var key = loaderKind switch
            {
                "fabric" => "fabric-loader",
                "quilt" => "quilt-loader",
                _ => loaderKind,
            };
            deps.Append($",\n    \"{key}\": \"{Esc(loaderVersion)}\"");
        }

        // files[]：在线匹配到的 mod，只记链接和哈希（安装时从 CDN 拉）
        var filesJson = new StringBuilder();
        if (online.Count > 0)
        {
            filesJson.Append("  \"files\": [\n");
            for (var i = 0; i < online.Count; i++)
            {
                var e = online[i];
                var hashes = new StringBuilder($"\"sha1\": \"{e.Sha1}\"");
                if (!string.IsNullOrEmpty(e.Sha512))
                    hashes.Append($", \"sha512\": \"{e.Sha512}\"");
                filesJson.Append("    {\n");
                filesJson.Append($"      \"path\": \"{Esc(e.Rel)}\",\n");
                filesJson.Append($"      \"hashes\": {{ {hashes} }},\n");
                filesJson.Append($"      \"downloads\": [\"{Esc(e.Url)}\"],\n");
                filesJson.Append($"      \"fileSize\": {e.Size}\n");
                filesJson.Append(i == online.Count - 1 ? "    }\n" : "    },\n");
            }
            filesJson.Append("  ],\n");
        }
        else
        {
            filesJson.Append("  \"files\": [],\n");
        }

        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"formatVersion\": 1,\n");
        sb.Append("  \"game\": \"minecraft\",\n");
        sb.Append($"  \"versionId\": \"{Esc(packVersion)}\",\n");
        sb.Append($"  \"name\": \"{Esc(packName)}\",\n");
        sb.Append("  \"summary\": \"由 LargeHardLiteLauncher 导出的整合包\",\n");
        sb.Append(filesJson);
        sb.Append("  \"dependencies\": {\n");
        sb.Append(deps);
        sb.Append("\n  }\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>从 libraries 里掰出加载器种类 + 版本号</summary>
    internal static (string? Kind, string? Version) LoaderInfoOf(MinecraftVersion ver)
    {
        var loader = GameLauncher.DetectLoader(ver);
        if (loader is null)
            return (null, null);

        foreach (var lib in ver.Libraries)
        {
            var parts = (lib.Name ?? "").Split(':');
            if (parts.Length < 3)
                continue;
            if (loader == "forge" && parts[1] is "forge" or "fmlloader")
                return ("forge", TrimMcPrefix(parts[2]));
            if (loader == "neoforge" && parts[1] is "neoforge" or "fmlloader")
                return ("neoforge", TrimMcPrefix(parts[2]));
            if (loader == "fabric" && parts[1] == "fabric-loader")
                return ("fabric", parts[2]);
            if (loader == "quilt" && parts[1] == "quilt-loader")
                return ("quilt", parts[2]);
        }
        return (loader, null);
    }

    /// <summary>Forge 的库版本形如 1.20.1-47.4.28，导出只留 47.4.28</summary>
    private static string TrimMcPrefix(string version)
    {
        var dash = version.IndexOf('-');
        return dash > 0 && version[..dash].Count(c => c == '.') >= 1 ? version[(dash + 1)..] : version;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* 删不掉不影响 */ }
    }

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
