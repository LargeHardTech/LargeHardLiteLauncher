using System.Diagnostics;
using System.Text.Json;
using LargeHardLiteLauncher.Core;

namespace LargeHardLiteLauncher;

/// <summary>
/// 加载器安装自测 (JIT 与 AOT 都可跑, 不依赖 Pump): --loadertest [mc] [--install]
/// 只测列表 + Fabric 真装 (轻量: profile json + 十来个库); Forge 安装器要起 java 进程, 留给手动验。
/// </summary>
public static class LoaderTest
{
    public static async Task RunAsync(string mc, bool doInstall, bool doForge = false)
    {
        foreach (var kind in new[] { LoaderKind.Forge, LoaderKind.Fabric, LoaderKind.Quilt, LoaderKind.NeoForge })
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var list = await LoaderApi.ListAsync(kind, mc);
                var head = string.Join(" | ", list.Take(3).Select(v =>
                    v.Version + (v.Note.Length > 0 ? "[" + v.Note + "]" : "")));
                Console.Error.WriteLine($"[loader] {kind} {mc}: {list.Count} 个 ({sw.ElapsedMilliseconds}ms) 前3: {head}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[loader] {kind} {mc} 列表失败 ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            }
        }

        if (!doInstall)
            return;

        // Fabric 真装: 装到临时目录, 验证 json 落盘 + 库文件数量
        var dir = Path.Combine(Path.GetTempPath(), "lll-loadertest");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 忽略 */ }
        Directory.CreateDirectory(dir);

        var fabric = await LoaderApi.ListAsync(LoaderKind.Fabric, mc);
        if (fabric.Count == 0)
        {
            Console.Error.WriteLine("[loader] Fabric 列表为空, 跳过安装");
            return;
        }
        var ver = (fabric.FirstOrDefault(v => v.Note == "稳定") ?? fabric[0]).Version;
        var task = new TaskItem("loadertest-fabric");
        var sw2 = Stopwatch.StartNew();
        try
        {
            var id = await LoaderInstaller.InstallJsonLoaderAsync(
                LoaderKind.Fabric, mc, ver, dir, task, CancellationToken.None);
            var json = Path.Combine(dir, "versions", id, id + ".json");
            var libDir = Path.Combine(dir, "libraries");
            var libs = Directory.Exists(libDir)
                ? Directory.GetFiles(libDir, "*.jar", SearchOption.AllDirectories).Length
                : 0;
            Console.Error.WriteLine($"[loader] Fabric {ver} 安装完成: id={id} ({sw2.ElapsedMilliseconds}ms) json={File.Exists(json)} libs={libs}");

            // 版本名重命名 (PCL 的版本名支持): 目录 + json 文件名 + json id 字段
            var renamed = LoaderInstaller.RenameVersion(dir, id, "我的Fabric测试", task);
            var rJson = Path.Combine(dir, "versions", renamed, renamed + ".json");
            var idInJson = "";
            if (File.Exists(rJson))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(rJson));
                idInJson = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            }
            Console.Error.WriteLine($"[loader] 重命名: {renamed} dir={Directory.Exists(Path.Combine(dir, "versions", renamed))} json={File.Exists(rJson)} jsonId={idInJson}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[loader] Fabric 安装失败 ({sw2.ElapsedMilliseconds}ms): {ex.Message}");
            foreach (var line in task.LogText.Split('\n').TakeLast(6))
                Console.Error.WriteLine("  " + line);
        }

        if (!doForge)
            return;

        // Forge 真装: 下载安装器 → 起 java --installClient (安装器自己下载处理器和库, 要几分钟)
        var forge = await LoaderApi.ListAsync(LoaderKind.Forge, mc);
        if (forge.Count == 0)
        {
            Console.Error.WriteLine("[loader] Forge 列表为空, 跳过安装");
            return;
        }
        var fv = forge[0].Version;
        Console.Error.WriteLine($"[loader] Forge {fv} 开始安装 (下载安装器 + 跑 java)…");
        var task2 = new TaskItem("loadertest-forge");
        var sw3 = Stopwatch.StartNew();
        try
        {
            var id = await LoaderInstaller.InstallByInstallerAsync(
                LoaderKind.Forge, mc, fv, dir, task2, CancellationToken.None);
            var vdir = Path.Combine(dir, "versions", id);
            Console.Error.WriteLine($"[loader] Forge 安装完成: id={id} ({sw3.ElapsedMilliseconds}ms) dir={Directory.Exists(vdir)} files={Directory.GetFiles(vdir).Length}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[loader] Forge 安装失败 ({sw3.ElapsedMilliseconds}ms): {ex.Message}");
            foreach (var line in task2.LogText.Split('\n').TakeLast(10))
                Console.Error.WriteLine("  " + line);
        }
    }
}
