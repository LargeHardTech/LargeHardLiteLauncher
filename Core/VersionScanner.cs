using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>一个库条目</summary>
public sealed class LibEntry
{
    public string Name { get; init; } = "";
    /// <summary>artifact 相对 libraries 的路径</summary>
    public string? ArtifactPath { get; init; }
    /// <summary>natives-windows 分类器路径</summary>
    public string? NativesWindowsPath { get; init; }
    /// <summary>是否适用于 Windows (rules 过滤后)</summary>
    public bool AppliesToWindows { get; init; } = true;

    // ---- 补全版本要用: json 里给了就照抄, 没给就按 maven 坐标和源推导 ----
    /// <summary>artifact 的下载地址 (官方源)</summary>
    public string? Url { get; init; }
    public string? Sha1 { get; init; }
    public long Size { get; init; }
    /// <summary>natives jar 的下载地址 (缺失时靠它补)</summary>
    public string? NativesUrl { get; init; }
    public string? NativesSha1 { get; init; }
    public long NativesSize { get; init; }
}

/// <summary>本地已安装的游戏版本</summary>
public sealed class MinecraftVersion
{
    public string Id { get; init; } = "";
    /// <summary>版本目录 (versions/{id})</summary>
    public string Dir { get; init; } = "";
    public string? InheritsFrom { get; init; }
    public string? MainClass { get; set; }
    public string? AssetIndexId { get; set; }
    /// <summary>需求 Java 主版本 (json 的 javaVersion.majorVersion)</summary>
    public int RequiredJava { get; set; }
    public string Type { get; set; } = "release";
    public List<LibEntry> Libraries { get; } = new();
    /// <summary>client jar 路径</summary>
    public string? ClientJar { get; set; }

    // ---- 补全版本要用的下载信息 (json 的 downloads.client / assetIndex) ----
    public string? ClientJarUrl { get; set; }
    public string? ClientJarSha1 { get; set; }
    public long ClientJarSize { get; set; }
    public string? AssetIndexUrl { get; set; }
    public string? AssetIndexSha1 { get; set; }

    /// <summary>arguments.jvm 模板 (rules 已按本机过滤, 保留 ${} 占位符)</summary>
    public List<string> JvmTemplate { get; } = new();
    /// <summary>arguments.game 模板 (rules 已按本机过滤, 保留 ${} 占位符)</summary>
    public List<string> GameTemplate { get; } = new();
    /// <summary>老格式 (1.12 及以前) 的整串 minecraftArguments</summary>
    public string? LegacyArguments { get; set; }
    /// <summary>
    /// 版本 json 的 <c>assets</c> 字段，已废弃：1.18+ 的值等于 assetIndex 的 id（比如 "5"），
    /// 只有 1.6/1.7 才是 "virtual" / "map_to_resources"。判断虚拟资源要读 assetIndex 里的 virtual。
    /// </summary>
    public string? AssetsVirtual { get; set; }

    /// <summary>是否用新版 arguments 模板 (false = 老格式, 需自行拼游戏参数)</summary>
    public bool HasArgumentTemplate => GameTemplate.Count > 0 || JvmTemplate.Count > 0;

    public string Display => $"{Id}  [{Type}]";
}

/// <summary>
/// 扫描 .minecraft/versions, 解析版本 json (含 inheritsFrom 单层继承合并)。
/// 用 JsonDocument 手工解析, NativeAOT 安全。
/// </summary>
public static class VersionScanner
{
    public static List<MinecraftVersion> Scan(string mcDir)
    {
        var result = new List<MinecraftVersion>();
        var versionsDir = Path.Combine(mcDir, "versions");
        if (!Directory.Exists(versionsDir))
            return result;

        foreach (var dir in Directory.EnumerateDirectories(versionsDir))
        {
            var id = Path.GetFileName(dir);
            var json = Path.Combine(dir, id + ".json");
            if (!File.Exists(json))
                continue;
            try
            {
                var v = ParseOne(id, dir, json);
                if (v is not null)
                    result.Add(ResolveInheritance(v, versionsDir));
            }
            catch (Exception ex)
            {
                LauncherLog.Warn($"解析版本 {id} 失败: {ex.Message}");
            }
        }

        // 排序: 正式版在前 → 按**基础 MC 版本**数值降序 (1.21 > 1.20.1 > 1.9.4,
        // 不能用 id 字符串降序, "1.9.4" > "1.20.1" 会把老版本排到最上面)
        // → 同一 MC 版本内原版在前、加载器版本随后
        var baseOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in result)
            baseOf[v.Id] = VersionHelper.ResolveBaseVersion(v, result);

        return result
            .OrderByDescending(v => v.Type == "release")
            .ThenByDescending(v => baseOf[v.Id], Comparer<string>.Create(VersionHelper.CompareVersionDesc))
            .ThenBy(v => v.InheritsFrom is null ? 0 : 1)
            .ThenByDescending(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MinecraftVersion? ParseOne(string id, string dir, string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement;
        var v = new MinecraftVersion
        {
            Id = id,
            Dir = dir,
            InheritsFrom = GetString(root, "inheritsFrom"),
            MainClass = GetString(root, "mainClass"),
            Type = GetString(root, "type") ?? "release",
            ClientJar = Path.Combine(dir, id + ".jar"),
        };

        if (root.TryGetProperty("assetIndex", out var ai))
        {
            v.AssetIndexId = GetString(ai, "id");
            v.AssetIndexUrl = GetString(ai, "url");
            v.AssetIndexSha1 = GetString(ai, "sha1");
        }

        // client jar 的下载信息 (补全版本时要靠它补)
        if (root.TryGetProperty("downloads", out var dl) && dl.TryGetProperty("client", out var client))
        {
            v.ClientJarUrl = GetString(client, "url");
            v.ClientJarSha1 = GetString(client, "sha1");
            v.ClientJarSize = GetLong(client, "size");
        }
        if (root.TryGetProperty("javaVersion", out var jv) &&
            jv.TryGetProperty("majorVersion", out var major) && major.TryGetInt32(out var m))
            v.RequiredJava = m;

        if (root.TryGetProperty("assets", out var ast) && ast.ValueKind == JsonValueKind.String)
            v.AssetsVirtual = ast.GetString();

        ParseLibraries(root, v);
        ParseArguments(root, v);
        return v;
    }

    private static void ParseLibraries(JsonElement root, MinecraftVersion v)
    {
        if (!root.TryGetProperty("libraries", out var libs) || libs.ValueKind != JsonValueKind.Array)
            return;

        foreach (var lib in libs.EnumerateArray())
        {
            var name = GetString(lib, "name") ?? "";
            if (name.Length == 0)
                continue;

            bool applies = EvaluateRules(lib);
            string? artifact = null, natives = null;
            string? artUrl = null, artSha1 = null, natUrl = null, natSha1 = null;
            long artSize = 0, natSize = 0;
            var libBaseUrl = GetString(lib, "url"); // Fabric 风格: 顶层给 maven 根地址

            if (lib.TryGetProperty("downloads", out var dl))
            {
                if (dl.TryGetProperty("artifact", out var art))
                {
                    artifact = GetString(art, "path");
                    artUrl = GetString(art, "url");
                    artSha1 = GetString(art, "sha1");
                    artSize = GetLong(art, "size");
                }
                if (dl.TryGetProperty("classifiers", out var cls) &&
                    cls.TryGetProperty("natives-windows", out var nat))
                {
                    natives = GetString(nat, "path");
                    natUrl = GetString(nat, "url");
                    natSha1 = GetString(nat, "sha1");
                    natSize = GetLong(nat, "size");
                }
                else if (name.Contains(":natives-windows", StringComparison.OrdinalIgnoreCase))
                {
                    // 新格式 (MC 26.x): classifier 直接拼在 name 第 4 段, artifact 就是这个 native jar。
                    // 坑: 同一条 dll 有 natives-windows / -x86 / -arm64 三个包, 而且 rules 都只写
                    // os.name=windows (不区分架构), 全收的话 x86 的 dll 会覆盖 x64 的 →
                    // LWJGL 报 "Platform/architecture mismatch detected for module: org.lwjgl"
                    var segs = name.Split(':');
                    var classifier = segs.Length > 3 ? segs[3] : "";
                    if (NativesMatchesArch(classifier))
                    {
                        natives = artifact;
                        natUrl = artUrl;
                        natSha1 = artSha1;
                        natSize = artSize;
                    }

                    // natives jar 一律不进 classpath (它在 native 目录里已解压,
                    // 留在 classpath 反而会让 LWJGL 从里面提取错架构的库)
                    artifact = null;
                    artUrl = null;
                    artSha1 = null;
                    artSize = 0;
                }
            }

            // Fabric / 部分第三方 json 只给 maven 坐标不给 downloads.artifact,
            // 不推导的话这些库进不了 classpath → 主类 ClassNotFoundException
            if (artifact is null && natives is null)
            {
                artifact = MavenPath(name);
                if (artifact is not null)
                    artUrl = GuessLibUrl(name, artifact, libBaseUrl);
            }

            v.Libraries.Add(new LibEntry
            {
                Name = name,
                ArtifactPath = artifact,
                NativesWindowsPath = natives,
                AppliesToWindows = applies,
                Url = artUrl,
                Sha1 = artSha1,
                Size = artSize,
                NativesUrl = natUrl,
                NativesSha1 = natSha1,
                NativesSize = natSize,
            });
        }
    }

    /// <summary>
    /// 解析启动参数模板: 新版 arguments.jvm / arguments.game, 或老版 minecraftArguments 字符串。
    /// rules 在这里就过滤掉 (只留本机适用的), 占位符原样保留给启动时替换。
    /// </summary>
    private static void ParseArguments(JsonElement root, MinecraftVersion v)
    {
        if (root.TryGetProperty("minecraftArguments", out var legacy) &&
            legacy.ValueKind == JsonValueKind.String)
            v.LegacyArguments = legacy.GetString();

        if (!root.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Object)
            return;

        CollectArgs(args, "jvm", v.JvmTemplate);
        CollectArgs(args, "game", v.GameTemplate);
    }

    /// <summary>展开 arguments 里的一个数组: 元素可能是裸字符串, 也可能是 {rules, value}</summary>
    private static void CollectArgs(JsonElement args, string key, List<string> target)
    {
        if (!args.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;

        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.String)
            {
                AddArg(target, e.GetString());
                continue;
            }
            if (e.ValueKind != JsonValueKind.Object)
                continue;
            if (!JsonRules.Allow(e))
                continue;
            if (!e.TryGetProperty("value", out var val))
                continue;

            if (val.ValueKind == JsonValueKind.String)
            {
                AddArg(target, val.GetString());
            }
            else if (val.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in val.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                        AddArg(target, item.GetString());
            }
        }
    }

    private static void AddArg(List<string> target, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            target.Add(value);
    }

    /// <summary>
    /// natives 分类器是否匹配当前进程架构。
    /// LWJGL 的命名: natives-windows (x64) / natives-windows-x86 / natives-windows-arm64,
    /// 无后缀代表 64 位 x86, 32 位进程不能拿, 反之亦然。
    /// </summary>
    private static bool NativesMatchesArch(string classifier)
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;

        if (classifier.Contains("arm64", StringComparison.OrdinalIgnoreCase))
            return arch == System.Runtime.InteropServices.Architecture.Arm64;
        if (classifier.EndsWith("-x86", StringComparison.OrdinalIgnoreCase))
            return arch == System.Runtime.InteropServices.Architecture.X86;
        if (classifier.EndsWith("-x64", StringComparison.OrdinalIgnoreCase))
            return arch == System.Runtime.InteropServices.Architecture.X64;

        // 无架构后缀: 64 位包
        return arch is System.Runtime.InteropServices.Architecture.X64
                   or System.Runtime.InteropServices.Architecture.X86;
    }

    /// <summary>
    /// maven 坐标 → libraries 下的相对路径。
    /// 格式 group:artifact:version[:classifier] → group/path/artifact/version/artifact-version[-classifier].jar
    /// </summary>
    private static string? MavenPath(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
            return null;

        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? parts[3] : null;

        // natives-xxx 分类器是原生库, 不进 classpath
        if (classifier is not null && classifier.StartsWith("natives", StringComparison.OrdinalIgnoreCase))
            return null;

        var file = classifier is null
            ? $"{artifact}-{version}.jar"
            : $"{artifact}-{version}-{classifier}.jar";

        return $"{group.Replace('.', '/')}/{artifact}/{version}/{file}";
    }

    /// <summary>rules 过滤: 统一走 JsonRules (libraries 和 arguments 共用同一套语义)</summary>
    private static bool EvaluateRules(JsonElement lib) => JsonRules.Allow(lib);

    /// <summary>
    /// 合并父版本 (inheritsFrom), 支持多层继承 (Forge → 1.20.1 → 无)。
    /// 顺序遵循官方规范: 父在前 + 子在后, 子版本的库/参数可以覆盖或追加父的。
    /// </summary>
    private static MinecraftVersion ResolveInheritance(MinecraftVersion child, string versionsDir)
        => ResolveInheritance(child, versionsDir,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { child.Id });

    private static MinecraftVersion ResolveInheritance(MinecraftVersion child, string versionsDir, HashSet<string> visited)
    {
        var parentId = child.InheritsFrom;
        if (string.IsNullOrEmpty(parentId))
            return child;

        if (!visited.Add(parentId!))
        {
            LauncherLog.Warn($"版本 {child.Id} 的继承链存在循环 ({parentId}),已忽略该层");
            return child;
        }

        var parentDir = Path.Combine(versionsDir, parentId!);
        var parentJson = Path.Combine(parentDir, parentId + ".json");
        if (!File.Exists(parentJson))
        {
            LauncherLog.Warn($"版本 {child.Id} 的父版本 {parentId} 未安装,只能按自己的 json 启动");
            return child;
        }

        try
        {
            var parent = ParseOne(parentId!, parentDir, parentJson);
            if (parent is null)
                return child;
            // 父版本自己可能也 inheritsFrom (Forge → 原版)
            parent = ResolveInheritance(parent, versionsDir, visited);

            // ---- 标量: 子没有才继承 ----
            child.MainClass ??= parent.MainClass;
            child.AssetIndexId ??= parent.AssetIndexId;
            child.AssetIndexUrl ??= parent.AssetIndexUrl;
            child.AssetIndexSha1 ??= parent.AssetIndexSha1;
            if (string.IsNullOrEmpty(child.AssetsVirtual))
                child.AssetsVirtual = parent.AssetsVirtual;
            if (child.RequiredJava == 0)
                child.RequiredJava = parent.RequiredJava;

            // 参数模板格式互斥: 新版 arguments 和老版 minecraftArguments 不能混拼
            if (child.GameTemplate.Count == 0 && string.IsNullOrEmpty(child.LegacyArguments))
                child.LegacyArguments = parent.LegacyArguments;

            // Forge 的版本目录里通常没有 client jar, 用父版本的
            // (补全时也要能拿到父版本的下载地址, 否则补不了)
            if (!File.Exists(child.ClientJar) && File.Exists(parent.ClientJar))
            {
                child.ClientJar = parent.ClientJar;
                child.ClientJarUrl ??= parent.ClientJarUrl;
                child.ClientJarSha1 ??= parent.ClientJarSha1;
                if (child.ClientJarSize == 0)
                    child.ClientJarSize = parent.ClientJarSize;
            }

            // ---- libraries: 父在前, 同名（完全一致）用子版本的 ----
            if (parent.Libraries.Count > 0)
            {
                var childLibs = child.Libraries.ToList();
                var owned = new HashSet<string>(childLibs.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
                child.Libraries.Clear();
                foreach (var pl in parent.Libraries)
                {
                    if (!owned.Contains(pl.Name))
                        child.Libraries.Add(pl);
                }
                child.Libraries.AddRange(childLibs);
            }

            // ---- 参数模板: 父在前 + 子追加 (顺序有意义, 绝不按内容去重) ----
            if (parent.JvmTemplate.Count > 0)
            {
                var mine = child.JvmTemplate.ToList();
                child.JvmTemplate.Clear();
                child.JvmTemplate.AddRange(parent.JvmTemplate);
                child.JvmTemplate.AddRange(mine);
            }
            if (parent.GameTemplate.Count > 0)
            {
                var mine = child.GameTemplate.ToList();
                child.GameTemplate.Clear();
                child.GameTemplate.AddRange(parent.GameTemplate);
                child.GameTemplate.AddRange(mine);
            }
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"合并父版本 {parentId} 失败: {ex.Message}");
        }

        return child;
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>
    /// 没有 <c>downloads.artifact.url</c> 的库（Fabric 和部分第三方整合包常见），
    /// 按 maven 坐标 + 顶层 <c>url</c>（或 group 归属）拼下载地址。
    /// </summary>
    private static string? GuessLibUrl(string name, string artifactPath, string? libBaseUrl)
    {
        if (!string.IsNullOrEmpty(libBaseUrl))
            return libBaseUrl.TrimEnd('/') + "/" + artifactPath;

        var group = name.Split(':')[0];
        if (group.StartsWith("net.minecraftforge", StringComparison.OrdinalIgnoreCase)
            || group.StartsWith("cpw.mods", StringComparison.OrdinalIgnoreCase))
            return "https://maven.minecraftforge.net/" + artifactPath;
        if (group.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase))
            return "https://maven.neoforged.net/releases/" + artifactPath;
        if (group.StartsWith("org.quiltmc", StringComparison.OrdinalIgnoreCase)
            || group.StartsWith("net.fabricmc", StringComparison.OrdinalIgnoreCase))
            return "https://maven.fabricmc.net/" + artifactPath;
        return "https://libraries.minecraft.net/" + artifactPath;
    }
}
