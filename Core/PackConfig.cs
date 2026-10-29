using System.Text;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 资源包 / 光影的启用状态读写。两者的开关都不在文件名上：
/// 资源包写在 <c>options.txt</c> 的 <c>resourcePacks</c> 数组里（可多选），
/// 光影写在 <c>optionsshaders.txt</c> 或 <c>config/iris.properties</c> 的
/// <c>shaderPack=</c> 里（只能一份，关闭时写 <c>OFF</c>）。
/// 旧版本用加 <c>.disabled</c> 后缀实现，改名后游戏会直接看不到该包，这里顺带做一次迁移还原。
/// </summary>
public static class PackConfig
{
    private const string DisabledSuffix = ".disabled";
    private const string ShaderOff = "OFF";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>resourcePacks 里指向本地文件的条目前缀（MC 的 FileResourcePack.id）</summary>
    private const string FilePrefix = "file/";

    // ================= 公共 =================

    /// <summary>
    /// 把历史上被改名成 <c>xxx.zip.disabled</c> 的包改回原名（旧 bug 的遗留）。
    /// 返回改回来的个数。改名后它处于「未启用」状态, 和用户当初的意图一致。
    /// </summary>
    public static int MigrateDisabledNames(string dir)
    {
        if (!Directory.Exists(dir))
            return 0;

        var fixedCount = 0;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                var name = Path.GetFileName(path);
                if (!name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var target = Path.Combine(dir, name[..^DisabledSuffix.Length]);
                try
                {
                    if (File.Exists(target) || Directory.Exists(target))
                        continue; // 同名文件已存在，别覆盖
                    if (Directory.Exists(path))
                        Directory.Move(path, target);
                    else
                        File.Move(path, target);
                    fixedCount++;
                }
                catch (Exception ex)
                {
                    LauncherLog.Warn($"还原 {name} 失败: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"扫描 {dir} 失败: {ex.Message}");
        }
        return fixedCount;
    }

    // ================= 资源包 =================

    /// <summary>读 options.txt 里启用中的资源包（返回磁盘上的名字，zip 带扩展名）</summary>
    public static HashSet<string> ReadEnabledPacks(string gameDir)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in ReadEnabledPackIds(gameDir))
        {
            if (raw.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) && raw.Length > FilePrefix.Length)
                result.Add(raw[FilePrefix.Length..]);
        }
        return result;
    }

    /// <summary>
    /// 设置某个包是否启用。只动 resourcePacks 里属于它的那一条，其它条目（vanilla、mod 提供的）原样保留。
    /// </summary>
    public static bool SetPackEnabled(string gameDir, string packName, bool enable, out string message)
    {
        message = "";
        try
        {
            var ids = ReadEnabledPackIds(gameDir);
            var id = FilePrefix + packName;
            var had = ids.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (enable)
            {
                if (!had)
                    ids.Add(id);
            }
            else if (!had)
            {
                return true; // 本来就没启用
            }

            WriteResourcePacksLine(gameDir, ids);
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static List<string> ReadEnabledPackIds(string gameDir)
    {
        var list = new List<string>();
        var path = Path.Combine(gameDir, "options.txt");
        if (!File.Exists(path))
            return list;

        string? line = null;
        try
        {
            foreach (var l in File.ReadAllLines(path))
            {
                if (l.StartsWith("resourcePacks:", StringComparison.Ordinal))
                {
                    line = l["resourcePacks:".Length..].Trim();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"读 options.txt 失败: {ex.Message}");
            return list;
        }

        if (string.IsNullOrWhiteSpace(line))
            return list;

        // MC 写的是标准 JSON 数组，直接解析比自己拆字符串稳
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
                        list.Add(s);
            }
        }
        catch (JsonException)
        {
            // 手改坏的 options.txt：别崩，当成空的（下次写会覆盖掉这一行）
            LauncherLog.Warn("options.txt 里的 resourcePacks 不是合法 JSON,已忽略");
        }
        return list;
    }

    private static void WriteResourcePacksLine(string gameDir, List<string> ids)
    {
        var path = Path.Combine(gameDir, "options.txt");
        var newLine = "resourcePacks:[" + string.Join(",", ids.Select(JsonText.Str)) + "]";

        var lines = new List<string>();
        if (File.Exists(path))
        {
            try
            {
                lines = File.ReadAllLines(path).ToList();
            }
            catch (Exception ex)
            {
                throw new IOException($"读 options.txt 失败: {ex.Message}", ex);
            }
        }

        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("resourcePacks:", StringComparison.Ordinal))
                continue;
            lines[i] = newLine;
            replaced = true;
            break;
        }
        if (!replaced)
            lines.Add(newLine);

        // 结尾空行会让 MC 忽略最后一行, 统一去掉尾部空行再补一个换行
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        Directory.CreateDirectory(gameDir);
        File.WriteAllText(path, string.Join("\n", lines) + "\n", Utf8NoBom);
    }

    // ================= 光影 =================

    /// <summary>读当前生效的光影包名；没开或读不到返回 null</summary>
    public static string? ReadActiveShader(string gameDir)
    {
        foreach (var path in ShaderConfigFiles(gameDir))
        {
            var v = ReadKeyValue(path, '=', "shaderPack");
            if (v is null)
                continue;
            if (IsOff(v))
                return null;
            return v;
        }
        return null;
    }

    /// <summary>
    /// 设置生效的光影（同时只能有一个）。<paramref name="packName"/> 传 null = 关掉光影。
    /// 会写进所有存在的配置文件（OptiFine 和 Iris 各读各的）。
    /// </summary>
    public static bool SetActiveShader(string gameDir, string? packName, out string message)
    {
        message = "";
        try
        {
            var files = ShaderConfigFiles(gameDir).Where(File.Exists).ToList();
            if (files.Count == 0)
            {
                // 两个都没有：按 OptiFine 的约定新建（Iris 装了之后也会读 optionsshaders.txt 的旧值）
                files.Add(Path.Combine(gameDir, "optionsshaders.txt"));
            }

            var value = string.IsNullOrEmpty(packName) ? ShaderOff : packName;
            foreach (var path in files)
                WriteKeyValue(path, '=', "shaderPack", value);
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    /// <summary>光影配置可能在这两个位置</summary>
    private static IEnumerable<string> ShaderConfigFiles(string gameDir)
    {
        yield return Path.Combine(gameDir, "optionsshaders.txt");
        yield return Path.Combine(gameDir, "config", "iris.properties");
    }

    private static bool IsOff(string v) =>
        v.Length == 0
        || v.Equals("OFF", StringComparison.OrdinalIgnoreCase)
        || v.Equals("(off)", StringComparison.OrdinalIgnoreCase)
        || v.Equals("none", StringComparison.OrdinalIgnoreCase);

    // ================= 通用 key=value 读写 =================

    private static string? ReadKeyValue(string path, char sep, string key)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.Trim();
                if (t.Length == 0 || t[0] == '#')
                    continue;
                var i = t.IndexOf(sep);
                if (i <= 0)
                    continue;
                if (!t[..i].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;
                return t[(i + 1)..].Trim();
            }
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"读 {Path.GetFileName(path)} 失败: {ex.Message}");
        }
        return null;
    }

    private static void WriteKeyValue(string path, char sep, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var newLine = $"{key}{sep}{value}";
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            var idx = t.IndexOf(sep);
            if (idx <= 0 || !t[..idx].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            lines[i] = newLine;
            replaced = true;
            break;
        }
        if (!replaced)
            lines.Add(newLine);

        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, string.Join("\n", lines) + "\n", Utf8NoBom);
    }
}
