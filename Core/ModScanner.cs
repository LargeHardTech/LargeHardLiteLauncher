using System.IO.Compression;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>本地 mods 目录里的一个 mod 文件</summary>
public sealed class ModInfo
{
    /// <summary>磁盘上的真实路径（禁用状态时带 .disabled 后缀）</summary>
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    /// <summary>去掉 .disabled/.old 后的名字，用作启用后的目标名（也是 UI 的多选 Key）</summary>
    public string EnabledName { get; init; } = "";
    public bool Enabled { get; init; }
    /// <summary>jar 里读出的显示名；读不到就用文件名</summary>
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string ModId { get; set; } = "";
    /// <summary>forge / neoforge / fabric / quilt / litemod / unknown</summary>
    public string Loader { get; set; } = "unknown";
    public string Description { get; set; } = "";
    public long Size { get; init; }
    public DateTime Modified { get; init; }

    /// <summary>列表显示：名字 + 版本 + 加载器 + 状态</summary>
    public string Display
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Name) ? EnabledName : Name;
            var ver = Version.Length > 0 ? " " + Version : "";
            var loader = Loader == "unknown" ? "" : $"  [{Loader}]";
            var state = Enabled ? "" : "  (已禁用)";
            return $"{name}{ver}{loader}{state}";
        }
    }
}

/// <summary>
/// 本地 mods 扫描 + 启用/禁用。启用状态靠**文件名后缀**表示（学 PCL2）：
/// <c>xxx.jar</c> 启用，<c>xxx.jar.disabled</c> 禁用，<c>xxx.jar.old</c> 老备份。
/// </summary>
public static class ModScanner
{
    private static readonly string[] EnabledExt = { ".jar", ".litemod" };
    private const string DisabledSuffix = ".disabled";
    private const string OldSuffix = ".old";

    /// <summary>元数据缓存: key = 完整路径 + 修改时间，避免每次进页面都重读几百个 jar</summary>
    private static readonly Dictionary<string, (DateTime Stamp, long Size, ModInfo Info)> Cache = new();

    /// <summary>扫描目录（后台线程调用；内部只读文件，不碰 UI）</summary>
    public static List<ModInfo> Scan(string modsDir)
    {
        var result = new List<ModInfo>();
        if (!Directory.Exists(modsDir))
            return result;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(modsDir);
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"扫描 mods 目录失败: {ex.Message}");
            return result;
        }

        foreach (var path in files)
        {
            var fileName = Path.GetFileName(path);
            var enabled = false;
            string enabledName;

            if (EndsWith(fileName, DisabledSuffix) || EndsWith(fileName, OldSuffix))
            {
                enabledName = fileName[..^SuffixLen(fileName)];
                enabled = false;
            }
            else if (EnabledExt.Any(e => EndsWith(fileName, e)))
            {
                enabledName = fileName;
                enabled = true;
            }
            else
            {
                continue; // 不是 mod 文件
            }

            FileInfo fi;
            try
            {
                fi = new FileInfo(path);
                if (!fi.Exists)
                    continue;
            }
            catch
            {
                continue;
            }

            var info = new ModInfo
            {
                FilePath = path,
                FileName = fileName,
                EnabledName = enabledName,
                Enabled = enabled,
                Size = fi.Length,
                Modified = fi.LastWriteTime,
                Name = Path.GetFileNameWithoutExtension(enabledName),
            };

            // 缓存命中就用旧的元数据（只更新启用状态，那个来自文件名）
            var key = path;
            if (Cache.TryGetValue(key, out var hit) && hit.Stamp == fi.LastWriteTime && hit.Size == fi.Length)
            {
                info.Name = hit.Info.Name;
                info.Version = hit.Info.Version;
                info.ModId = hit.Info.ModId;
                info.Loader = hit.Info.Loader;
                info.Description = hit.Info.Description;
            }
            else
            {
                try
                {
                    ReadMeta(path, info);
                }
                catch (Exception ex)
                {
                    LauncherLog.Warn($"读取 mod 元数据失败 {fileName}: {ex.Message}");
                }
                Cache[key] = (fi.LastWriteTime, fi.Length, info);
            }

            result.Add(info);
        }

        // 启用名相同的合并显示（.jar 和 .jar.disabled 同时存在时两个都留着，让用户看到冲突）
        return result
            .OrderByDescending(m => m.Enabled)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>清掉元数据缓存（强制重读）</summary>
    public static void ClearCache() => Cache.Clear();

    private static bool EndsWith(string s, string suffix) =>
        s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static int SuffixLen(string fileName) =>
        EndsWith(fileName, DisabledSuffix) ? DisabledSuffix.Length : OldSuffix.Length;

    /// <summary>
    /// 读 jar 内的元数据。按 1) mods.toml (Forge/NeoForge) 2) fabric.mod.json (Fabric/Quilt)
    /// 3) mcmod.info (1.12 老 Forge) 4) MANIFEST 的顺序尝试。
    /// </summary>
    private static void ReadMeta(string path, ModInfo info)
    {
        using var zip = ZipFile.OpenRead(path);

        var toml = ReadEntry(zip, "META-INF/mods.toml");
        if (toml is not null)
        {
            ParseModsToml(toml, info);
            // NeoForge 的 mods.toml 里 modLoader 可能写 javafml 但依赖 net.neoforged
            info.Loader = toml.Contains("neoforge", StringComparison.OrdinalIgnoreCase)
                ? "neoforge"
                : "forge";
            return;
        }

        var fabric = ReadEntry(zip, "fabric.mod.json");
        if (fabric is not null)
        {
            ParseFabricJson(fabric, info);
            info.Loader = fabric.Contains("\"quilt_loader\"", StringComparison.OrdinalIgnoreCase)
                ? "quilt"
                : "fabric";
            return;
        }

        var mcmod = ReadEntry(zip, "mcmod.info");
        if (mcmod is not null)
        {
            ParseMcmodInfo(mcmod, info);
            info.Loader = "forge";
            return;
        }

        // LiteLoader
        var litemod = ReadEntry(zip, "litemod.json");
        if (litemod is not null)
        {
            try
            {
                using var d = JsonDocument.Parse(litemod);
                info.Name = Str(d.RootElement, "name") ?? info.Name;
                info.Version = Str(d.RootElement, "version") ?? "";
                info.ModId = Str(d.RootElement, "name") ?? "";
                info.Loader = "litemod";
                info.Description = Str(d.RootElement, "description") ?? "";
            }
            catch { /* 坏 json 就当读不到 */ }
        }
    }

    /// <summary>读 zip 里的文本 entry（不存在返回 null）</summary>
    private static string? ReadEntry(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName)
                    ?? zip.Entries.FirstOrDefault(e =>
                        string.Equals(e.FullName, entryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length == 0 || entry.Length > 512 * 1024)
            return null; // 超过 512KB 的多半不是元数据，别读了
        using var s = entry.Open();
        using var reader = new StreamReader(s);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 极简 TOML 解析：只要 <c>[[mods]]</c> 段里的 modId / version / displayName / description。
    /// 不值得为这几个字段引一个 TOML 库（AOT 体积 + 依赖都划不来）。
    /// </summary>
    private static void ParseModsToml(string text, ModInfo info)
    {
        var lines = text.Split('\n');
        var inMods = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                // 第一个 [[mods]] 是主 mod；[[dependencies.xxx]] 之后的内容不管
                inMods = line.Equals("[[mods]]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inMods)
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var value = Unquote(line[(eq + 1)..].Trim());

            switch (key)
            {
                case "modId":
                    info.ModId = value;
                    break;
                case "version":
                    info.Version = value;
                    break;
                case "displayName":
                    info.Name = value.Length > 0 ? value : info.Name;
                    break;
                case "description":
                    // 多行 ''' 只取第一段，够显示了
                    info.Description = Shorten(value, 200);
                    break;
            }
        }
    }

    private static void ParseFabricJson(string text, ModInfo info)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            info.ModId = Str(root, "id") ?? "";
            info.Name = Str(root, "name") ?? info.Name;
            info.Version = Str(root, "version") ?? "";
            info.Description = Shorten(Str(root, "description") ?? "", 200);
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"fabric.mod.json 解析失败: {ex.Message}");
        }
    }

    private static void ParseMcmodInfo(string text, ModInfo info)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            // 1.12 是数组；也有写成 {"modList":[...]} 的
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("modList", out var ml))
                root = ml;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return;
            var first = root[0];
            info.ModId = Str(first, "modid") ?? "";
            info.Name = Str(first, "name") ?? info.Name;
            info.Version = Str(first, "version") ?? "";
            info.Description = Shorten(Str(first, "description") ?? "", 200);
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"mcmod.info 解析失败: {ex.Message}");
        }
    }

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.StartsWith("'''", StringComparison.Ordinal))
            return s.Trim('\'').Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\''))
        {
            var q = s[0];
            var end = s.LastIndexOf(q);
            if (end > 0)
                return s[1..end];
        }
        return s;
    }

    private static string Shorten(string s, int max) =>
        s.Replace("\r", " ").Replace("\n", " ").Trim() is var t && t.Length > max ? t[..max] + "..." : t;

    /// <summary>
    /// 启用/禁用。成功返回 true，失败时 message 给出原因（给 UI 弹提示用）。
    /// </summary>
    public static bool SetEnabled(ModInfo mod, bool enabled, out string message)
    {
        message = "";
        try
        {
            if (mod.Enabled == enabled)
                return true;

            var dir = Path.GetDirectoryName(mod.FilePath) ?? "";
            var targetPath = enabled
                ? Path.Combine(dir, mod.EnabledName)
                : Path.Combine(dir, mod.EnabledName + DisabledSuffix);

            // 目标已存在：可能是同名但内容不同的另一份文件（PCL 也会拦这种情况）
            if (File.Exists(targetPath))
            {
                message = $"目标文件已存在: {Path.GetFileName(targetPath)}\n" +
                          "同时存在启用与禁用两个版本时,请手动删除其中一个后再操作。";
                return false;
            }

            File.Move(mod.FilePath, targetPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            message = "文件被占用(游戏可能正在运行),请关闭游戏后重试。";
            return false;
        }
        catch (IOException ex)
        {
            message = $"改名失败: {ex.Message}\n(文件被占用时也会出现此提示,请关闭游戏后重试)";
            return false;
        }
        catch (Exception ex)
        {
            message = $"操作失败: {ex.Message}";
            return false;
        }
    }

    /// <summary>删除 mod 文件（回收站不好做，直接删；删前 UI 会确认）</summary>
    public static bool Delete(ModInfo mod, out string message)
    {
        message = "";
        try
        {
            File.Delete(mod.FilePath);
            Cache.Remove(mod.FilePath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            message = "文件被占用(游戏可能正在运行),请关闭游戏后重试。";
            return false;
        }
        catch (Exception ex)
        {
            message = $"删除失败: {ex.Message}";
            return false;
        }
    }
}
