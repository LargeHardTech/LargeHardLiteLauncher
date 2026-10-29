using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>整合包里要下载的一个文件</summary>
public sealed class ModpackFile
{
    /// <summary>相对版本目录的路径（mods/xxx.jar、config/xxx.toml…）</summary>
    public string RelPath { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Sha1 { get; init; }
    public long Size { get; init; }
}

/// <summary>读出来的整合包信息（还没安装）</summary>
public sealed class ModpackInfo
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>依赖的基础 MC 版本</summary>
    public string McVersion { get; init; } = "";
    /// <summary>forge / neoforge / fabric / quilt，null = 纯原版</summary>
    public string? LoaderKind { get; init; }
    public string? LoaderVersion { get; init; }
    public string Format { get; init; } = "";
    public List<ModpackFile> Files { get; } = new();

    public string LoaderDisplay => LoaderKind is null ? "原版" : $"{LoaderKind} {LoaderVersion}";

    public string Summary =>
        $"{Name} {Version}  ·  MC {McVersion}  ·  {LoaderDisplay}  ·  {Files.Count} 个文件  ·  {Format}";
}

/// <summary>
/// 整合包安装（学 PCL 的整合包流程：基础原版 → 加载器 → overrides → 下载清单）。
///
/// 目前支持 Modrinth 的 <c>.mrpack</c>：内容自包含（每个文件都给了直链），不需要 API key。
/// CurseForge 的 zip 只有 projectID/fileID，要拿 key 调 API 才能换下载地址, 那种格式这里明确拒绝，
/// 别假装支持。
/// </summary>
public static class ModpackInstaller
{
    /// <summary>只读元数据，不安装。格式不认识就抛异常说清楚。</summary>
    public static ModpackInfo Read(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);

        var index = FindEntry(zip, "modrinth.index.json");
        if (index is not null)
            return ReadMrpack(zip, index, archivePath);

        var manifest = FindEntry(zip, "manifest.json");
        if (manifest is not null)
            return ReadCurseForge(zip, manifest, archivePath);

        throw new InvalidOperationException(
            "这不像一个整合包:压缩包里既没有 modrinth.index.json 也没有 manifest.json。\n" +
            "如果是从别处下的 zip,它可能是普通的资源整合(mods 里手动塞就行),不用走整合包安装。");
    }

    private static ModpackInfo ReadMrpack(ZipArchive zip, ZipArchiveEntry index, string archivePath)
    {
        string text;
        using (var s = index.Open())
        using (var r = new StreamReader(s))
            text = r.ReadToEnd();

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        var name = Str(root, "name") ?? Path.GetFileNameWithoutExtension(archivePath);
        var version = Str(root, "versionId") ?? "";

        string mc = "";
        string? loaderKind = null, loaderVersion = null;
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
        {
            mc = Str(deps, "minecraft") ?? "";
            foreach (var (key, kind) in new[]
                     {
                         ("forge", "forge"),
                         ("neoforge", "neoforge"),
                         ("fabric-loader", "fabric"),
                         ("quilt-loader", "quilt"),
                     })
            {
                var v = Str(deps, key);
                if (!string.IsNullOrEmpty(v))
                {
                    loaderKind = kind;
                    loaderVersion = v;
                    break;
                }
            }
        }

        var files = new List<ModpackFile>();
        if (root.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in arr.EnumerateArray())
            {
                var path = Str(f, "path");
                if (string.IsNullOrEmpty(path))
                    continue;

                // downloads 是个数组，第一个能用的就行
                string url = "";
                if (f.TryGetProperty("downloads", out var dl) && dl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var u in dl.EnumerateArray())
                    {
                        if (u.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(u.GetString()))
                        {
                            url = u.GetString()!;
                            break;
                        }
                    }
                }
                if (url.Length == 0)
                    continue;

                string? sha1 = null;
                if (f.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object)
                    sha1 = Str(hashes, "sha1");

                files.Add(new ModpackFile
                {
                    RelPath = path.Replace('/', Path.DirectorySeparatorChar),
                    Url = url,
                    Sha1 = sha1,
                    Size = Long(f, "fileSize"),
                });
            }
        }

        var info = new ModpackInfo
        {
            Name = name,
            Version = version,
            McVersion = mc,
            LoaderKind = loaderKind,
            LoaderVersion = loaderVersion,
            Format = "Modrinth (.mrpack)",
        };
        info.Files.AddRange(files);
        return info;
    }

    /// <summary>
    /// CurseForge 格式：只能读出名字/版本/加载器，文件列表需要 API key 换地址。
    /// 这里读出来是为了给用户一个明确提示，而不是"装到一半失败"。
    /// </summary>
    private static ModpackInfo ReadCurseForge(ZipArchive zip, ZipArchiveEntry manifest, string archivePath)
    {
        string text;
        using (var s = manifest.Open())
        using (var r = new StreamReader(s))
            text = r.ReadToEnd();

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        var name = Str(root, "name") ?? Path.GetFileNameWithoutExtension(archivePath);
        var version = Str(root, "version") ?? "";

        string mc = "";
        string? loaderKind = null, loaderVersion = null;
        if (root.TryGetProperty("minecraft", out var mcv) && mcv.ValueKind == JsonValueKind.Object)
        {
            mc = Str(mcv, "version") ?? "";
            if (mcv.TryGetProperty("modLoaders", out var loaders) && loaders.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in loaders.EnumerateArray())
                {
                    var id = Str(l, "id") ?? "";
                    var dash = id.IndexOf('-');
                    if (dash <= 0)
                        continue;
                    loaderKind = id[..dash].ToLowerInvariant() switch
                    {
                        "forge" => "forge",
                        "neoforge" => "neoforge",
                        "fabric" => "fabric",
                        "quilt" => "quilt",
                        _ => null,
                    };
                    if (loaderKind is not null)
                    {
                        loaderVersion = id[(dash + 1)..];
                        break;
                    }
                }
            }
        }

        return new ModpackInfo
        {
            Name = name,
            Version = version,
            McVersion = mc,
            LoaderKind = loaderKind,
            LoaderVersion = loaderVersion,
            Format = "CurseForge (.zip)",
        };
    }

    /// <summary>
    /// 真正安装：基础原版校验 → 加载器 → 版本 json → overrides → 下载文件。
    /// 返回最终版本名（= instanceName）。
    /// </summary>
    public static async Task<string> InstallAsync(
        string archivePath, ModpackInfo pack, string mcDir, string instanceName,
        string downloadSource, TaskItem task, CancellationToken ct)
    {
        if (pack.Format.StartsWith("CurseForge", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "CurseForge 格式的整合包需要 API key 才能换出每个 mod 的下载地址,这里还没做。\n" +
                "可以在 CurseForge 页面上手动下 mods,或者用 Modrinth 上同名整合包的 .mrpack。");

        if (string.IsNullOrEmpty(pack.McVersion))
            throw new InvalidOperationException("整合包没声明依赖的 MC 版本,装不了。");

        // ---- 1) 基础原版必须在（我们不自动下载原版，避免和用户已装的版本打架）----
        var baseJson = Path.Combine(mcDir, "versions", pack.McVersion, pack.McVersion + ".json");
        if (!File.Exists(baseJson))
            throw new InvalidOperationException(
                $"这个整合包需要 MC {pack.McVersion},但本地没装。\n" +
                $"先到[下载 → 原版游戏]里装一个 {pack.McVersion},再回来装整合包。");

        task.LogLine($"基础版本: {pack.McVersion} ");
        task.Report(0.05);

        // ---- 2) 加载器（版本名沿用官方命名，已存在就跳过）----
        var parentId = pack.McVersion;
        if (pack.LoaderKind is not null && !string.IsNullOrEmpty(pack.LoaderVersion))
        {
            var kind = MapLoader(pack.LoaderKind);
            var loaderName = DefaultLoaderName(kind, pack.LoaderVersion, pack.McVersion);
            var loaderJson = Path.Combine(mcDir, "versions", loaderName, loaderName + ".json");

            if (File.Exists(loaderJson))
            {
                task.LogLine($"加载器已存在: {loaderName}");
            }
            else
            {
                task.LogLine($"安装加载器 {pack.LoaderKind} {pack.LoaderVersion}...");
                if (kind is LoaderKind.Fabric or LoaderKind.Quilt)
                    await LoaderInstaller.InstallJsonLoaderAsync(kind, pack.McVersion, pack.LoaderVersion,
                        mcDir, task, ct).ConfigureAwait(false);
                else
                    await LoaderInstaller.InstallByInstallerAsync(kind, pack.McVersion, pack.LoaderVersion,
                        mcDir, task, ct).ConfigureAwait(false);
            }
            parentId = loaderName;
        }
        task.Report(0.35);

        // ---- 3) 版本目录 + json（继承加载器版本，什么都不要自己写）----
        var verDir = Path.Combine(mcDir, "versions", instanceName);
        if (Directory.Exists(verDir) && File.Exists(Path.Combine(verDir, instanceName + ".json")))
            throw new InvalidOperationException($"版本 {instanceName} 已经存在,换个名字。");

        Directory.CreateDirectory(verDir);
        var jsonPath = Path.Combine(verDir, instanceName + ".json");
        await File.WriteAllTextAsync(jsonPath, BuildVersionJson(instanceName, parentId), ct).ConfigureAwait(false);
        task.LogLine($"版本 json: {jsonPath} (inheritsFrom {parentId})");
        task.Report(0.4);

        // ---- 4) overrides（整合包自带的配置/资源/预置文件）----
        var overrides = ExtractOverrides(archivePath, verDir, task, ct);
        task.LogLine($"overrides: {overrides} 个文件");
        task.Report(overrides > 0 ? 0.55 : 0.45);

        // ---- 5) 文件清单 ----
        if (pack.Files.Count > 0)
        {
            var downloads = pack.Files
                .Select(f => new DownloadFile
                {
                    Url = GameDownloader.Mirror(f.Url, downloadSource),
                    FallbackUrl = downloadSource == "auto" ? f.Url : null,
                    Path = Path.Combine(verDir, f.RelPath),
                    Sha1 = f.Sha1,
                    Size = f.Size,
                })
                .ToList();
            task.LogLine($"下载 {downloads.Count} 个文件...");
            await GameDownloader.DownloadAsync(downloads, task, ct).ConfigureAwait(false);
        }
        else
        {
            task.LogLine("这个整合包没有额外的下载文件(只有 overrides)");
        }

        task.Report(1.0);
        task.LogLine($"整合包 {pack.Name} 安装完成 → {instanceName}");
        return instanceName;
    }

    /// <summary>版本 json：只有 id + inheritsFrom，其余全部从加载器/原版继承</summary>
    private static string BuildVersionJson(string id, string parentId)
    {
        // 手写转义，不为一个 json 引 STJ 序列化元数据（AOT 体积）
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"id\": \"{Esc(id)}\",\n");
        sb.Append($"  \"inheritsFrom\": \"{Esc(parentId)}\",\n");
        sb.Append("  \"type\": \"release\",\n");
        sb.Append("  \"libraries\": [],\n");
        sb.Append("  \"arguments\": { \"game\": [], \"jvm\": [] }\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>把 zip 里 overrides/ 的东西铺到版本目录</summary>
    private static int ExtractOverrides(string archivePath, string verDir, TaskItem task, CancellationToken ct)
    {
        const string prefix = "overrides/";
        var count = 0;
        var rootFull = Path.GetFullPath(verDir);

        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var rel = entry.FullName[prefix.Length..];
            if (rel.Length == 0)
                continue;

            var target = Path.GetFullPath(Path.Combine(verDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            // 防路径穿越（zip 里写 ../../ 就能写到版本目录外面）
            if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                task.LogLine($"[跳过] 可疑路径: {entry.FullName}");
                continue;
            }

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target); // 目录项
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            count++;
        }
        return count;
    }

    private static LoaderKind MapLoader(string kind) => kind.ToLowerInvariant() switch
    {
        "forge" => LoaderKind.Forge,
        "neoforge" => LoaderKind.NeoForge,
        "fabric" => LoaderKind.Fabric,
        "quilt" => LoaderKind.Quilt,
        _ => LoaderKind.Vanilla,
    };

    /// <summary>和 UI 安装页用的命名保持一致</summary>
    private static string DefaultLoaderName(LoaderKind kind, string loaderVer, string baseMc) => kind switch
    {
        LoaderKind.Forge => $"{baseMc}-forge-{loaderVer}",
        LoaderKind.NeoForge => $"neoforge-{loaderVer}",
        LoaderKind.Fabric => $"fabric-loader-{loaderVer}-{baseMc}",
        LoaderKind.Quilt => $"quilt-loader-{loaderVer}-{baseMc}",
        _ => baseMc,
    };

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name) =>
        zip.GetEntry(name)
        ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
