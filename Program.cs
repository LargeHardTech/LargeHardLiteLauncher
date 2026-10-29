using LargeHardLiteLauncher.UI;
using Terminal.Gui;

namespace LargeHardLiteLauncher;

internal static class Program
{
#if !PUBLISH_BUILD
    // ===== 以下全是开发期自测脚手架（--uitest / --vmtest / --selftest ...）=====
    // PUBLISH_BUILD 由 csproj 在 Release 下定义 → 发布产物里这段代码连编译都不参与，
    // 连带 SelfTest.cs 等 5 个测试文件也一起被 csproj 排除。

    /// <summary>网络诊断实测: 正常站点 + 证书有问题的站点, 看 Brief/Describe 输出质量</summary>
    private static async Task RunNetTest()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        var urls = new[]
        {
            "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
            "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json",
            "https://self-signed.badssl.com/",
            "https://expired.badssl.com/",
            "https://httpstat.us/502",
        };

        using var http = Core.Net.CreateClient(TimeSpan.FromSeconds(20));
        foreach (var u in urls)
        {
            Console.Error.WriteLine($"[net] === {u}");
            try
            {
                var len = (await http.GetStringAsync(u)).Length;
                Console.Error.WriteLine($"[net] OK   {len} 字符");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[net] FAIL Brief: {Core.Net.Brief(ex)}");
                Console.Error.WriteLine($"[net] 单行归因: {Core.Net.DescribeHttp(ex, u)}");
            }
        }

        // 忽略证书错误模式: 自签名站点应该能通
        Console.Error.WriteLine("[net] === 自签名站点 (忽略证书错误模式)");
        using var httpIgnore = Core.Net.CreateClient(TimeSpan.FromSeconds(20), ignoreSslErrors: true);
        try
        {
            var len = (await httpIgnore.GetStringAsync("https://self-signed.badssl.com/")).Length;
            Console.Error.WriteLine($"[net] OK   {len} 字符 (忽略证书错误生效)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[net] FAIL 忽略模式下仍失败: {ex.Message}");
        }

        // HTTP 错误码归因
        Console.Error.WriteLine("[net] === HTTP 状态码归因");
        foreach (var code in new[] { 502, 503, 404, 429 })
            Console.Error.WriteLine($"[net]   {code} → {Core.Net.HttpStatusHint(code)}");
    }

    /// <summary>实测版本选择弹窗: 打开后 dump 布局再关闭</summary>
    private static void RunPickerTest()
    {
        Application.Init();
        var win = new MainWindow();
        win.ShowPage(PageId.Launch);

        var pages = typeof(MainWindow).GetField("_pages",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(win)!;
        var lp = ((System.Collections.Generic.Dictionary<PageId, PageBase>)pages)[PageId.Launch];

        Application.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            typeof(LaunchPage).GetMethod("ShowVersionPicker",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(lp, null);
            return false;
        });
        Application.AddTimeout(TimeSpan.FromMilliseconds(900), () =>
        {
            Application.RequestStop();
            return false;
        });
        Application.AddTimeout(TimeSpan.FromMilliseconds(1300), () =>
        {
            Application.RequestStop();
            return false;
        });

        Application.Run(win);
        Application.Shutdown();
    }

    /// <summary>
    /// 崩溃分析实测：喂几段真实的崩溃日志，看能不能识别出来。
    /// </summary>
    private static void RunCrashAnalyzeTest()
    {
        var samples = new (string Name, string Log)[]
        {
            ("Forge 模块冲突（用户 2026-10-26 真实日志）",
                "[18:23:20] [stderr] Exception in thread \"main\" java.lang.module.ResolutionException: " +
                "Modules _1._20._1 and minecraft export package net.minecraft.client.main to module forge\n" +
                "[18:23:20] 游戏进程退出, 代码 1"),

            ("Java 版本过低",
                "[main/ERROR] java.lang.UnsupportedClassVersionError: cpw/mods/bootstraplauncher/BootstrapLauncher " +
                "has been compiled by a more recent version of the Java Runtime (class file version 61.0)"),

            ("Java 太新",
                "[stderr] [LWJGL] [ThreadLocalUtil] Unsupported JNI version detected, this may result in a crash."),

            ("内存不足",
                "java.lang.OutOfMemoryError: Java heap space\n\tat net.minecraft.client.Minecraft.<init>(Minecraft.java:500)"),

            ("缺前置 mod",
                "[main/ERROR] Missing or unsupported mandatory dependencies:\n" +
                "\tMod ID: 'architectury', Requested by: 'some-mod', Expected range: '[9,)', Actual version: '[MISSING]'"),

            ("mod 重复",
                "[main/WARN] Found a duplicate mod sodium at D:\\mc\\mods\\sodium-1.jar"),

            ("Mixin 失败",
                "org.spongepowered.asm.mixin.injection.throwables.InvalidMixinException: Mixin apply failed " +
                "sodium.mixins.json:SomeMixin -> net.minecraft.client.Minecraft"),

            ("完全正常的日志（不该报任何东西）",
                "[18:27:24] [Render thread/INFO] [minecraft/Minecraft]: Setting user: PeterMC10086\n" +
                "[18:27:31] [Render thread/INFO] [minecraft/SoundEngine]: Sound engine started"),
        };

        foreach (var (name, log) in samples)
        {
            var diags = Core.CrashAnalyzer.Analyze(log, 1);
            Console.WriteLine($"[crash] === {name} ===");
            if (diags.Count == 0)
                Console.WriteLine("[crash]   (没识别出原因)");
            for (var i = 0; i < diags.Count; i++)
            {
                foreach (var line in diags[i].Render(i + 1).Split('\n'))
                    Console.WriteLine("[crash]   " + line);
            }
            Console.WriteLine();
        }
        Console.WriteLine("[crash] done");
    }

    /// <summary>
    /// JAR 启动实测：<c>--jartest &lt;jar&gt; [--gc zgc] [--mem 4096]</c>。
    /// 打印拼出来的参数 + 真实起进程，把输出原样打出来。
    /// </summary>
    private static async Task RunJarLaunchTest(string[] args)
    {
        var idx = Array.IndexOf(args, "--jartest");
        var jar = idx + 1 < args.Length && !args[idx + 1].StartsWith("--") ? args[idx + 1] : "";
        if (!File.Exists(jar))
        {
            Console.WriteLine($"[jar] 文件不存在: {jar}");
            return;
        }

        string GcArg(string name, string def)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
        }

        var opt = new Core.JarLaunchOptions
        {
            JarPath = Path.GetFullPath(jar),
            MaxMemoryMb = int.TryParse(GcArg("--mem", "2048"), out var m) ? m : 2048,
            MinMemoryMb = 256,
            GcPreset = GcArg("--gc", "g1"),
            ServerMode = false, // 测试 jar 不认 nogui
            ExtraArgs = GcArg("--extra", ""),
        };

        var err = Core.JarLauncher.Validate(opt);
        if (err is not null)
        {
            Console.WriteLine($"[jar] 参数不合法: {err}");
            return;
        }

        var javaPath = Core.JavaDetector.Resolve("", 0);
        if (javaPath is null)
        {
            Console.WriteLine("[jar] 找不到 Java");
            return;
        }
        var major = Core.JavaDetector.Detect()
            .FirstOrDefault(j => string.Equals(j.Path, javaPath, StringComparison.OrdinalIgnoreCase))?.Major ?? 0;

        var warn = new List<string>();
        Console.WriteLine($"[jar] java={javaPath} (主版本 {major})");
        Console.WriteLine($"[jar] 参数: {string.Join(' ', Core.JarLauncher.BuildArgs(opt, major, warn))}");
        foreach (var w in warn)
            Console.WriteLine($"[jar] 警告: {w}");

        Console.WriteLine("[jar] ---- 进程输出 ----");
        var code = await Core.JarLauncher.RunAsync(opt, javaPath, major,
            line => Console.WriteLine($"[out] {line}"), CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"[jar] ---- 退出码 {code} ----");
    }

    /// <summary>
    /// 导出实测：<c>--exporttest &lt;版本id&gt; [输出路径]</c>。
    /// 导完会用 <see cref="Core.ModpackInstaller.Read"/> 再读一遍，验证"导出的包能被自己识别"。
    /// </summary>
    private static async Task RunExportTest(string[] args)
    {
        var cfg = Core.LauncherConfig.Load();
        var idx = Array.IndexOf(args, "--exporttest");
        var verId = idx + 1 < args.Length && !args[idx + 1].StartsWith("--") ? args[idx + 1] : cfg.SelectedVersion;
        var outPath = idx + 2 < args.Length && !args[idx + 2].StartsWith("--")
            ? args[idx + 2]
            : Path.Combine(Path.GetTempPath(), "lll-export-test.mrpack");

        var all = Core.VersionScanner.Scan(cfg.MinecraftPath);
        var v = all.First(x => x.Id == verId);
        if (v is null)
        {
            Console.WriteLine($"[export] 版本不存在: {verId}");
            return;
        }

        var task = new Core.TaskItem("导出测试");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var matchOnline = !args.Contains("--no-online");
            var result = await Core.ModpackExporter.ExportAsync(
                cfg, v, outPath, verId, "1.0.0", matchOnline, task, CancellationToken.None).ConfigureAwait(false);
            sw.Stop();
            Console.WriteLine($"[export] 导出完成 {sw.ElapsedMilliseconds}ms → {result.Path}");
            Console.WriteLine($"[export] 在线匹配 {result.Matched} 个 / 打包 {result.Packed} 个 / {result.SizeMb:0.0} MB");

            // 闭环验证：用安装器读一遍
            var back = Core.ModpackInstaller.Read(result.Path);
            Console.WriteLine($"[export] 回收验证: {back.Summary}");
            Console.WriteLine($"[export] 读回的 MC={back.McVersion} 加载器={back.LoaderDisplay} 下载项={back.Files.Count}");

            // 看一眼 zip 里的结构
            using var zip = System.IO.Compression.ZipFile.OpenRead(result.Path);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Console.WriteLine($"[export] zip 条目 {names.Count} 个");
            Console.WriteLine($"[export] 含 index.json={names.Contains("modrinth.index.json")}");
            var modJars = names.Count(n => n.StartsWith("overrides/mods/") && n.EndsWith(".jar"));
            var disabled = names.Count(n => n.EndsWith(".disabled"));
            var logs = names.Count(n => n.StartsWith("overrides/logs/"));
            Console.WriteLine($"[export] overrides/mods 里 {modJars} 个 jar（禁用 {disabled} 个不该带，logs {logs} 个不该带）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[export] 失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 整合包实测：<c>--packtest &lt;file&gt; [--install 版本名]</c>。
    /// 不带 --install 只解析（快），带上就真装（会装缺失的加载器 + 解压 overrides）。
    /// </summary>
    private static async Task RunModpackTest(string[] args)
    {
        var idx = Array.IndexOf(args, "--packtest");
        var path = idx + 1 < args.Length && !args[idx + 1].StartsWith("--") ? args[idx + 1] : "";
        if (!File.Exists(path))
        {
            Console.WriteLine($"[pack] 文件不存在: {path}");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Core.ModpackInfo pack;
        try
        {
            pack = Core.ModpackInstaller.Read(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[pack] 解析失败: {ex.Message}");
            return;
        }
        sw.Stop();

        Console.WriteLine($"[pack] 解析 {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"[pack] {pack.Summary}");
        Console.WriteLine($"[pack] 名称={pack.Name}  版本={pack.Version}  格式={pack.Format}");
        Console.WriteLine($"[pack] MC={pack.McVersion}  加载器={pack.LoaderDisplay}");
        Console.WriteLine($"[pack] 下载清单 {pack.Files.Count} 个");
        foreach (var f in pack.Files.Take(5))
            Console.WriteLine($"[pack]   {f.RelPath}  {f.Size / 1048576.0:0.0} MB");

        var instIdx = Array.IndexOf(args, "--install");
        if (instIdx < 0)
        {
            Console.WriteLine("[pack] 只解析，没安装（加 --install <版本名> 才真装）");
            return;
        }
        var instanceName = instIdx + 1 < args.Length && !args[instIdx + 1].StartsWith("--")
            ? args[instIdx + 1]
            : "pack-test";

        var cfg = Core.LauncherConfig.Load();
        var task = new Core.TaskItem($"安装整合包 {instanceName}");
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var name = await Core.ModpackInstaller.InstallAsync(path, pack, cfg.MinecraftPath, instanceName,
                cfg.DownloadSource, task, CancellationToken.None).ConfigureAwait(false);
            sw2.Stop();
            Console.WriteLine($"[pack] 装好了: {name} ({sw2.ElapsedMilliseconds}ms)");
            var verDir = Path.Combine(cfg.MinecraftPath, "versions", name);
            Console.WriteLine($"[pack] 版本目录: {verDir}");
            Console.WriteLine($"[pack] json 存在={File.Exists(Path.Combine(verDir, name + ".json"))}");
            var modsCount = Directory.Exists(Path.Combine(verDir, "mods"))
                ? Directory.GetFiles(Path.Combine(verDir, "mods"), "*.jar").Length
                : 0;
            Console.WriteLine($"[pack] mods: {modsCount} 个 jar");
            Console.WriteLine($"[pack] 解压文件总数: {Directory.GetFiles(verDir, "*", SearchOption.AllDirectories).Length}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[pack] 安装失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Java 自动下载实测：<c>--javatest [主版本] [--download]</c>。
    /// 不带 --download 只查 API 不真下（省流量）。
    /// </summary>
    private static async Task RunJavaDownloadTest(string[] args)
    {
        var idx = Array.IndexOf(args, "--javatest");
        var majorArg = idx + 1 < args.Length && int.TryParse(args[idx + 1], out var m) ? m : 17;
        var doDownload = args.Contains("--download");

        Console.WriteLine($"[java] 查询 Adoptium Java {majorArg}…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Core.JavaPackage? pkg;
        try
        {
            pkg = await Core.JavaInstaller.FetchLatestAsync(majorArg, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[java] 查询失败: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        sw.Stop();

        if (pkg is null)
        {
            Console.WriteLine("[java] 没查到（API 返回空数组）");
            return;
        }
        Console.WriteLine($"[java] {pkg.Display}  ({sw.ElapsedMilliseconds}ms)");
        Console.WriteLine($"[java] {pkg.FileName}");
        Console.WriteLine($"[java] {pkg.Url}");
        Console.WriteLine($"[java] sha256={pkg.Sha256?[..Math.Min(16, pkg.Sha256?.Length ?? 0)]}…");

        if (!doDownload)
        {
            Console.WriteLine("[java] 只查了 API，没下载（加 --download 才真下）");
            return;
        }

        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var exe = await Core.JavaInstaller.InstallAsync(
                pkg,
                (pct, msg) => Console.WriteLine($"[java] {pct * 100:0}% {msg}"),
                CancellationToken.None);
            sw2.Stop();
            Console.WriteLine($"[java] 装好了 ({sw2.ElapsedMilliseconds}ms): {exe}");
            Console.WriteLine($"[java] 存在={File.Exists(exe)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[java] 下载失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 补全检查实测: <c>--checkver [版本id]</c>。打印缺失统计和耗时，不下载。
    /// </summary>
    private static void RunVersionCheckTest(string[] args)
    {
        var cfg = Core.LauncherConfig.Load();
        var idx = Array.IndexOf(args, "--checkver");
        var verId = idx + 1 < args.Length && !args[idx + 1].StartsWith("--") ? args[idx + 1] : cfg.SelectedVersion;

        var all = Core.VersionScanner.Scan(cfg.MinecraftPath);
        var v = all.FirstOrDefault(x => x.Id == verId) ?? all.FirstOrDefault();
        if (v is null)
        {
            Console.WriteLine("[check] 本地没有版本");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var plan = Core.VersionChecker.Build(cfg, v);
        sw.Stop();

        var idxPath = Path.Combine(cfg.MinecraftPath, "assets", "indexes", (v.AssetIndexId ?? "?") + ".json");
        Console.WriteLine($"[check] 版本={v.Id} 库={v.Libraries.Count} 资源索引={v.AssetIndexId}");
        Console.WriteLine($"[check] index: {idxPath} exists={File.Exists(idxPath)} virtual={v.AssetsVirtual ?? "(null)"}");
        Console.WriteLine($"[check] 检查 {plan.Checked} 项, {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"[check] {plan.Summary}");
        Console.WriteLine($"[check] 计划文件数={plan.Files.Count}");
        foreach (var s in plan.Samples)
            Console.WriteLine($"[check]   缺: {s}");
    }

    /// <summary>
    /// Mod 扫描实测: <c>--modscan [版本id]</c>。打印 mods 目录、条数、耗时和每个 mod 的元数据。
    /// </summary>
    private static void RunModScanTest(string[] args)
    {
        var cfg = Core.LauncherConfig.Load();
        var idx = Array.IndexOf(args, "--modscan");
        var verId = idx + 1 < args.Length && !args[idx + 1].StartsWith("--") ? args[idx + 1] : cfg.SelectedVersion;

        var all = Core.VersionScanner.Scan(cfg.MinecraftPath);
        var v = all.FirstOrDefault(x => x.Id == verId) ?? all.FirstOrDefault();
        if (v is null)
        {
            Console.WriteLine("[modscan] 本地没有版本");
            return;
        }

        var dir = Core.VersionSettings.InstalledModsDir(cfg, v);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mods = Core.ModScanner.Scan(dir);
        sw.Stop();

        Console.WriteLine($"[modscan] 版本={v.Id} 隔离={cfg.Isolation}");
        Console.WriteLine($"[modscan] 目录={dir}");
        Console.WriteLine($"[modscan] {mods.Count} 个文件 ({mods.Count(m => m.Enabled)} 启用 / {mods.Count(m => !m.Enabled)} 禁用), {sw.ElapsedMilliseconds}ms");
        foreach (var m in mods)
            Console.WriteLine($"[modscan] {(m.Enabled ? "✓" : "·")} {m.Name} | {m.Version} | {m.Loader} | {m.FileName}");
    }

    /// <summary>
    /// 自定义主页链路实测：占位符替换 → 远程拉取 → 缓存写入 → 缓存回读 → 坏 URL 不抛异常。
    /// </summary>
    private static async Task RunHomeTest()
    {
        var cfg = Core.LauncherConfig.Load();
        var ct = CancellationToken.None;

        var all = Core.VersionScanner.Scan(cfg.MinecraftPath);
        var testVer = all.FirstOrDefault(v => v.Id.Contains("1.20.1")) ?? all.FirstOrDefault();

        Console.WriteLine("[home] 1) 占位符替换:");
        var raw = "日期=${date} 时间=${time} 玩家=${player} 版本=${version} 内存=${memory} Java=${java} 启动器=${launcher}";
        Console.WriteLine("   " + Core.HomePageLoader.Render(raw, cfg, testVer));

        const string testUrl = "https://cnb.cool/HMCL-Terracotta/Terracotta/-/raw/main/README.md";
        Console.WriteLine($"[home] 2) 拉远程: {testUrl}");
        var (text, fromNet) = await Core.HomePageLoader.FetchAsync(testUrl, ct).ConfigureAwait(false);
        Console.WriteLine($"   ok={fromNet} 长度={text?.Length ?? 0}");
        if (text is not null)
            Console.WriteLine($"   首行: {text.Split('\n')[0].Trim()}");

        Console.WriteLine("[home] 3) 回读缓存:");
        var cached = Core.HomePageLoader.LoadCache(testUrl);
        Console.WriteLine($"   {(cached is null ? "null（错，应该写到缓存了）" : cached.Length + " 字符（对）")}");

        Console.WriteLine("[home] 4) 缓存 URL 不匹配时应返回 null:");
        var wrong = Core.HomePageLoader.LoadCache("https://example.com/other.txt");
        Console.WriteLine($"   {(wrong is null ? "null（对）" : "有值（错）")}");

        Console.WriteLine("[home] 5) 坏 URL 不应抛异常:");
        var (bad, badOk) = await Core.HomePageLoader.FetchAsync(
            "https://this-domain-really-does-not-exist-lll.invalid/x.txt", ct).ConfigureAwait(false);
        Console.WriteLine($"   ok={badOk} text={(bad is null ? "null" : "有值")}  → 期望 ok=False text=null");

        Console.WriteLine("[home] 6) 本地文件不存在:");
        Console.WriteLine("   " + Core.HomePageLoader.LoadLocal(@"D:\no\such\file.txt"));

        Console.WriteLine("[home] 7) 预设主页:");
        var preset = Core.HomePageLoader.PresetText(cfg, testVer);
        Console.WriteLine("   " + preset.Replace("\n", "\n   ").TrimEnd());

        Console.WriteLine("[home] 8) 每个版本的实际内存/Java（要和启动时一致）:");
        foreach (var v in all)
        {
            var (mem, javaText) = Core.HomePageLoader.ResolveRuntime(cfg, v);
            var mods = Core.VersionSettings.CountMods(cfg, v);
            Console.WriteLine($"   {v.Id,-32} 需要Java{v.RequiredJava,-3} mod数{mods,-4} 内存 {mem,-16} {javaText}");
        }
    }

    /// <summary>
    /// 会话记录验证：写记录 → 重新读（模拟启动器重启）→ 篡改启动时间（模拟 PID 复用）→
    /// 杀进程后再读（应该自愈清掉）。用 ping 当假游戏，不依赖 MC。
    /// </summary>
    private static async Task RunSessionTest()
    {
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        using var proc = Core.DetachedProcess.Start(ping, new[] { "-n", "20", "127.0.0.1" },
            Environment.SystemDirectory);
        Console.WriteLine($"[session] 假游戏 PID={proc.Pid}（ping）");

        Core.GameSessionStore.Save(new Core.GameSession
        {
            Pid = proc.Pid,
            StartTicksUtc = proc.StartedAt.ToUniversalTime().Ticks,
            ProcessName = "ping",
            VersionId = "test",
            VersionName = "测试版本 1.20.1",
            LogPath = "",
            GameDir = "",
        });
        Console.WriteLine("[session] 已写 running-game.json");

        var live = Core.GameSessionStore.LoadAlive();
        Console.WriteLine(live is null
            ? "[session] 重启后重读: null  → 期望有值，这里错了"
            : $"[session] 重启后重读: {live.VersionName} PID={live.Pid}  → 期望有值，正确");

        // 模拟 PID 被别的进程复用：启动时间对不上就该判死
        var tampered = Core.GameSessionStore.Load();
        if (tampered is not null)
        {
            tampered.StartTicksUtc += TimeSpan.FromMinutes(5).Ticks;
            Console.WriteLine($"[session] 篡改启动时间后 IsAlive: {Core.GameSessionStore.IsAlive(tampered)}  → 期望 False");
        }

        proc.Kill();
        await Task.Delay(1200).ConfigureAwait(false);
        Console.WriteLine(Core.GameSessionStore.LoadAlive() is null
            ? "[session] 进程被杀后 LoadAlive: null  → 期望 null（自愈清掉），正确"
            : "[session] 进程被杀后 LoadAlive: 还有值  → 错了");
    }

    /// <summary>
    /// 验证 DETACHED_PROCESS 真的把子进程从控制台摘下去了。
    /// 判断方法：<c>GetConsoleProcessList</c> 列出"当前控制台里的进程"，
    /// 子进程出现在里面 = 还挂在控制台上（关窗口会连带死），不在 = 脱离成功。
    /// 顺便验证 stdout 管道还能读（detach 不影响重定向）。
    /// </summary>
    private static async Task RunDetachTest()
    {
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        if (!File.Exists(ping))
        {
            Console.Error.WriteLine($"[detach] 找不到 {ping}");
            return;
        }

        var consoleBefore = Core.GameWindowFinder.ConsoleProcesses();
        Console.WriteLine($"[detach] 本进程 PID={Environment.ProcessId}，" +
                          $"当前控制台里 {consoleBefore.Length} 个进程: {string.Join(",", consoleBefore)}");

        var lines = new List<string>();
        using var proc = Core.DetachedProcess.Start(ping, new[] { "-n", "20", "127.0.0.1" }, Environment.SystemDirectory);
        proc.OutputLine += line =>
        {
            lock (lines) lines.Add(line);
        };
        Console.WriteLine($"[detach] 已启动子进程 PID={proc.Pid}");

        await Task.Delay(2000).ConfigureAwait(false);

        // 关键判定
        var consoleNow = Core.GameWindowFinder.ConsoleProcesses();
        var inConsole = Array.IndexOf(consoleNow, (uint)proc.Pid) >= 0;
        Console.WriteLine($"[detach] 2 秒后控制台进程: {string.Join(",", consoleNow)}");
        Console.WriteLine($"[detach] 子进程是否挂在当前控制台: {inConsole}  → 期望 False");
        Console.WriteLine($"[detach] 子进程存活: {!proc.HasExited}  → 期望 True");

        // stdout 是否可读
        int count;
        string sample;
        lock (lines)
        {
            count = lines.Count;
            sample = lines.FirstOrDefault() ?? "(无)";
        }
        Console.WriteLine($"[detach] 已读到 {count} 行输出，首行: {sample}  → 期望 >0 行");

        proc.Kill();
        await Task.Delay(500).ConfigureAwait(false);
        Console.WriteLine($"[detach] Kill 后 HasExited: {proc.HasExited}  → 期望 True");
        Console.WriteLine(inConsole ? "[detach] 结论: 失败（仍挂在控制台上）" : "[detach] 结论: 成功（已脱离控制台）");
    }

    /// <summary>
    /// 陶瓦联机端到端实测：解压内嵌包 → 起进程 → 拿端口 → 拉状态 → 开房 → 复位 → 停。
    /// 不开 UI，纯 Core 链路验证。
    /// </summary>
    private static async Task RunTerracottaTest()
    {
        var task = new Core.TaskItem("陶瓦联机测试");
        task.Changed += () =>
        {
            // 把子进程输出打到控制台
        };

        using var svc = new Core.TerracottaService();
        var ct = CancellationToken.None;
        svc.LogLine += line => Console.WriteLine($"[out] {line}");

        try
        {
            Console.WriteLine("[tc] 1) 解压内嵌包…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var exe = await Core.TerracottaService.EnsureInstalledAsync(task, ct).ConfigureAwait(false);
            sw.Stop();
            Console.WriteLine($"[tc]    {exe}");
            Console.WriteLine($"[tc]    {sw.ElapsedMilliseconds}ms，存在={File.Exists(exe)}");

            Console.WriteLine("[tc] 2) 启动进程…");
            sw.Restart();
            var port = await svc.StartAsync(task, ct).ConfigureAwait(false);
            sw.Stop();
            Console.WriteLine($"[tc]    端口 {port}（{sw.ElapsedMilliseconds}ms）");

            Console.WriteLine("[tc] 3) 拉状态…");
            var s0 = await svc.GetStateAsync(ct).ConfigureAwait(false);
            Console.WriteLine($"[tc]    {s0?.State} index={s0?.Index} 「{s0?.StateText}」");

            Console.WriteLine("[tc] 4) 拉节点列表…");
            var nodes = await Core.TerracottaService.FetchNodesAsync(ct).ConfigureAwait(false);
            Console.WriteLine($"[tc]    {nodes.Count} 个: {string.Join(", ", nodes)}");

            Console.WriteLine("[tc] 5) 请求开房（会扫本机 MC 世界），然后盯 30 秒看会不会自己停…");
            await svc.HostAsync("TestPlayer", nodes, ct).ConfigureAwait(false);
            for (var i = 1; i <= 15; i++)
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
                if (!svc.IsRunning)
                {
                    Console.WriteLine($"[tc]    !!! 第 {i * 2} 秒发现进程已经没了");
                    break;
                }
                var si = await svc.GetStateAsync(ct).ConfigureAwait(false);
                Console.WriteLine($"[tc]    {i * 2,2}s: {si?.State} index={si?.Index} 「{si?.StateText}」");
            }

            Console.WriteLine("[tc] 6) 复位…");
            await svc.SetWaitingAsync(ct).ConfigureAwait(false);
            await Task.Delay(800, ct).ConfigureAwait(false);
            var s2 = await svc.GetStateAsync(ct).ConfigureAwait(false);
            Console.WriteLine($"[tc]    {s2?.State} index={s2?.Index}");

            Console.WriteLine("[tc] 7) 导出日志…");
            var log = await svc.ExportLogAsync(ct).ConfigureAwait(false);
            Console.WriteLine($"[tc]    日志 {log.Length} 字符，前 120: {log[..Math.Min(120, log.Length)].Replace('\n', ' ')}");

            Console.WriteLine("[tc] 8) 许可证…");
            var lic = Core.TerracottaService.ReadLicense();
            Console.WriteLine($"[tc]    {lic.Length} 字符，首行: {lic.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim()}");

            Console.WriteLine("[tc] 9) 停止…");
            svc.Stop();
            Console.WriteLine($"[tc]    已停止，IsRunning={svc.IsRunning}");
            Console.Error.WriteLine("[tc] done");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tc] EX: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
        }
    }

    /// <summary>
    /// 「千万别点」彩蛋冒烟：把 7 种整蛊形态挨个跑一遍，每种 1.8 秒后自动关门。
    /// 主要抓"定时器访问已销毁控件"这类崩（这些形态都靠 AddTimeout 驱动）。
    /// </summary>
    private static void RunPrankTest()
    {
        Application.Init();
        var win = new MainWindow();
        win.ShowPage(PageId.More);

        var names = new[]
        {
            "FakeBsod", "FakeDeleteAll", "GarbageScreen",
            "FakeWindowsUpdate", "FakeCmdFlood", "FakeCrashedDialog", "FakeRansomNote",
        };
        var methods = typeof(UI.MorePage)
            .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .Where(m => names.Contains(m.Name))
            .OrderBy(m => Array.IndexOf(names, m.Name))
            .ToList();
        Console.Error.WriteLine($"[prank] 找到 {methods.Count}/{names.Length} 个形态");

        Application.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            foreach (var m in methods)
            {
                try
                {
                    // 先安排好"到点关门"，再进模态（形态内部的 Application.Run 会阻塞）
                    Application.AddTimeout(TimeSpan.FromMilliseconds(1800), () =>
                    {
                        try { Application.RequestStop(); }
                        catch { /* 已经关了 */ }
                        return false;
                    });
                    m.Invoke(null, null);
                    Console.Error.WriteLine($"[prank] {m.Name} ok");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[prank] {m.Name} EX: {ex.InnerException?.Message ?? ex.Message}");
                }
            }
            Application.RequestStop();
            return false;
        });

        // 兜底：万一某个形态卡住，30 秒后强停
        Application.AddTimeout(TimeSpan.FromMilliseconds(30000), () =>
        {
            Console.Error.WriteLine("[prank] 超时强停");
            Application.RequestStop();
            return false;
        });

        Application.Run(win);
        Application.Shutdown();
        Console.Error.WriteLine("[prank] done");
    }

    /// <summary>
    /// 版本管理页冒烟：打开对话框 → 依次切到 4 个导航页（概览/设置/Mod管理/补全）→ 关掉。
    /// 目的是抓 TG 布局类崩溃（Pos.Right(null)、FrameView 裁剪、控件构造异常）。
    /// </summary>
    private static void RunVersionManagerTest()
    {
        Application.Init();
        var win = new MainWindow();
        win.ShowPage(PageId.Launch);

        var pages = typeof(MainWindow).GetField("_pages",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(win)!;
        var lp = ((Dictionary<PageId, PageBase>)pages)[PageId.Launch];

        var selField = typeof(LaunchPage).GetField("_selected",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var selected = selField.GetValue(lp) as Core.MinecraftVersion;
        if (selected is null)
        {
            var all = Core.VersionScanner.Scan(Core.LauncherConfig.Load().MinecraftPath);
            selected = all.FirstOrDefault();
        }
        if (selected is null)
        {
            Console.Error.WriteLine("[vm] 本地没有任何已安装版本, 跳过");
            Application.Shutdown();
            return;
        }
        Console.Error.WriteLine($"[vm] 目标版本: {selected.Id}");

        var navTitles = new List<string>();
        var navIndex = 0;

        // 打开对话框（内部 Application.Run 是嵌套模态，会阻塞这个回调直到关掉）
        Application.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            Console.Error.WriteLine("[vm] 打开版本管理页…");
            try
            {
                typeof(LaunchPage).GetMethod("ShowVersionManager",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(lp, new object[] { selected });
                Console.Error.WriteLine("[vm] 对话框关闭 ok");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vm] EX: {ex}");
            }
            return false;
        });

        // 对话框打开后，每隔 300ms 切一个导航页
        for (var i = 0; i < 6; i++)
        {
            var target = i;
            Application.AddTimeout(TimeSpan.FromMilliseconds(700 + i * 300), () =>
            {
                var dlg = LaunchPage.OpenManagerDialog;
                if (dlg is null)
                {
                    Console.Error.WriteLine($"[vm] 第 {target} 页: 对话框还没打开");
                    return false;
                }
                var nav = dlg.SubViews.OfType<ListView<string>>().FirstOrDefault();
                if (nav is null)
                {
                    Console.Error.WriteLine($"[vm] 第 {target} 页: 找不到导航列表");
                    return false;
                }
                var items = nav.Source?.ToList().Cast<object>().Select(o => o?.ToString() ?? "").ToList();
                if (items is null || items.Count <= target)
                {
                    Console.Error.WriteLine($"[vm] 第 {target} 页: 导航项不足 ({items?.Count})");
                    return false;
                }
                nav.SelectedItem = items[target];
                Console.Error.WriteLine($"[vm] 第 {target} 页 [{items[target]}] ok, 内容子视图 {dlg.SubViews.Count} 个");
                navIndex = target;
                return false;
            });
        }

        Application.AddTimeout(TimeSpan.FromMilliseconds(2600), () =>
        {
            Console.Error.WriteLine($"[vm] 关对话框 (最后停在第 {navIndex} 页)");
            Application.RequestStop();
            return false;
        });
        Application.AddTimeout(TimeSpan.FromMilliseconds(3000), () =>
        {
            Application.RequestStop();
            return false;
        });

        Application.Run(win);
        navTitles.ForEach(t => Console.Error.WriteLine($"[vm] 见过: {t}"));
        Application.Shutdown();
        Console.Error.WriteLine("[vm] done");
    }

    /// <summary>在视图树里找最上层的 Dialog</summary>
    private static Dialog? FindDialog(View? root)
    {
        if (root is null)
            return null;
        if (root is Dialog d)
            return d;
        foreach (var sub in root.SubViews)
        {
            var hit = FindDialog(sub);
            if (hit is not null)
                return hit;
        }
        return null;
    }

    /// <summary>安装页冒烟：打开"双击版本 -> 安装页"的对话框后自动关闭，验证构造和布局。</summary>
    private static void RunInstallDialogTest()
    {
        Application.Init();
        var win = new MainWindow();
        win.ShowPage(PageId.Download);

        var pages = typeof(MainWindow).GetField("_pages",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(win)!;
        var dp = ((System.Collections.Generic.Dictionary<PageId, PageBase>)pages)[PageId.Download];

        // 造一个 manifest 条目 (只用 Id, 不会真下载, 对话框会被立刻关掉)
        var entry = new Core.VersionEntry { Id = "1.20.1", TypeRaw = "release", Url = "" };

        Application.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            typeof(DownloadPage).GetMethod("PromptInstallWithLoader",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(dp, new object[] { entry });
            Console.Error.WriteLine("[install] dialog opened + closed ok");
            return false;
        });
        Application.AddTimeout(TimeSpan.FromMilliseconds(1100), () =>
        {
            Application.RequestStop();
            return false;
        });
        Application.AddTimeout(TimeSpan.FromMilliseconds(1500), () =>
        {
            Application.RequestStop();
            return false;
        });

        Application.Run(win);
        Application.Shutdown();
        Console.Error.WriteLine("[install] done");
    }

    /// <summary>下载器实测: 拉真实清单 + 真下载, 用于验证 P1 链路</summary>
    private static async Task RunDownloadTest(string[] args)
    {
        var i = Array.IndexOf(args, "--dltest");
        var id = i >= 0 && i + 1 < args.Length ? args[i + 1] : "1.0";
        var dir = Path.Combine(Path.GetTempPath(), "lht-dltest", id);

        Console.Error.WriteLine($"[dl] fetching manifest...");
        var manifest = await Core.MojangApi.FetchAsync("auto", Core.LauncherConfig.Load().IgnoreSslErrors);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == id)
                    ?? throw new Exception($"版本 {id} 不在 manifest 中");
        Console.Error.WriteLine($"[dl] version={entry.Id} type={entry.Type} url={entry.Url}");

        var task = Core.TaskManager.Submit("dltest " + id, (_, _) => Task.CompletedTask);
        var files = await Core.GameDownloader.BuildPlanAsync(entry, dir, "auto", null, CancellationToken.None);
        Console.Error.WriteLine($"[dl] plan: {files.Count} files / {files.Sum(f => f.Size) / 1048576.0:0.0} MB");
        Console.Error.WriteLine($"[dl] sample: {files[0].Url}");
        var limit = args.Contains("--limit") ? int.Parse(args[Array.IndexOf(args, "--limit") + 1]) : 0;
        if (limit > 0)
            files = files.Take(limit).ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Core.GameDownloader.DownloadAsync(files, task, CancellationToken.None);
        sw.Stop();
        var jar = Path.Combine(dir, "versions", id, id + ".jar");
        Console.Error.WriteLine($"[dl] done in {sw.ElapsedMilliseconds}ms, jar={File.Exists(jar)} size={(File.Exists(jar) ? new FileInfo(jar).Length : 0)}");
    }

    /// <summary>
    /// Mod 更新对话框的布局冒烟：造几条假更新，开框 2 秒后自动关。
    /// 只验布局/勾选逻辑不崩，不下载任何东西。
    /// </summary>
    private static void RunUpdateDialogTest()
    {
        Application.Init();
        try
        {
            var cfg = Core.LauncherConfig.Load();
            var page = new UI.LaunchPage(cfg);
            var method = typeof(UI.LaunchPage).GetMethod("ShowUpdateDialog",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method is null)
            {
                Console.Error.WriteLine("[upddlg] 找不到 ShowUpdateDialog（改名了？）");
                Environment.ExitCode = 1;
                return;
            }

            var updates = new List<Core.ModUpdateInfo>();
            for (var i = 1; i <= 4; i++)
            {
                var name = $"[测试mod{i}] testmod-1.{i}.0.jar";
                updates.Add(new Core.ModUpdateInfo
                {
                    Mod = new Core.ModInfo
                    {
                        FilePath = Path.Combine(Path.GetTempPath(), name),
                        FileName = name,
                        EnabledName = name,
                        Enabled = i % 2 == 0,
                        Name = $"测试mod{i}",
                        Size = 1024 * i,
                        Modified = DateTime.Now,
                    },
                    NewFile = new Core.RemoteFile
                    {
                        ProjectId = "test",
                        FileName = i == 1 ? name : $"testmod-1.{i}.1.jar", // 第 1 条验「同名原地替换」
                        DisplayName = $"testmod-1.{i}.1",
                        Url = "https://example.com/x.jar",
                        Size = 2 * 1024 * 1024 * i,
                        Sha1 = "deadbeef",
                        VersionType = "release",
                        GameVersions = new List<string> { "1.20.1" },
                        Loaders = new List<string> { "forge" },
                    },
                    OldSha1 = "cafe",
                });
            }

            Application.AddTimeout(TimeSpan.FromMilliseconds(1200), () =>
            {
                // 打印底部按钮的实际尺寸：文字被挤到第二行时内宽会小于文字列数
                try
                {
                    var top = Application.Instance?.TopRunnableView;
                    foreach (var v in top?.SubViews ?? new List<View>())
                    {
                        if (v is not Button b)
                            continue;
                        var f = b.Frame;
                        var cols = (b.Text ?? "").GetColumns();
                        var inner = f.Width - 2; // 左右边框各 1 列
                        Console.Error.WriteLine(
                            $"[upddlg]   按钮「{b.Text}」 x={f.X,3} w={f.Width,3} h={f.Height} " +
                            $"文字={cols} 内宽={inner} {(inner < cols ? "★会被挤" : "")}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[upddlg] 尺寸诊断失败: " + ex.Message);
                }
                return false;
            });
            Application.AddTimeout(TimeSpan.FromMilliseconds(2000), () =>
            {
                try { Application.RequestStop(); }
                catch { /* 已经关了 */ }
                return false;
            });

            try
            {
                method.Invoke(page, new object[] { updates, Path.GetTempPath(), (Action)(() => { }) });
                Console.Error.WriteLine("[upddlg] 更新对话框打开/关闭正常");
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                Console.Error.WriteLine("[upddlg] 抛异常: " + ex.InnerException?.Message);
                Environment.ExitCode = 1;
            }
        }
        finally
        {
            Application.Shutdown();
        }
    }

    /// <summary>
    /// 社区项目详情框的冒烟测试：造三个假版本（有依赖 / 没依赖 / 依赖名查不到），
    /// 打开详情框 2 秒后自动关掉。主要抓布局崩（FrameView 裁剪、Pos.Bottom 空引用）
    /// 和「切换选中版本时依赖列表重建」那段。
    /// </summary>
    private static void RunProjectDetailTest()
    {
        Application.Init();
        try
        {
            var cfg = Core.LauncherConfig.Load();
            var all = Core.VersionScanner.Scan(cfg.MinecraftPath);
            if (all.Count == 0)
            {
                Console.Error.WriteLine("[detail] 没有游戏版本，跳过");
                return;
            }
            var inst = all[0];

            var page = new UI.DownloadPage(cfg);
            var method = typeof(UI.DownloadPage).GetMethod("ShowProjectDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method is null)
            {
                Console.Error.WriteLine("[detail] 找不到 ShowProjectDetail（改名了？）");
                Environment.ExitCode = 1;
                return;
            }

            var project = new Core.RemoteProject
            {
                Id = "testproject",
                Slug = "testproject",
                Title = "测试项目",
                Description = "依赖框冒烟测试用",
                Author = "tester",
                Downloads = 123456,
                Source = "modrinth",
                GameVersions = new List<string> { "1.20.1" },
            };

            var files = new List<Core.RemoteFile>
            {
                new()
                {
                    ProjectId = project.Id,
                    Url = "https://example.com/a.jar",
                    FileName = "a-1.2.0.jar",
                    DisplayName = "a-1.2.0（一个必选 + 两个可选，其中 fabricapi 重复出现在两边）",
                    Size = 1024 * 1024,
                    Sha1 = "aaaa",
                    GameVersions = new List<string> { "1.20.1" },
                    Loaders = new List<string> { "forge" },
                    RequiredDependencies = new List<string> { "P7dR8mSH", "AANobbMI" },
                    OptionalDependencies = new List<string> { "P7dR8mSH", "unknownid" },
                    VersionType = "release",
                },
                new()
                {
                    ProjectId = project.Id,
                    Url = "https://example.com/b.jar",
                    FileName = "b-1.0.0.jar",
                    DisplayName = "b-1.0.0（没有任何依赖）",
                    Size = 512 * 1024,
                    Sha1 = "bbbb",
                    GameVersions = new List<string> { "1.20.1" },
                    Loaders = new List<string> { "forge" },
                    VersionType = "release",
                },
                new()
                {
                    ProjectId = project.Id,
                    Url = "https://example.com/c.jar",
                    FileName = "c-2.0.0.jar",
                    DisplayName = "c-2.0.0（依赖名一个都查不到，应该退回显示 id）",
                    Size = 2 * 1024 * 1024,
                    Sha1 = "cccc",
                    GameVersions = new List<string> { "1.18.2" },
                    Loaders = new List<string> { "forge" },
                    RequiredDependencies = new List<string> { "zzz111", "yyy222" },
                    VersionType = "beta",
                },
            };

            // 只给了两个名字，剩下的故意不填，验「查不到就显示 id」
            var depNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["P7dR8mSH"] = "Fabric API",
                ["AANobbMI"] = "Sodium",
            };

            // 到点自动关框（详情框内部是 Application.Run，会阻塞）
            Application.AddTimeout(TimeSpan.FromMilliseconds(1200), () =>
            {
                // 顺手把布局坐标打出来：溢出/裁剪在 TG 里不会抛异常，只能这么看
                try
                {
                    var rows = Application.Driver?.Rows ?? 0;
                    var cols = Application.Driver?.Cols ?? 0;
                    Console.Error.WriteLine($"[detail] 终端 {cols}x{rows}");
                    var top = Application.Instance?.TopRunnableView;
                    if (top is not null)
                        Console.Error.WriteLine($"[detail] 容器 {top.GetType().Name} y={top.Frame.Y} h={top.Frame.Height} w={top.Frame.Width}");
                    foreach (var v in top?.SubViews ?? new List<View>())
                    {
                        var f = v.Frame;
                        var clipped = f.Y + f.Height > rows + 1;
                        Console.Error.WriteLine(
                            $"[detail]   {v.GetType().Name,-12} y={f.Y,3} h={f.Height,3} " +
                            $"底={f.Y + f.Height,3} {(clipped ? "★超出屏幕" : "")}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[detail] 布局诊断失败: " + ex.Message);
                }
                return false;
            });
            Application.AddTimeout(TimeSpan.FromMilliseconds(2000), () =>
            {
                try { Application.RequestStop(); }
                catch { /* 已经关了 */ }
                return false;
            });

            try
            {
                method.Invoke(page, new object[] { project, files, inst, Core.AddonType.Mod, depNames });
                Console.Error.WriteLine("[detail] 详情框打开/关闭正常（含依赖框）");
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                Console.Error.WriteLine("[detail] 打开详情框抛异常: " + ex.InnerException?.Message);
                foreach (var line in Core.Net.Describe(ex.InnerException ?? ex).Split('\n'))
                    Console.Error.WriteLine("[detail] " + line.TrimEnd());
                Environment.ExitCode = 1;
            }
        }
        finally
        {
            Application.Shutdown();
        }
    }

    /// <summary>
    /// 资源包/光影启用状态的自测：在临时目录里造假的 options.txt / optionsshaders.txt，
    /// 验证读、写、保留其它条目、历史 .disabled 迁移、坏 JSON 不崩。
    /// </summary>
    private static void RunPackConfigTest()
    {
        var pass = 0;
        var fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) { pass++; Console.Error.WriteLine($"[pack]   OK   {name}"); }
            else { fail++; Console.Error.WriteLine($"[pack]   FAIL {name} {detail}"); }
        }

        var dir = Path.Combine(Path.GetTempPath(), "lll-pack-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var optPath = Path.Combine(dir, "options.txt");

        // 造一份「游戏已经写过」的 options.txt：别的键都得原样保留
        File.WriteAllText(optPath,
            "version:3465\n" +
            "fov:0.0\n" +
            "resourcePacks:[\"vanilla\"]\n" +
            "incompatibleResourcePacks:[]\n" +
            "lang:zh_cn\n");

        Console.Error.WriteLine("[pack] 1) 初始状态（只有 vanilla）");
        var initial = Core.PackConfig.ReadEnabledPacks(dir);
        Check("没有本地包被启用", initial.Count == 0, $"count={initial.Count}");

        Console.Error.WriteLine("[pack] 2) 启用两个资源包");
        Core.PackConfig.SetPackEnabled(dir, "Better Leaves.zip", true, out _);
        Core.PackConfig.SetPackEnabled(dir, "中文修复", true, out _);
        var after = Core.PackConfig.ReadEnabledPacks(dir);
        Check("两个都读回来了", after.Contains("Better Leaves.zip") && after.Contains("中文修复"),
            string.Join("|", after));

        var text = File.ReadAllText(optPath);
        Check("vanilla 还在列表里", text.Contains("\"vanilla\""));
        Check("其它键没被动", text.Contains("version:3465") && text.Contains("lang:zh_cn")
                                && text.Contains("incompatibleResourcePacks:[]"));

        Console.Error.WriteLine("[pack] 3) 关掉其中一个");
        Core.PackConfig.SetPackEnabled(dir, "Better Leaves.zip", false, out _);
        var after2 = Core.PackConfig.ReadEnabledPacks(dir);
        Check("只删了指定的那个", !after2.Contains("Better Leaves.zip") && after2.Contains("中文修复"),
            string.Join("|", after2));

        Console.Error.WriteLine("[pack] 4) 重复关闭同一个包（应该无害）");
        var okRepeat = Core.PackConfig.SetPackEnabled(dir, "不存在.zip", false, out _);
        Check("关一个本来就没启用的包返回 true", okRepeat);

        Console.Error.WriteLine("[pack] 5) 历史 .disabled 改名迁移");
        File.WriteAllText(Path.Combine(dir, "OldPack.zip.disabled"), "x");
        var healed = Core.PackConfig.MigrateDisabledNames(dir);
        Check("还原了 1 个", healed == 1, $"healed={healed}");
        Check("文件名回来了", File.Exists(Path.Combine(dir, "OldPack.zip")));
        Check("迁移后不算启用", !Core.PackConfig.ReadEnabledPacks(dir).Contains("OldPack.zip"));

        Console.Error.WriteLine("[pack] 6) options.txt 里的 JSON 被手改坏");
        File.WriteAllText(optPath, "resourcePacks:[\"vanilla\",broken\n");
        var broken = Core.PackConfig.ReadEnabledPacks(dir);
        Check("坏 JSON 不崩，返回空", broken.Count == 0);
        Core.PackConfig.SetPackEnabled(dir, "X.zip", true, out _);
        Check("坏 JSON 能被写回正常格式", Core.PackConfig.ReadEnabledPacks(dir).Contains("X.zip"));

        Console.Error.WriteLine("[pack] 7) 光影（独占，写 optionsshaders.txt）");
        Check("初始没有生效光影", Core.PackConfig.ReadActiveShader(dir) is null);
        Core.PackConfig.SetActiveShader(dir, "SEUS-v11.zip", out _);
        Check("读回生效光影", Core.PackConfig.ReadActiveShader(dir) == "SEUS-v11.zip");
        var shaderText = File.ReadAllText(Path.Combine(dir, "optionsshaders.txt"));
        Check("配置文件里是 shaderPack=", shaderText.Contains("shaderPack=SEUS-v11.zip"), shaderText.Trim());

        Console.Error.WriteLine("[pack] 8) 换一份光影 / 关掉光影");
        Core.PackConfig.SetActiveShader(dir, "Complementary.zip", out _);
        Check("换成新的了", Core.PackConfig.ReadActiveShader(dir) == "Complementary.zip");
        Core.PackConfig.SetActiveShader(dir, null, out _);
        Check("关掉后读回 null", Core.PackConfig.ReadActiveShader(dir) is null);
        Check("关掉写的是 OFF",
            File.ReadAllText(Path.Combine(dir, "optionsshaders.txt")).Contains("shaderPack=OFF"));

        Console.Error.WriteLine("[pack] 9) 同时存在 iris.properties 时两边都写");
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        var iris = Path.Combine(dir, "config", "iris.properties");
        File.WriteAllText(iris, "enableShaders=true\nshaderPack=OFF\n");
        Core.PackConfig.SetActiveShader(dir, "BSL.zip", out _);
        Check("iris 也写到了", File.ReadAllText(iris).Contains("shaderPack=BSL.zip"));
        Check("iris 的其它键保留", File.ReadAllText(iris).Contains("enableShaders=true"));

        try { Directory.Delete(dir, recursive: true); } catch { /* 删不掉无所谓 */ }

        Console.Error.WriteLine($"[pack] === {pass} 通过 / {fail} 失败 ===");
        Environment.ExitCode = fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// Mod 更新检查实测：<c>--updatetest &lt;版本id&gt; [--apply]</c>。
    /// 用真实的 mods 目录算 SHA1 打 Modrinth，把「有更新」的列出来；带 --apply 就真下载替换。
    /// </summary>
    private static async Task RunUpdateTest(string[] args)
    {
        var cfg = Core.LauncherConfig.Load();
        var all = Core.VersionScanner.Scan(cfg.MinecraftPath);
        var idIdx = Array.IndexOf(args, "--updatetest") + 1;
        var wanted = idIdx < args.Length && !args[idIdx].StartsWith("--") ? args[idIdx] : null;
        var ver = (wanted is null ? all.FirstOrDefault() : all.FirstOrDefault(v => v.Id == wanted))
                  ?? all.FirstOrDefault();
        if (ver is null)
        {
            Console.Error.WriteLine("[upd] 没有可用版本");
            Environment.ExitCode = 1;
            return;
        }

        var modsDir = Core.VersionSettings.InstalledModsDir(cfg, ver);
        var baseMc = Core.VersionHelper.ResolveBaseVersion(ver, all);
        var loader = Core.GameLauncher.DetectLoader(ver);
        Console.Error.WriteLine($"[upd] 版本 {ver.Id}  MC {baseMc}  加载器 {loader ?? "原版"}");
        Console.Error.WriteLine($"[upd] mods: {modsDir}");

        var mods = Core.ModScanner.Scan(modsDir);
        Console.Error.WriteLine($"[upd] 扫到 {mods.Count} 个文件");

        var task = new Core.TaskItem("更新检查测试");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var updates = await Core.ModUpdateChecker
            .CheckAsync(mods, baseMc, loader, task, CancellationToken.None).ConfigureAwait(false);
        sw.Stop();
        foreach (var line in task.LogText.Split('\n'))
            Console.Error.WriteLine("[upd] " + line);
        Console.Error.WriteLine($"[upd] 耗时 {sw.ElapsedMilliseconds}ms，可更新 {updates.Count} 个");
        foreach (var u in updates.Take(20))
            Console.Error.WriteLine($"[upd]   {u.Display}");

        if (args.Contains("--apply-one"))
        {
            // 拿最小的那个更新做「沙箱演练」：把老文件复制到临时目录里再替换，绝不碰真实 mods 目录。
            // 验的是 ApplyAsync 那段（下载 → 校验 → 改名 → 删旧）, 这段才是有破坏性的。
            var u = updates.OrderBy(x => x.NewFile.Size).First();
            var sandbox = Path.Combine(Path.GetTempPath(), "lll-upd-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            var oldCopy = Path.Combine(sandbox, u.Mod.EnabledName);
            File.Copy(u.Mod.FilePath, oldCopy);
            Console.Error.WriteLine($"[upd] 沙箱目录 {sandbox}");
            Console.Error.WriteLine($"[upd] 老文件已复制: {Path.GetFileName(oldCopy)} ({new FileInfo(oldCopy).Length / 1024} KB)");

            var fakeMod = new Core.ModInfo
            {
                FilePath = oldCopy,
                FileName = u.Mod.EnabledName,
                EnabledName = u.Mod.EnabledName,
                Enabled = true,
                Name = u.Mod.Name,
                Size = u.Mod.Size,
                Modified = DateTime.Now,
            };
            var dry = new List<Core.ModUpdateInfo>
            {
                new() { Mod = fakeMod, NewFile = u.NewFile, OldSha1 = u.OldSha1 },
            };

            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            var (ok1, fail1) = await Core.ModUpdateChecker
                .ApplyAsync(dry, task, CancellationToken.None).ConfigureAwait(false);
            sw2.Stop();
            Console.Error.WriteLine($"[upd] 演练结果：成功 {ok1} 失败 {fail1}，耗时 {sw2.ElapsedMilliseconds}ms");

            var after = Directory.GetFiles(sandbox);
            Console.Error.WriteLine($"[upd] 目录里现在 {after.Length} 个文件:");
            foreach (var f in after)
                Console.Error.WriteLine($"[upd]   {Path.GetFileName(f)}  {new FileInfo(f).Length / 1024} KB");

            var newPath = Path.Combine(sandbox, u.NewFile.FileName);
            var exists = File.Exists(newPath);
            var oldGone = !File.Exists(oldCopy)
                          || string.Equals(oldCopy, newPath, StringComparison.OrdinalIgnoreCase);
            Console.Error.WriteLine($"[upd]   新文件存在: {exists}  期望 True");
            Console.Error.WriteLine($"[upd]   老文件已删: {oldGone}  期望 True");
            if (exists && !string.IsNullOrEmpty(u.NewFile.Sha1))
            {
                var h = await Core.ModUpdateChecker.Sha1OfAsync(newPath, CancellationToken.None)
                    .ConfigureAwait(false);
                Console.Error.WriteLine($"[upd]   新文件 SHA1 匹配: " +
                    $"{string.Equals(h, u.NewFile.Sha1, StringComparison.OrdinalIgnoreCase)}  期望 True");
            }
            try { Directory.Delete(sandbox, recursive: true); } catch { /* 删不掉无所谓 */ }
            return;
        }

        if (!args.Contains("--apply") || updates.Count == 0)
            return;

        Console.Error.WriteLine("[upd] 开始实际更新…");
        var (ok, fail) = await Core.ModUpdateChecker
            .ApplyAsync(updates, task, CancellationToken.None).ConfigureAwait(false);
        Console.Error.WriteLine($"[upd] 结果：成功 {ok}，失败 {fail}");
        Console.Error.WriteLine($"[upd] 更新后目录里 {Core.ModScanner.Scan(modsDir).Count} 个文件");
    }

    /// <summary>
    /// 微软登录请求体自测：验证手拼 JSON 的合法性 + 转义正确性。
    /// 重点抓的是「AOT 下序列化匿名类型会抛 NotSupportedException」这个坑,
    /// 这里全部用 JsonDocument.Parse 回读，能解析、字段对得上才算过。
    /// </summary>
    private static async Task RunAuthTest()
    {
        var pass = 0;
        var fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) { pass++; Console.Error.WriteLine($"[auth]   OK   {name}"); }
            else { fail++; Console.Error.WriteLine($"[auth]   FAIL {name} {detail}"); }
        }

        // 恶意 token：引号、反斜杠、换行、制表符、中文、控制字符全塞进去
        const string nasty = "a\"b\\c\nd\te中\u0001文";

        Console.Error.WriteLine("[auth] 1) 含特殊字符的 token 能拼出合法 JSON");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(Core.MicrosoftAuth.BuildXblBody(nasty));
            var root = doc.RootElement;
            var rps = root.GetProperty("Properties").GetProperty("RpsTicket").GetString();
            Check("JSON 合法", true);
            Check("RpsTicket 还原一致", rps == "d=" + nasty, $"got={rps}");
            Check("AuthMethod=RPS",
                root.GetProperty("Properties").GetProperty("AuthMethod").GetString() == "RPS");
            Check("RelyingParty 正确",
                root.GetProperty("RelyingParty").GetString() == "http://auth.xboxlive.com");
        }
        catch (Exception ex)
        {
            Check("JSON 合法", false, ex.Message);
        }

        Console.Error.WriteLine("[auth] 2) XSTS 请求体（UserTokens 是数组）");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                Core.MicrosoftAuth.BuildXstsBody("tok\"en"));
            var props = doc.RootElement.GetProperty("Properties");
            var arr = props.GetProperty("UserTokens");
            Check("是数组且长度为 1", arr.ValueKind == System.Text.Json.JsonValueKind.Array
                                     && arr.GetArrayLength() == 1);
            Check("token 还原一致", arr[0].GetString() == "tok\"en");
            Check("SandboxId=RETAIL", props.GetProperty("SandboxId").GetString() == "RETAIL");
        }
        catch (Exception ex)
        {
            Check("JSON 合法", false, ex.Message);
        }

        Console.Error.WriteLine("[auth] 3) MC 登录请求体");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                Core.MicrosoftAuth.BuildMcLoginBody("uhs123", "xsts456"));
            var idt = doc.RootElement.GetProperty("identityToken").GetString();
            Check("identityToken 格式正确", idt == "XBL3.0 x=uhs123;xsts456", $"got={idt}");
        }
        catch (Exception ex)
        {
            Check("JSON 合法", false, ex.Message);
        }

        Console.Error.WriteLine("[auth] 4) device code 申请（真联网，走表单）");
        var clientId = (Core.LauncherConfig.Load().MicrosoftClientId ?? "").Trim();
        if (clientId.Length == 0)
        {
            Console.Error.WriteLine("[auth]   SKIP 没配 Azure client id（设置 → 账户），跳过联网检查");
        }
        else
        {
            try
            {
                var info = await Core.MicrosoftAuth.RequestDeviceCodeAsync(clientId, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("拿到 user_code", !string.IsNullOrEmpty(info?.UserCode),
                    $"device_code={info?.DeviceCode?.Length ?? 0} 字符");
                Console.Error.WriteLine($"[auth]   验证地址: {info?.VerificationUri}  代码: {info?.UserCode}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[auth]   SKIP 网络不可达: {ex.Message}");
            }
        }

        Console.Error.WriteLine($"[auth] === {pass} 通过 / {fail} 失败 ===");
        Environment.ExitCode = fail == 0 ? 0 : 1;
    }

#endif // !PUBLISH_BUILD

    private static void Main(string[] args)
    {
#if !PUBLISH_BUILD
        if (args.Contains("--scantest"))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cfg = Core.LauncherConfig.Load();
            var vs = Core.VersionScanner.Scan(cfg.MinecraftPath);
            Console.Error.WriteLine($"scan {vs.Count} versions in {sw.ElapsedMilliseconds}ms from {cfg.MinecraftPath}");
            foreach (var v in vs.Take(3))
                Console.Error.WriteLine($"  {v.Id} java={v.RequiredJava} libs={v.Libraries.Count} main={v.MainClass} jar={v.ClientJar}");
            return;
        }

        if (args.Contains("--modtest"))
        {
            var qi = Array.IndexOf(args, "--modtest");
            var q = qi >= 0 && qi + 1 < args.Length && !args[qi + 1].StartsWith("--") ? args[qi + 1] : "sodium";
            var gvi = Array.IndexOf(args, "--gv");
            var gv = gvi >= 0 && gvi + 1 < args.Length ? args[gvi + 1] : "1.21.1";
            var li = Array.IndexOf(args, "--loader");
            var ld = li >= 0 && li + 1 < args.Length ? args[li + 1] : "fabric";
            ModTest.RunAsync(q, gv, ld).GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--mstest"))
        {
            var mi = Array.IndexOf(args, "--mstest");
            var cid = mi + 1 < args.Length && !args[mi + 1].StartsWith("--") ? args[mi + 1] : "";
            MsAuthTest.RunAsync(cid).GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--nettest"))
        {
            RunNetTest().GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--loadertest"))
        {
            var li = Array.IndexOf(args, "--loadertest");
            var mc = li + 1 < args.Length && !args[li + 1].StartsWith("--") ? args[li + 1] : "1.20.1";
            LoaderTest.RunAsync(mc, args.Contains("--install"), args.Contains("--forge"))
                .GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--launchtest"))
        {
            var li = Array.IndexOf(args, "--launchtest");
            var lid = li + 1 < args.Length ? args[li + 1] : "26.3";
            var tIdx = Array.IndexOf(args, "--timeout");
            var secs = tIdx >= 0 && tIdx + 1 < args.Length ? int.Parse(args[tIdx + 1]) : 60;
            LaunchTest.RunAsync(lid, secs).GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--inherittest"))
        {
            InheritTest.Run();
            return;
        }

        if (args.Contains("--pickertest"))
        {
            RunPickerTest();
            return;
        }

        if (args.Contains("--installtest"))
        {
            RunInstallDialogTest();
            return;
        }

        if (args.Contains("--vmtest"))
        {
            RunVersionManagerTest();
            return;
        }

        if (args.Contains("--pranktest"))
        {
            RunPrankTest();
            return;
        }

        if (args.Contains("--tctest"))
        {
            RunTerracottaTest().GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--detachtest"))
        {
            RunDetachTest().GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--sessiontest"))
        {
            RunSessionTest().GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--hometest"))
        {
            RunHomeTest().GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--detailtest"))
        {
            RunProjectDetailTest();
            return;
        }

        if (args.Contains("--upddlgtest"))
        {
            RunUpdateDialogTest();
            return;
        }

        if (args.Contains("--packcfgtest"))
        {
            RunPackConfigTest();
            return;
        }

        if (args.Contains("--updatetest"))
        {
            RunUpdateTest(args).GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--authtest"))
        {
            RunAuthTest().GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--modscan"))
        {
            RunModScanTest(args);
            return;
        }

        if (args.Contains("--checkver"))
        {
            RunVersionCheckTest(args);
            return;
        }

        if (args.Contains("--crashtest"))
        {
            RunCrashAnalyzeTest();
            return;
        }

        if (args.Contains("--javatest"))
        {
            RunJavaDownloadTest(args).GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--packtest"))
        {
            RunModpackTest(args).GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--exporttest"))
        {
            RunExportTest(args).GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--jartest"))
        {
            RunJarLaunchTest(args).GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--dltest"))
        {
            RunDownloadTest(args).GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--uitest"))
        {
            Application.Init();
            Console.Error.WriteLine("[ui] init ok");
            var w = new MainWindow();
            Console.Error.WriteLine("[ui] window ok");
            foreach (var p in new[]
                     {
                         UI.PageId.Launch, UI.PageId.Download, UI.PageId.Terracotta,
                         UI.PageId.JarLaunch, UI.PageId.Settings, UI.PageId.More, UI.PageId.Launch,
                     })
            {
                w.ShowPage(p);
                Console.Error.WriteLine($"[ui] page {p} ok");
            }
            Application.Shutdown();
            Console.Error.WriteLine("[ui] done");
            return;
        }

        if (args.Contains("--selftest"))
        {
            SelfTest.Run().GetAwaiter().GetResult();
            return;
        }
#endif // !PUBLISH_BUILD

        // 单实例互斥: 已有实例在跑时, 本次只弹提示 (有动画则先播完假边框展开)
        using var mutex = new System.Threading.Mutex(true, @"Local\LargeHardLiteLauncher.SingleInstance", out bool isFirstInstance);

        // 命令行带 .jar（或把 jar 拖到 exe 上）→ 启动后直接进「启动 JAR」页
        var initialJar = args.FirstOrDefault(a =>
            !a.StartsWith("-", StringComparison.Ordinal)
            && a.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
        if (initialJar is not null)
        {
            try
            {
                initialJar = Path.GetFullPath(initialJar);
            }
            catch
            {
                // 路径怪就原样传，页面里会报"文件不存在"
            }
        }

        Application.Init();
        try
        {
            var win = new MainWindow(duplicateInstance: !isFirstInstance, initialJar: initialJar);
            Application.Run(win);
        }
        finally
        {
            Application.Shutdown();
        }
    }
}
