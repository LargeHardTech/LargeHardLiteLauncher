using System.Diagnostics;
using System.Text;

namespace LargeHardLiteLauncher.Core;

/// <summary>启动 JAR 的参数（给"启动 JAR"页用，和 MC 版本无关）</summary>
public sealed class JarLaunchOptions
{
    public string JarPath { get; set; } = "";
    /// <summary>Java 路径，空 = 自动（挑 17 及以上最高的）</summary>
    public string JavaPath { get; set; } = "";
    /// <summary>工作目录，空 = jar 所在目录（服务器要读写同目录的 world/eula.txt）</summary>
    public string WorkingDir { get; set; } = "";

    public int MaxMemoryMb { get; set; } = 2048;
    public int MinMemoryMb { get; set; } = 512;

    /// <summary>GC 预设：g1 / zgc / parallel / serial / none</summary>
    public string GcPreset { get; set; } = "g1";

    /// <summary>服务端模式：往游戏参数里加 nogui，关窗口时结束进程</summary>
    public bool ServerMode { get; set; } = true;

    public string ExtraJvmArgs { get; set; } = "";
    public string ExtraArgs { get; set; } = "";
}

/// <summary>GC 预设的显示名和参数</summary>
public sealed record GcPreset(string Key, string Display, string Description);

/// <summary>
/// 独立启动一个 jar（MC 服务端 / 客户端 / 任何 Java 程序）。
/// 和 <see cref="GameLauncher"/> 完全独立：不需要版本 json，只拼 <c>java [jvm] -jar xxx.jar [args]</c>。
/// </summary>
public static class JarLauncher
{
    public static readonly GcPreset[] GcPresets =
    {
        new("g1", "G1(推荐)", "通用选择,延迟与吞吐平衡。1.12+ 服务端默认"),
        new("zgc", "ZGC(Java 17+)", "大内存低延迟,堆 8G 以上效果明显。Java 17 以下不支持"),
        new("parallel", "Parallel", "吞吐优先,适合小内存或单核性能较弱的机器"),
        new("serial", "Serial", "极简,适用于内存小于 1G 的场景"),
        new("none", "不指定", "完全交给 JVM 决定"),
    };

    public static GcPreset PresetOf(string key) =>
        GcPresets.FirstOrDefault(p => p.Key == key) ?? GcPresets[0];

    /// <summary>
    /// 拼启动参数。参数顺序：内存 → GC → 其他 jvm → -jar → 程序参数。
    /// Java 8 不认的选项会被跳过（免得 "Unrecognized VM option" 直接起不来）。
    /// </summary>
    public static List<string> BuildArgs(JarLaunchOptions opt, int javaMajor, List<string>? warnings = null)
    {
        var args = new List<string>();

        var maxMem = Math.Max(256, opt.MaxMemoryMb);
        var minMem = Math.Clamp(opt.MinMemoryMb, 128, maxMem);
        args.Add($"-Xms{minMem}M");
        args.Add($"-Xmx{maxMem}M");

        // ---- 文件编码 / 内存映射（现代 Java 都支持）----
        if (javaMajor >= 18)
            args.Add("-Dfile.encoding=UTF-8");

        // ---- GC ----
        switch (opt.GcPreset)
        {
            case "g1":
                if (javaMajor >= 8)
                {
                    args.Add("-XX:+UseG1GC");
                    args.Add("-XX:+UnlockExperimentalVMOptions");
                    args.Add("-XX:+UnlockDiagnosticVMOptions");
                    args.Add("-XX:G1NewSizePercent=20");
                    args.Add("-XX:G1ReservePercent=20");
                    args.Add("-XX:MaxGCPauseMillis=50");
                    args.Add("-XX:G1HeapRegionSize=32M");
                }
                else
                {
                    warnings?.Add("Java 8 以下没有 G1,已改用默认 GC");
                }
                break;

            case "zgc":
                if (javaMajor >= 17)
                {
                    args.Add("-XX:+UseZGC");
                    // ZGC 配大堆才好用，顺手给两个常规参数
                    args.Add("-XX:+UnlockExperimentalVMOptions");
                }
                else
                {
                    warnings?.Add($"ZGC 需要 Java 17 及以上,当前 Java {javaMajor} ,已回退到 G1");
                    args.Add("-XX:+UseG1GC");
                }
                break;

            case "parallel":
                args.Add("-XX:+UseParallelGC");
                break;

            case "serial":
                args.Add("-XX:+UseSerialGC");
                break;
        }

        // ---- 崩溃时别吞堆栈（排查问题时很有用）----
        if (javaMajor >= 8)
            args.Add("-XX:-OmitStackTraceInFastThrow");

        // ---- 用户自定义 JVM 参数（里面的 -Xmx/-Xms 过滤掉，避免打架）----
        foreach (var seg in (opt.ExtraJvmArgs ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)
                || seg.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase))
                continue;
            args.Add(seg);
        }

        // ---- jar + 程序参数 ----
        args.Add("-jar");
        args.Add(opt.JarPath);

        if (opt.ServerMode)
            args.Add("nogui"); // MC 服务端的无界面开关；其他 jar 不认也没关系

        foreach (var seg in (opt.ExtraArgs ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            args.Add(seg);

        return args;
    }

    /// <summary>校验参数，返回错误说明（null = 没问题）</summary>
    public static string? Validate(JarLaunchOptions opt)
    {
        if (string.IsNullOrWhiteSpace(opt.JarPath))
            return "还没选 jar 文件。";
        if (!File.Exists(opt.JarPath))
            return $"jar 文件不存在:{opt.JarPath}";
        if (!opt.JarPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            return "选的文件不是 .jar。";
        if (opt.MaxMemoryMb < 256)
            return "最大内存至少 256 MB。";
        if (opt.MinMemoryMb > opt.MaxMemoryMb)
            return "最小内存不能大于最大内存。";
        return null;
    }

    /// <summary>工作目录（空则用 jar 所在目录）</summary>
    public static string ResolveWorkingDir(JarLaunchOptions opt)
    {
        if (!string.IsNullOrWhiteSpace(opt.WorkingDir))
            return opt.WorkingDir;
        var dir = Path.GetDirectoryName(opt.JarPath);
        return string.IsNullOrEmpty(dir) ? Environment.CurrentDirectory : dir;
    }

    /// <summary>
    /// 起进程。输出逐行回调（在后台线程），返回退出码。
    /// </summary>
    public static async Task<int> RunAsync(
        JarLaunchOptions opt, string javaPath, int javaMajor,
        Action<string> onOutput, CancellationToken ct)
    {
        var workDir = ResolveWorkingDir(opt);
        Directory.CreateDirectory(workDir);

        var psi = new ProcessStartInfo(javaPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // 留着发 stop 之类的命令
            CreateNoWindow = true,
            WorkingDirectory = workDir,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in BuildArgs(opt, javaMajor))
            psi.ArgumentList.Add(a);

        onOutput($"工作目录: {workDir}");
        onOutput($"Java: {javaPath} (主版本 {javaMajor})");
        onOutput($"命令: {javaPath} {string.Join(' ', psi.ArgumentList)}");
        onOutput("");

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                onOutput(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                onOutput(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        LastProcess = proc;

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消 = 用户点了停止
            TryKill(proc);
            throw;
        }
        finally
        {
            LastProcess = null;
        }

        onOutput("");
        onOutput($"进程退出,代码 {proc.ExitCode}");
        return proc.ExitCode;
    }

    /// <summary>当前跑着的进程（给"停止"按钮和命令输入用）</summary>
    public static Process? LastProcess { get; private set; }

    /// <summary>往进程的标准输入写一行（服务端控制台命令，比如 stop / list）</summary>
    public static bool TrySendCommand(string command)
    {
        var p = LastProcess;
        if (p is null || p.HasExited)
            return false;
        try
        {
            p.StandardInput.WriteLine(command);
            p.StandardInput.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>结束进程树（jar 可能自己再拉起子进程）</summary>
    public static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // 已经退了就算了
        }
    }
}
