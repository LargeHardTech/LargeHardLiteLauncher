using System.Diagnostics;
using System.Text;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 按窗口类名查找 Minecraft 游戏窗口。用于判断游戏是否真的进到主界面（比解析日志可靠），
/// 以及改写窗口标题。类名由 LWJGL 固定，不会被 mod 改：
/// <c>GLFW30</c>（1.13+）、<c>LWJGL</c>（老版本）、<c>SunAwtFrame</c>（部分场景）。
/// </summary>
public static class GameWindowFinder
{
    private static readonly string[] GameWindowClasses = { "GLFW30", "LWJGL", "SunAwtFrame" };

    /// <summary>窗口查找结果</summary>
    public sealed class Result
    {
        /// <summary>游戏主窗口句柄（还没出来时为 0）</summary>
        public IntPtr Handle { get; init; }
        public string Title { get; init; } = "";

        /// <summary>见过 FML/Forge 的加载窗口（说明 Forge 已经开始加载了）</summary>
        public bool SawLoadingWindow { get; init; }

        public bool Found => Handle != IntPtr.Zero;
    }

    /// <summary>
    /// 扫一遍所有顶层窗口，找"这次启动的游戏"的窗口。
    /// </summary>
    /// <param name="processStartedAt">游戏进程的启动时间：只认之后创建的窗口，避免抓到上一次的游戏</param>
    public static Result Find(DateTime processStartedAt)
    {
        var handle = IntPtr.Zero;
        var title = "";
        var sawLoading = false;

        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (handle != IntPtr.Zero)
                    return false; // 已经找到，停

                var cls = new StringBuilder(256);
                if (NativeMethods.GetClassName(hwnd, cls, 256) <= 0)
                    return true;
                var className = cls.ToString();
                if (Array.IndexOf(GameWindowClasses, className) < 0)
                    return true;

                var txt = new StringBuilder(512);
                NativeMethods.GetWindowText(hwnd, txt, 512);
                var windowTitle = txt.ToString();

                // Forge 加载时的 FML 窗口：记一笔，但不当成游戏窗口
                if (windowTitle.StartsWith("FML", StringComparison.Ordinal))
                {
                    sawLoading = true;
                    return true;
                }
                // GLFW 的辅助窗口 / 弹窗，跳过
                if (windowTitle.StartsWith("GLFW", StringComparison.Ordinal)
                    || windowTitle == "PopupMessageWindow")
                    return true;

                // 窗口所属进程必须是这次启动的（PCL 也是这么判的）
                if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
                    return true;
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    // 比启动时间早 = 上次遗留的窗口
                    if (p.StartTime < processStartedAt.AddSeconds(-2))
                        return true;
                }
                catch
                {
                    return true;
                }

                if (!NativeMethods.IsWindowVisible(hwnd))
                    return true;

                handle = hwnd;
                title = windowTitle;
                return false;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"枚举窗口失败: {ex.Message}");
        }

        return new Result { Handle = handle, Title = title, SawLoadingWindow = sawLoading };
    }

    /// <summary>改游戏窗口标题（PCL 的"自定义窗口标题"功能）</summary>
    public static bool SetTitle(IntPtr hwnd, string title)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(title))
            return false;
        try
        {
            return NativeMethods.SetWindowText(hwnd, title);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把游戏窗口最大化</summary>
    public static bool Maximize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;
        try
        {
            return NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MAXIMIZE);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 当前进程所附着控制台里的进程列表（自测用：验证 DETACHED_PROCESS 真的把子进程摘出去了）
    /// </summary>
    public static uint[] ConsoleProcesses()
    {
        var buf = new uint[64];
        var n = NativeMethods.GetConsoleProcessList(buf, (uint)buf.Length);
        if (n == 0 || n > buf.Length)
            return Array.Empty<uint>();
        var result = new uint[n];
        Array.Copy(buf, result, n);
        return result;
    }
}
