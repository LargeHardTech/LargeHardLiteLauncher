using System.Text;

namespace LargeHardLiteLauncher.Core;

/// <summary>启动器自身日志 (常驻, 独立于后台任务)</summary>
public static class LauncherLog
{
    private static readonly List<string> _lines = new();
    private static readonly object _lock = new();
    private static readonly DateTime _start = DateTime.Now;

    /// <summary>日志级别: 0=安静 1=正常 2=详细 3=调试</summary>
    public static int Level { get; set; } = 1;

    public static event Action? Changed;

    public static int LineCount
    {
        get { lock (_lock) return _lines.Count; }
    }

    public static string LevelName => Level switch
    {
        <= 0 => Loc.T("log.quiet"),
        2 => Loc.T("log.verbose"),
        >= 3 => Loc.T("log.debug"),
        _ => Loc.T("log.normal"),
    };

    public static void Write(int level, string tag, string message)
    {
        if (level > Level || Level <= 0 && level > 1)
            return;
        lock (_lock)
        {
            _lines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {message}");
            // 上限防止无限增长
            if (_lines.Count > 5000)
                _lines.RemoveRange(0, 1000);
        }
        Changed?.Invoke();
    }

    public static void Info(string msg) => Write(1, "INFO", msg);
    public static void Warn(string msg) => Write(1, "WARN", msg);
    public static void Error(string msg) => Write(1, "ERROR", msg);
    public static void Verbose(string msg) => Write(2, "VERB", msg);
    public static void Debug(string msg) => Write(3, "DBG", msg);

    public static string Text
    {
        get
        {
            lock (_lock)
                return _lines.Count == 0
                    ? Loc.T("log.empty")
                    : $"LargeHardLiteLauncher 日志  启动于 {_start:yyyy-MM-dd HH:mm:ss}  级别 {LevelName}\n" +
                      new string('─', 60) + "\n" +
                      string.Join('\n', _lines);
        }
    }

    /// <summary>清空</summary>
    public static void Clear()
    {
        lock (_lock)
            _lines.Clear();
        Changed?.Invoke();
    }

    /// <summary>默认保存路径: ~/.largehardlitelauncher/logs/launcher-yyyyMMdd-HHmmss.log</summary>
    public static string DefaultLogPath()
    {
        var dir = Path.Combine(LauncherConfig.RootDir, "logs");
        return Path.Combine(dir, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    /// <summary>写文件, 返回实际路径; 失败抛异常由调用方提示</summary>
    public static string Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, Text, new UTF8Encoding(false));
        return path;
    }
}
