using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 「上一次启动的游戏」记录。写盘保存，这样启动器被关掉（甚至被强杀）之后，
/// 下次打开还能知道那个游戏是不是还在跑。
/// </summary>
public sealed class GameSession
{
    public int Pid { get; set; }

    /// <summary>进程启动时间（UTC ticks）。PID 会被系统复用，靠它区分"同一个进程"</summary>
    public long StartTicksUtc { get; set; }

    /// <summary>进程名，一般就是 java / javaw</summary>
    public string ProcessName { get; set; } = "";

    public string VersionId { get; set; } = "";
    public string VersionName { get; set; } = "";
    public DateTime LaunchedAt { get; set; } = DateTime.Now;

    /// <summary>logs/latest.log 的路径（重新接上时用来跟读日志）</summary>
    public string LogPath { get; set; } = "";

    public string GameDir { get; set; } = "";

    public TimeSpan Elapsed => DateTime.Now - LaunchedAt;
}

/// <summary>
/// 会话记录的读写。文件放在数据目录下，启动器被关窗口时不会清理，
/// 下次启动靠 PID + 启动时间双重校验判断游戏是否还活着（死记录自动清掉）。
/// </summary>
public static class GameSessionStore
{
    private static string FilePath => Path.Combine(LauncherConfig.RootDir, "running-game.json");

    public static void Save(GameSession s)
    {
        try
        {
            Directory.CreateDirectory(LauncherConfig.RootDir);
            var json = new StringBuilder();
            json.Append("{\n");
            json.Append($"  \"pid\": {s.Pid},\n");
            json.Append($"  \"startTicksUtc\": {s.StartTicksUtc},\n");
            json.Append($"  \"processName\": \"{Esc(s.ProcessName)}\",\n");
            json.Append($"  \"versionId\": \"{Esc(s.VersionId)}\",\n");
            json.Append($"  \"versionName\": \"{Esc(s.VersionName)}\",\n");
            json.Append($"  \"launchedAt\": \"{s.LaunchedAt:O}\",\n");
            json.Append($"  \"logPath\": \"{Esc(s.LogPath)}\",\n");
            json.Append($"  \"gameDir\": \"{Esc(s.GameDir)}\"\n");
            json.Append("}\n");
            File.WriteAllText(FilePath, json.ToString());
            LauncherLog.Info($"记录运行中的游戏: PID={s.Pid} {s.VersionName}");
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"写 running-game.json 失败: {ex.Message}");
        }
    }

    public static GameSession? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;

            return new GameSession
            {
                Pid = Int(root, "pid"),
                StartTicksUtc = Long(root, "startTicksUtc"),
                ProcessName = Str(root, "processName") ?? "",
                VersionId = Str(root, "versionId") ?? "",
                VersionName = Str(root, "versionName") ?? "",
                LaunchedAt = DateTime.TryParse(Str(root, "launchedAt"), out var t) ? t : DateTime.Now,
                LogPath = Str(root, "logPath") ?? "",
                GameDir = Str(root, "gameDir") ?? "",
            };
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"读 running-game.json 失败: {ex.Message}");
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch
        {
            // 删不掉不影响
        }
    }

    /// <summary>
    /// 三重校验这个会话对应的进程是不是还活着：
    /// ① PID 存在 ② 进程名对得上 ③ 启动时间对得上（防 PID 复用）
    /// </summary>
    public static bool IsAlive(GameSession s)
    {
        if (s.Pid <= 0)
            return false;
        try
        {
            using var p = Process.GetProcessById(s.Pid);
            if (p.HasExited)
                return false;

            // ② 进程名（java / javaw；有些发行版叫别的，记了才校验）
            if (!string.IsNullOrEmpty(s.ProcessName)
                && !p.ProcessName.Equals(s.ProcessName, StringComparison.OrdinalIgnoreCase))
                return false;

            // ③ 启动时间。允许 2 秒误差（取时间的时机略有差异）
            if (s.StartTicksUtc > 0)
            {
                var ticks = p.StartTime.ToUniversalTime().Ticks;
                if (Math.Abs(ticks - s.StartTicksUtc) > TimeSpan.FromSeconds(2).Ticks)
                    return false;
            }
            return true;
        }
        catch (ArgumentException)
        {
            return false; // 进程不存在
        }
        catch
        {
            return false; // 权限之类的，保守当它死了
        }
    }

    /// <summary>
    /// 取"还活着的"上次会话。死记录会被顺手清掉（自愈）。
    /// </summary>
    public static GameSession? LoadAlive()
    {
        var s = Load();
        if (s is null)
            return null;
        if (IsAlive(s))
            return s;
        LauncherLog.Info($"上次的游戏 (PID={s.Pid}) 已经不在了,清掉记录");
        Clear();
        return null;
    }

    // ---------- JSON 小工具 ----------

    private static string Esc(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n")
        .Replace("\r", "");

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static long Long(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
