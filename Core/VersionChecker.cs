using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>补全版本的结果：要下什么、缺多少、给 UI 看的摘要</summary>
public sealed class RepairPlan
{
    /// <summary>完整文件清单（已存在的会被下载器跳过，所以这里给全量最省事）</summary>
    public List<DownloadFile> Files { get; } = new();

    public int JarMissing { get; set; }
    public int LibMissing { get; set; }
    public int NativesMissing { get; set; }
    public int IndexMissing { get; set; }
    public int AssetMissing { get; set; }

    /// <summary>检查过多少个文件（本地 + 缺失）</summary>
    public int Checked { get; set; }

    /// <summary>前几条缺失项的说明，UI 里列出来让用户知道缺什么</summary>
    public List<string> Samples { get; } = new();

    public int TotalMissing =>
        JarMissing + LibMissing + NativesMissing + IndexMissing + AssetMissing;

    public bool HasProblems => TotalMissing > 0;

    public string Summary
    {
        get
        {
            if (!HasProblems)
                return $"文件完整 ({Checked} 项全部就位)";
            var parts = new List<string>();
            if (JarMissing > 0) parts.Add($"客户端 jar {JarMissing}");
            if (LibMissing > 0) parts.Add($"库 {LibMissing}");
            if (NativesMissing > 0) parts.Add($"natives {NativesMissing}");
            if (IndexMissing > 0) parts.Add($"资源索引 {IndexMissing}");
            if (AssetMissing > 0) parts.Add($"资源文件 {AssetMissing}");
            return $"缺失 {TotalMissing} 项({string.Join(" / ", parts)})";
        }
    }
}

/// <summary>
/// 版本补全（学 PCL 的「补全版本」）：拿本地已解析的版本 json 逐个核对文件，
/// 缺什么列什么，然后把完整清单交给 <see cref="GameDownloader.DownloadAsync"/> 下缺的。
///
/// 为什么不用 <see cref="GameDownloader.BuildPlanAsync"/>：它要重新拉远端 json 并覆盖本地，
/// 而且遇到 inheritsFrom 直接抛异常, 加载器版本正是要补的对象。
/// </summary>
public static class VersionChecker
{
    private const string AssetObjectsBase = "https://resources.download.minecraft.net";

    /// <summary>
    /// 检查并生成补全清单。<paramref name="checkAssets"/> = false 时跳过资源文件逐个核对
    /// （3000+ 次 File.Exists，机械硬盘上要几秒）。
    /// </summary>
    public static RepairPlan Build(LauncherConfig cfg, MinecraftVersion v, bool checkAssets = true)
    {
        var plan = new RepairPlan();
        var mcDir = cfg.MinecraftPath;
        var libRoot = Path.Combine(mcDir, "libraries");
        var source = cfg.DownloadSource;

        string Prime(string officialUrl) => GameDownloader.Mirror(officialUrl, source);
        string? Alt(string officialUrl) =>
            source == "auto" && Prime(officialUrl) != officialUrl ? officialUrl : null;

        void AddFile(string url, string localPath, string? sha1, long size)
        {
            if (string.IsNullOrEmpty(url))
                return;
            plan.Files.Add(new DownloadFile
            {
                Url = Prime(url),
                FallbackUrl = Alt(url),
                Path = localPath,
                Sha1 = sha1,
                Size = size,
            });
        }

        void Miss(string kind, int weight, string desc)
        {
            plan.Checked++;
            if (plan.Samples.Count < 10)
                plan.Samples.Add($"[{kind}] {desc}");
            switch (kind)
            {
                case "jar": plan.JarMissing += weight; break;
                case "库": plan.LibMissing += weight; break;
                case "natives": plan.NativesMissing += weight; break;
                case "索引": plan.IndexMissing += weight; break;
                case "资源": plan.AssetMissing += weight; break;
            }
        }

        // ---- 1) 客户端 jar ----
        if (!string.IsNullOrEmpty(v.ClientJar))
        {
            var exists = File.Exists(v.ClientJar);
            if (!exists)
                Miss("jar", 1, Path.GetFileName(v.ClientJar));
            else
                plan.Checked++;
            AddFile(v.ClientJarUrl ?? "", v.ClientJar, v.ClientJarSha1, v.ClientJarSize);
        }

        // ---- 2) libraries (含 natives jar) ----
        foreach (var lib in v.Libraries)
        {
            if (!lib.AppliesToWindows)
                continue;

            if (!string.IsNullOrEmpty(lib.ArtifactPath))
            {
                var path = Path.Combine(libRoot, lib.ArtifactPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                    plan.Checked++;
                else
                    Miss("库", 1, Path.GetFileName(lib.ArtifactPath));
                AddFile(lib.Url ?? "", path, lib.Sha1, lib.Size);
            }

            if (!string.IsNullOrEmpty(lib.NativesWindowsPath))
            {
                var path = Path.Combine(libRoot, lib.NativesWindowsPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                    plan.Checked++;
                else
                    Miss("natives", 1, Path.GetFileName(lib.NativesWindowsPath));
                AddFile(lib.NativesUrl ?? lib.Url ?? "", path, lib.NativesSha1 ?? lib.Sha1, lib.NativesSize);
            }
        }

        // ---- 3) 资源索引 ----
        if (!string.IsNullOrEmpty(v.AssetIndexId))
        {
            var indexPath = Path.Combine(mcDir, "assets", "indexes", v.AssetIndexId + ".json");
            var exists = File.Exists(indexPath);
            if (!exists)
                Miss("索引", 1, v.AssetIndexId + ".json");
            else
                plan.Checked++;
            AddFile(v.AssetIndexUrl ?? "", indexPath, v.AssetIndexSha1, 0);

            // ---- 4) 资源文件（读 index 里的 objects 逐个核对）----
            // 不要用 v.AssetsVirtual 判断：那是版本 json 的 `assets` 字段，是个废弃字段，
            // 值就是 assetIndex 的 id（"5"），永远不等于 "virtual"。
            // 真正的虚拟资源标记在 assetIndex 文件自己的 virtual / map_to_resources 里。
            if (checkAssets && exists)
                CheckAssets(mcDir, indexPath, plan, Prime, Alt);
        }

        return plan;
    }

    private static void CheckAssets(string mcDir, string indexPath, RepairPlan plan,
        Func<string, string> prime, Func<string, string?> alt)
    {
        var objectsRoot = Path.Combine(mcDir, "assets", "objects");
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(indexPath));
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"资源索引读取失败 {indexPath}: {ex.Message}");
            return;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("objects", out var objs) || objs.ValueKind != JsonValueKind.Object)
                return;

            // 老版本 (1.6-1.7) 的资源是虚拟结构: assets/virtual/<index>/... 或 assets/resources/...
            // 路径规则不同，这里不逐项核对（补全也不该往 objects 里塞）
            if (IsVirtualIndex(doc.RootElement))
            {
                LauncherLog.Info($"资源索引 {Path.GetFileName(indexPath)} 是虚拟结构,跳过逐文件核对");
                return;
            }

            var missing = 0;
            foreach (var prop in objs.EnumerateObject())
            {
                var o = prop.Value;
                if (o.ValueKind != JsonValueKind.Object)
                    continue;
                var hash = o.TryGetProperty("hash", out var h) && h.ValueKind == JsonValueKind.String
                    ? h.GetString()
                    : null;
                if (string.IsNullOrEmpty(hash) || hash.Length < 4)
                    continue;
                var size = o.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number
                    && sz.TryGetInt64(out var n) ? n : 0;

                var rel = Path.Combine(hash[..2], hash);
                var local = Path.Combine(objectsRoot, rel);
                var url = $"{AssetObjectsBase}/{hash[..2]}/{hash}";

                plan.Checked++;
                if (!File.Exists(local))
                {
                    missing++;
                    if (plan.Samples.Count < 10)
                        plan.Samples.Add($"[资源] {prop.Name}");
                }
                plan.Files.Add(new DownloadFile
                {
                    Url = prime(url),
                    FallbackUrl = alt(url),
                    Path = local,
                    Sha1 = hash,
                    Size = size,
                });
            }
            plan.AssetMissing += missing;
        }
    }

    /// <summary>assetIndex 里声明了 virtual / map_to_resources（1.6-1.7 的老结构）</summary>
    private static bool IsVirtualIndex(JsonElement root) =>
        (root.TryGetProperty("virtual", out var v) && v.ValueKind == JsonValueKind.True)
        || (root.TryGetProperty("map_to_resources", out var m) && m.ValueKind == JsonValueKind.True);
}
