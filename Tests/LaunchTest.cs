using LargeHardLiteLauncher.Core;

namespace LargeHardLiteLauncher;

/// <summary>
/// 真启动实测: 真的把游戏拉起来, 盯日志到进主菜单 (或超时), 然后杀掉。
/// 用来验证参数模板 / natives 布局是否真的能让客户端起来, 而不是只看拼出来的字符串。
/// </summary>
internal static class LaunchTest
{
    public static async Task RunAsync(string versionId, int timeoutSec)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        var cfg = LauncherConfig.Load();
        var all = VersionScanner.Scan(cfg.MinecraftPath);
        var v = all.FirstOrDefault(x => x.Id == versionId);
        if (v is null)
        {
            Console.Error.WriteLine($"[launch] 版本 {versionId} 没找到, 现有 {all.Count} 个");
            return;
        }

        Console.Error.WriteLine($"[launch] 版本={v.Id} main={v.MainClass}");
        Console.Error.WriteLine($"[launch] jvm模板={v.JvmTemplate.Count} game模板={v.GameTemplate.Count} 老格式={v.LegacyArguments is not null}");

        var task = TaskManager.Submit($"launchtest {versionId}", (_, _) => Task.CompletedTask);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));

        int exit = -999;
        string? error = null;
        try
        {
            exit = await GameLauncher.LaunchAsync(v, cfg, task, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            error = "超时取消 (大概率已经进游戏了)";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        // 打印日志尾部 + 关键里程碑
        var log = task.LogText.Split('\n');
        Console.Error.WriteLine($"[launch] exit={exit} error={error ?? "-"} 日志 {log.Length} 行");
        Console.Error.WriteLine("[launch] --- 全部日志 ---");
        foreach (var line in log)
        {
            if (line.Trim().Length > 0)
                Console.Error.WriteLine("  " + line.Trim());
        }
        Console.Error.WriteLine("[launch] --- 关键行 ---");
        foreach (var line in log)
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t.Contains("natives", StringComparison.OrdinalIgnoreCase)
                || t.Contains("主类") || t.Contains("启动参数") || t.Contains("工作目录")
                || t.Contains("版本隔离") || t.Contains("进程已启动") || t.Contains("退出")
                || t.Contains("Setting user") || t.Contains("Sound engine")
                || t.Contains("Time elapsed") || t.Contains("ERROR") || t.Contains("Exception")
                || t.Contains("LWJGL") || t.Contains("OpenGL"))
                Console.Error.WriteLine("  " + t);
        }

        // 判断有没有真的到主菜单
        var reached = task.LogText.Contains("Time elapsed", StringComparison.OrdinalIgnoreCase)
                   || task.LogText.Contains("Created:", StringComparison.OrdinalIgnoreCase);
        Console.Error.WriteLine($"[launch] ===== 到达主菜单/资源加载: {reached} =====");
    }
}
