using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 以 DETACHED_PROCESS 启动进程，脱离当前控制台，同时保留 stdout/stderr 重定向和退出通知。
/// Process.Start 创建的进程会继承父控制台，用户关终端时 Windows 向控制台内所有进程发
/// CTRL_CLOSE_EVENT，Java 随之退出；DETACHED_PROCESS 没有控制台，收不到该事件。
/// UseShellExecute=true 也能脱离，但拿不到输出也没有进程句柄，所以这里手写 CreateProcess。
/// </summary>
public sealed class DetachedProcess : IDisposable
{
    private IntPtr _hProcess;
    private int _pid;
    private volatile bool _exited;
    private int _exitCode = -1;

    public int Pid => _pid;
    public bool HasExited => _exited;
    public int ExitCode => _exitCode;
    public DateTime StartedAt { get; private set; }

    /// <summary>每读到一行输出就触发（stdout 和 stderr 合并，和 PCL 一样的处理）</summary>
    public event Action<string>? OutputLine;

    /// <summary>进程退出（后台线程触发）</summary>
    public event Action<int>? Exited;

    /// <summary>
    /// 启动进程。
    /// </summary>
    /// <param name="exePath">可执行文件完整路径（可为 null 表示从命令行推断）</param>
    /// <param name="args">参数（会按 Windows 规则自动加引号）</param>
    /// <param name="workingDir">工作目录</param>
    public static DetachedProcess Start(string exePath, IEnumerable<string> args, string workingDir)
    {
        var sa = new NativeMethods.SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>(),
            bInheritHandle = 1, // 管道写端必须能被子进程继承
        };
        if (!NativeMethods.CreatePipe(out var hRead, out var hWrite, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建管道失败");

        // 读端不给子进程继承，否则我们关了它也不收到 EOF
        NativeMethods.SetHandleInformation(hRead, NativeMethods.HANDLE_FLAG_INHERIT, 0);

        var si = new NativeMethods.STARTUPINFO
        {
            cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>(),
            dwFlags = NativeMethods.STARTF_USESTDHANDLES,
            hStdOutput = hWrite,
            hStdError = hWrite, // stderr 合并到 stdout，少一个线程
            hStdInput = IntPtr.Zero,
        };

        var cmd = new StringBuilder(BuildCommandLine(exePath, args), 32768);

        var ok = NativeMethods.CreateProcess(
            exePath,
            cmd,
            IntPtr.Zero,
            IntPtr.Zero,
            bInheritHandles: true,
            NativeMethods.DETACHED_PROCESS, // ★ 核心：脱离控制台
            IntPtr.Zero,
            workingDir,
            ref si,
            out var pi);

        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            NativeMethods.CloseHandle(hRead);
            NativeMethods.CloseHandle(hWrite);
            throw new Win32Exception(err, $"CreateProcess 失败:{exePath}");
        }

        // 父进程必须马上关写端，否则读端永远等不到 EOF
        NativeMethods.CloseHandle(hWrite);
        NativeMethods.CloseHandle(pi.hThread);

        var proc = new DetachedProcess
        {
            _hProcess = pi.hProcess,
            _pid = pi.dwProcessId,
        };
        try
        {
            proc.StartedAt = Process.GetProcessById(proc._pid).StartTime;
        }
        catch
        {
            proc.StartedAt = DateTime.Now; // 拿不到就凑合
        }

        proc.StartPump(hRead);
        LauncherLog.Info($"已启动(已脱离控制台) PID={proc._pid}: {exePath}");
        return proc;
    }

    /// <summary>后台线程：读输出 + 等退出</summary>
    private void StartPump(IntPtr hRead)
    {
        // 读线程
        var reader = new Thread(() =>
        {
            try
            {
                using var fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(hRead, ownsHandle: true),
                    FileAccess.Read, 4096, isAsync: false);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                while (sr.ReadLine() is { } line)
                    OutputLine?.Invoke(line);
            }
            catch
            {
                // 管道断了 = 进程结束，正常
            }
        })
        {
            IsBackground = true,
            Name = $"DetachedProcess-Reader-{_pid}",
        };
        reader.Start();

        // 等退出线程
        var waiter = new Thread(() =>
        {
            try
            {
                NativeMethods.WaitForSingleObject(_hProcess, 0xFFFFFFFF); // INFINITE
                uint code = 0;
                NativeMethods.GetExitCodeProcess(_hProcess, out code);
                _exitCode = unchecked((int)code);
            }
            catch
            {
                _exitCode = -1;
            }
            finally
            {
                _exited = true;
                Exited?.Invoke(_exitCode);
            }
        })
        {
            IsBackground = true,
            Name = $"DetachedProcess-Waiter-{_pid}",
        };
        waiter.Start();
    }

    /// <summary>强制结束（含子进程不做处理，Java 一般是单进程）</summary>
    public void Kill()
    {
        try
        {
            using var p = Process.GetProcessById(_pid);
            p.Kill(entireProcessTree: true);
        }
        catch
        {
            // 已经退了
        }
    }

    public void Dispose()
    {
        if (_hProcess != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }
        // 不杀进程：这个类的语义是"放生"
    }

    // ---------- 命令行拼装 ----------

    private static string BuildCommandLine(string exePath, IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(exePath));
        foreach (var a in args)
            sb.Append(' ').Append(Quote(a));
        return sb.ToString();
    }

    /// <summary>
    /// Windows 命令行转义规则（和 .NET 内部一致）：
    /// 反斜杠只在引号前才需要翻倍，引号本身要反斜杠转义。
    /// </summary>
    private static string Quote(string s)
    {
        if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\v' }) < 0)
            return s;

        var sb = new StringBuilder("\"");
        for (var i = 0; i < s.Length; i++)
        {
            var backslashes = 0;
            while (i < s.Length && s[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == s.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }
            if (s[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(s[i]);
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
