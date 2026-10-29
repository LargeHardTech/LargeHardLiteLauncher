// SelfTest 用反射驱动 TG 内部主循环, AOT trim 告警在此统一压制 (仅 --selftest 路径可达)
#pragma warning disable IL2026, IL2075
using System.Reflection;
using System.Text;
using LargeHardLiteLauncher.Core;
using LargeHardLiteLauncher.UI;
using Terminal.Gui;
using Terminal.Gui.Input;
using Terminal.Gui.Configuration;

namespace LargeHardLiteLauncher;

/// <summary>
/// 临时无头自测：真实鼠标点击路径验证页面切换稳定性。
/// dotnet run -- --selftest，日志写 selftest.log。
/// </summary>
internal static class SelfTest
{
    private static readonly StringBuilder Log = new();
    private static MainWindow _win = null!;
    private static Dictionary<PageId, PageBase> _pages = null!;
    private static List<(PageId Id, Label Label)> _tabItems = null!;

    public static async Task Run()
    {
        // 用户定规 (2026-10-25): 脚手架不伺候 AOT —— Pump 反射掏 TG 内部 ApplicationImpl.Instance,
        // AOT 下该属性已被 ILLink 裁掉, 跑出来全是假结果。AOT 验证请用 --uitest/--nettest/--scantest 冒烟
        // ⚠️ 不能用 RuntimeFeature.IsDynamicCodeSupported 判断: 本项目 PublishAot=true 会让 SDK 把该开关
        // 烘焙进 Debug 的 runtimeconfig.json, Debug(JIT) 下也报 false (2026-10-25 踩坑) —— 直接探测反射目标
        var implType = typeof(Application).Assembly.GetType("Terminal.Gui.App.ApplicationImpl");
        if (implType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) is null)
        {
            Console.Error.WriteLine("[selftest] Pump 依赖的 Terminal.Gui 内部成员不可反射 (AOT 裁剪), 拒绝运行");
            Console.Error.WriteLine("[selftest] selftest 只支持 Debug(JIT) 下运行; AOT 产物冒烟请用 --uitest / --nettest / --scantest");
            return;
        }

        Application.Init();
        try
        {
            // 主题探测
            L("=== Themes ===");
            foreach (var name in ThemeManager.GetThemeNames())
                L($"theme: {name}");
            L($"current theme: {ThemeManager.Theme}");

            // 联网 manifest 探测
            L("=== Mojang manifest ===");
            try
            {
                var m = await MojangApi.FetchAsync("auto");
                L($"fetched: {m.Versions.Count} versions, latest={m.Latest.Release}/{m.Latest.Snapshot}");
                foreach (var v in m.Versions.Take(3))
                    L($"  {v.Display}");
            }
            catch (Exception ex)
            {
                L($"fetch failed: {ex.Message}");
            }

            _win = new MainWindow();
            Application.Begin(_win);
            Pump(5);

            _pages = (Dictionary<PageId, PageBase>)typeof(MainWindow).GetField("_pages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_win)!;
            _tabItems = (List<(PageId, Label)>)typeof(MainWindow).GetField("_tabItems", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_win)!;

            Dump("0.begin");

            // ===== 任务系统 =====
            L("=== TaskManager ===");
            var slowTask = TaskManager.Submit("测试慢任务", async (item, ct) =>
            {
                for (int i = 0; i <= 10; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(50, ct);
                    item.Report(i / 10.0);
                    item.LogLine($"step {i}");
                }
            });
            L($"submitted: {slowTask.Name}");
            Pump(20); // 让后台任务跑
            var snap = TaskManager.Slots().Where(t => t != null).Select(t => t!).ToList();
            L($"snapshot: {snap.Count} tasks, first={snap[0].Name} status={snap[0].Status} progress={snap[0].Progress:0.0}");

            // 取消一个提交但立即取消的任务
            var cancelTask = TaskManager.Submit("待终止任务", async (item, ct) =>
            {
                await Task.Delay(5000, ct);
            });
            TaskManager.Cancel(cancelTask);
            Pump(5);
            L($"cancelled: status={cancelTask.Status}");

            // ===== 下载页后台化: 切页耗时应极短 (读缓存+联网都在后台线程) =====
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _win.ShowPage(PageId.Download); // 不含 Pump, 纯切页逻辑
            sw.Stop();
            L($"ShowPage(Download) took: {sw.ElapsedMilliseconds} ms (应 < 100ms)");
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            Pump(3);
            sw2.Stop();
            L($"Pump(3) after switch took: {sw2.ElapsedMilliseconds} ms");
            Pump(3);
            var versionTask = TaskManager.Slots().FirstOrDefault(t => t?.Name == "检查版本列表");
            L($"version task after switch: {(versionTask is null ? "null" : versionTask.Status.ToString())}");
            Dump("1.clickDownloadTab");

            // 等后台任务完成 (缓存 + 联网两阶段)
            var sw3 = System.Diagnostics.Stopwatch.StartNew();
            Pump(40);
            sw3.Stop();
            L($"Pump(40) took: {sw3.ElapsedMilliseconds} ms");
            var vt2 = TaskManager.Slots().FirstOrDefault(t => t?.Name == "检查版本列表");
            L($"version task after wait: {(vt2 is null ? "removed" : vt2.Status.ToString())}");
            var dl = _pages[PageId.Download];
            var resultItems = (System.Collections.ObjectModel.ObservableCollection<string>)typeof(DownloadPage)
                .GetField("_resultItems", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dl)!;
            L($"result list count: {resultItems.Count}, first={resultItems[0]}");

            // 2. 点击类别列表第一项
            var catList = FindAllFocusable(_pages[PageId.Download]).OfType<ListView>().First();
            Click(catList, catList.Viewport.Width / 2, 1);
            Dump("2.clickCategory");

            // 3. 再点一次类别第二项 (选中变化)
            Click(catList, catList.Viewport.Width / 2, 2);
            Dump("3.clickCategory2");

            // 4. 空闲多轮迭代 (模拟"过一会")
            Pump(10);
            Dump("4.idle");

            // 5. 点「启动」标签, 再点「下载」回来
            ClickTab(PageId.Launch);
            Dump("5.clickLaunchTab");
            ClickTab(PageId.Download);
            Dump("6.clickDownloadTabAgain");
            Pump(10);
            Dump("7.idle2");

            // 6. 直接 ShowPage 路径 (F1-F4 等价)
            _win.ShowPage(PageId.Settings);
            Pump();
            Dump("8.switchSettings");
            _win.ShowPage(PageId.Download);
            Pump();
            Dump("9.switchDownloadBack");
            Pump(10);
            Dump("10.idle3");

            // ===== 主题切换全量刷新 =====
            L("=== Theme switch ===");
            var before = _win.HasScheme;
            ThemeManager.Theme = "Dark";
            Pump(5);
            L($"after switch to Dark: win.HasScheme={_win.HasScheme} (应为 false, 缓存已清)");
            ThemeManager.Theme = "Default";
            Pump(5);

            // ===== 折叠任务栏 =====
            L("=== Collapse ===");
            var toggle = typeof(MainWindow).GetMethod("ToggleCollapse", BindingFlags.NonPublic | BindingFlags.Instance)!;
            toggle.Invoke(_win, null);
            Pump(3);
            var taskPanelField = typeof(MainWindow).GetField("_taskPanel", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var taskPanel = taskPanelField.GetValue(_win)!;
            var panelVisible = (bool)taskPanel.GetType().GetProperty("Visible")!.GetValue(taskPanel)!;
            L($"collapsed: taskPanel.Visible={panelVisible} (应为 false)");
            toggle.Invoke(_win, null);
            Pump(3);

            // ===== 渲染性能定位: 空列表 vs 920 项 =====
            L("=== Render profile ===");
            _win.ShowPage(PageId.Launch);
            Pump(3);
            var sw0 = System.Diagnostics.Stopwatch.StartNew();
            Pump(10);
            sw0.Stop();
            L($"Launch page Pump(10): {sw0.ElapsedMilliseconds} ms");

            _win.ShowPage(PageId.Download);
            Pump(10); // 等任务完成, 列表灌满
            var sw1 = System.Diagnostics.Stopwatch.StartNew();
            Pump(10);
            sw1.Stop();
            L($"Download page (920 items) Pump(10): {sw1.ElapsedMilliseconds} ms");

            // 折叠任务栏后再测 (排除任务栏进度条动画因素)
            var toggle0 = typeof(MainWindow).GetMethod("ToggleCollapse", BindingFlags.NonPublic | BindingFlags.Instance)!;
            toggle0.Invoke(_win, null);
            Pump(3);
            var swPanel = System.Diagnostics.Stopwatch.StartNew();
            Pump(10);
            swPanel.Stop();
            L($"Download page + collapsed panel Pump(10): {swPanel.ElapsedMilliseconds} ms");
            toggle0.Invoke(_win, null);
            Pump(3);
            var splitter = (View)typeof(MainWindow).GetField("_splitter", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_win)!;
            var splitPt = splitter.ViewportToScreen(new System.Drawing.Point(0, 0));
            L($"splitter screen pt: {splitPt}");
            var sx = splitPt.X;
            var sy = splitPt.Y + 2;
            var beforeW = ((View)taskPanel).Frame;
            L($"panel frame before: {beforeW}");
            // 模拟: 按下 → 拖动 → 松开
            RaiseMouse(sx, sy, MouseFlags.LeftButtonPressed);
            RaiseMouse(sx - 6, sy, MouseFlags.LeftButtonPressed | MouseFlags.PositionReport);
            RaiseMouse(sx - 6, sy, MouseFlags.LeftButtonReleased);
            Pump(3);
            var afterW = ((View)taskPanel).Frame;
            L($"panel frame after: {afterW} (应比 before 宽 6)");
            var savedWidth = LauncherConfig.Load().TaskPanelWidth;
            L($"saved TaskPanelWidth: {savedWidth}");

            // ===== 终态任务自动移除 + IDLE =====
            L("=== Auto-remove finished ===");
            TaskManager.Submit("瞬间任务", async (item, ct) =>
            {
                await Task.CompletedTask;
                item.LogLine("done");
            });
            Pump(5);
            var afterDone = TaskManager.Slots().FirstOrDefault(t => t?.Name == "瞬间任务");
            L($"after done: {(afterDone is null ? "removed" : afterDone.Status.ToString())} (应为 Completed, 3秒后才移除)");
            System.Threading.Thread.Sleep(3500); // 等过 3 秒停留期 (不能用 await, MainLoopSyncContext 会死锁)
            Pump(5);
            var afterWait = TaskManager.Slots().FirstOrDefault(t => t?.Name == "瞬间任务");
            L($"after 3.5s: {(afterWait is null ? "removed" : afterWait.Status.ToString())} (应为 removed)");

            L("=== Theme names ===");
            L("  " + string.Join(", ", Terminal.Gui.Configuration.ThemeManager.GetThemeNames()));

            // ===== 配色后再切主题 (用户报的 bug: 一直显示旧主题) =====
            L("=== Palette then theme switch ===");
            var baseSchemeBefore = Terminal.Gui.Configuration.SchemeManager.GetScheme(Terminal.Gui.Drawing.Schemes.Base);
            ColorSchemes.Apply(ColorSchemes.Find("nord"));
            var nordBg = Terminal.Gui.Configuration.SchemeManager.GetScheme(Terminal.Gui.Drawing.Schemes.Base).Normal.Background;
            ColorSchemes.Clear();
            Terminal.Gui.Configuration.ThemeManager.Theme = "Light";
            var lightBg = Terminal.Gui.Configuration.SchemeManager.GetScheme(Terminal.Gui.Drawing.Schemes.Base).Normal.Background;
            L($"  baseline={baseSchemeBefore.Normal.Background} nord={nordBg} afterLightSwitch={lightBg}");
            L($"  switch works: {lightBg != nordBg} (应为 True)");
            Terminal.Gui.Configuration.ThemeManager.Theme = "Default";
            Pump(3);

            // ===== 个性化页 (合并主题列表后崩溃回归) =====
            L("=== Personalize section ===");
            try
            {
                _win.ShowPage(PageId.Settings);
                var sp = _pages[PageId.Settings];
                typeof(SettingsPage).GetMethod("ShowSection", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(sp, new object[] { 1 });
                Pump(3);
                L("  personalize ok");
                var panelField = typeof(SettingsPage).GetField("_panel", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var pnl = (View)panelField.GetValue(sp)!;
                Pump(5);
                foreach (var v in pnl.SubViews)
                {
                    L($"    {v.GetType().Name} title={(v as FrameView)?.Title ?? "-"} Y={v.Frame.Y} H={v.Frame.Height}");
                    foreach (var iv in v.SubViews)
                        L($"      -> {iv.GetType().Name} Y={iv.Frame.Y} H={iv.Frame.Height} Text={(iv as CheckBox)?.Text ?? (iv as Label)?.Text ?? "-"}");
                }
                L($"  panel viewport={pnl.Viewport} content={pnl.GetContentSize()}");
                int y0 = pnl.Viewport.Y;
                pnl.ScrollVertical(4);
                Pump(2);
                L($"  scroll test: Y {y0} -> {pnl.Viewport.Y} (变大 = 滚动生效)");
                pnl.ScrollVertical(4);
                Pump(2);
                int yBefore = pnl.Viewport.Y;
                typeof(SettingsPage).GetMethod("ShowSection", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(sp, new object[] { 0 });
                Pump(4);
                L($"  switch-section reset: Y {yBefore} -> {pnl.Viewport.Y} (应回 0)");
                // 切回个性化 (内容超高才有得滚), 再测空隙
                typeof(SettingsPage).GetMethod("ShowSection", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(sp, new object[] { 1 });
                Pump(6);
                int yGap = pnl.Viewport.Y;
                var gapPt = pnl.ViewportToScreen(new System.Drawing.Point(3, 9));
                RaiseMouse(gapPt.X, gapPt.Y, MouseFlags.WheeledDown);
                Pump(3);
                L($"  wheel on gap: Y {yGap} -> {pnl.Viewport.Y} (应变大)");
                // 主题列表滚轮: 只滚视图, 选中项不能变 (否则每滚一格换主题, 界面狂闪)
                var tl = (ListView<string>)typeof(SettingsPage).GetField("_themeList", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sp)!;
                var selBefore = tl.SelectedItem ?? "(null)";
                var vyBefore = tl.Viewport.Y;
                var tp2 = tl.ViewportToScreen(new System.Drawing.Point(3, 3));
                RaiseMouse(tp2.X, tp2.Y, MouseFlags.WheeledDown);
                Pump(3);
                L($"  theme wheel: selected [{selBefore}] -> [{tl.SelectedItem ?? "(null)"}] (应不变), viewY {vyBefore} -> {tl.Viewport.Y}");
            }
            catch (Exception ex)
            {
                L("  PERSONALIZE EX: " + ex);
            }

            // ===== 启动参数模板化验证 =====
            L("=== Launch args (templated) ===");
            {
                var cfgA = LauncherConfig.Load();
                var vsA = VersionScanner.Scan(cfgA.MinecraftPath);

                foreach (var vid in new[] { vsA.FirstOrDefault(x => x.Id.StartsWith("26."))?.Id, vsA.FirstOrDefault(x => !x.Id.StartsWith("26.") && x.Type == "release")?.Id })
                {
                    var vv = vsA.FirstOrDefault(x => x.Id == vid);
                    if (vv is null) continue;
                    var cpA = GameLauncher.BuildClassPath(vv, cfgA.MinecraftPath, out _);
                    var warnA = new List<string>();
                    var effA = VersionSettings.Resolve(cfgA, vv, VersionSettings.CountMods(cfgA, vv));
                    var jvm = GameLauncher.BuildJvmArgs(vv, cfgA, effA, Path.Combine(vv.Dir, "natives"), cpA, 25, warnA);
                    var game = GameLauncher.BuildGameArgs(vv, cfgA, effA, cfgA.MinecraftPath, cfgA.MinecraftPath, warnA);

                    L($"  --- {vv.Id} (jvm模板 {vv.JvmTemplate.Count} / game模板 {vv.GameTemplate.Count}{(vv.LegacyArguments is null ? "" : " / 老格式")}) ---");
                    L($"  JVM: {jvm.Count} 个, -D 属性 {jvm.Count(x => x.StartsWith("-D"))} 个, 唯一 {jvm.Where(x => x.StartsWith("-D")).Select(x => x.Split('=')[0]).Distinct().Count()} 个");
                    L($"  JVM 含 -cp: {jvm.Any(x => x is "-cp")}, 含 java.library.path: {jvm.Any(x => x.Contains("java.library.path"))}");
                    L($"  重复检查: {string.Join(" | ", jvm.Where(x => x.StartsWith("-D")).GroupBy(x => x.Split('=')[0]).Where(g => g.Count() > 1).Select(g => g.Key))} {(jvm.Where(x => x.StartsWith("-D")).GroupBy(x => x.Split('=')[0]).All(g => g.Count() == 1) ? "(无重复)" : "")}");
                    L($"  GAME: {string.Join(" ", game.Take(30))}");
                    L($"  关键项: username={game.Contains("--username")} width={game.Contains("--width")} height={game.Contains("--height")} gameDir={game.Contains("--gameDir")} clientId={game.Contains("--clientId")} xuid={game.Contains("--xuid")}");
                    if (warnA.Count > 0)
                        L($"  警告: {string.Join(", ", warnA.Distinct())}");
                }
            }

            // ===== natives 分目录布局 (26.x) =====
            L("=== Natives layout ===");
            {
                var cfgN = LauncherConfig.Load();
                foreach (var v in VersionScanner.Scan(cfgN.MinecraftPath)
                             .Where(x => x.Id.StartsWith("26.") || x.Id == "1.9.2")
                             .Take(2))
                {
                    var nat = v.Libraries.Where(l => l.NativesWindowsPath is not null && l.AppliesToWindows).ToList();
                    var subs = GameLauncher.ExtractNativesSubDirs(v.JvmTemplate);
                    var nativeDir = Path.Combine(v.Dir, "natives");
                    GameLauncher.ExtractNativesFor(v, cfgN.MinecraftPath, nativeDir);
                    var rootDll = Directory.Exists(nativeDir)
                        ? Directory.GetFiles(nativeDir).Length : 0;
                    var subInfo = string.Join(", ", subs.Select(d =>
                        $"{d}={Directory.GetFiles(Path.Combine(nativeDir, d)).Length}"));
                    L($"  {v.Id}: native库={nat.Count} 子目录=[{string.Join("/", subs)}] 根={rootDll}个 {subInfo}");
                    // 关键: 有子目录时 java.library.path 指向的那个必须有 dll
                    if (subs.Count > 0)
                    {
                        var libPathDir = Path.Combine(nativeDir, "java");
                        var ok = Directory.Exists(libPathDir) && Directory.GetFiles(libPathDir, "*.dll").Length > 0;
                        L($"    java.library.path 指向的 java/ 目录有 dll: {ok}");
                    }
                }
            }

            // ===== modded 判定 (HMCL MODDED 档依赖它) =====
            L("=== Modded detection ===");
            {
                var vs = VersionScanner.Scan(LauncherConfig.Load().MinecraftPath);
                int modded = 0;
                foreach (var v in vs)
                {
                    bool m = GameLauncher.IsModdedVersion(v);
                    if (m) modded++;
                    if (modded <= 3 && m)
                        L($"  modded: {v.Id} main={v.MainClass}");
                }
                L($"  total={vs.Count} modded={modded} vanilla={vs.Count - modded}");
            }

            // ===== 版本名 → 基础 MC 版本 (用户报: 拿 1.20.1-forge-test 去搜 mod) =====
            L("=== Version resolution ===");
            {
                // 本地"真原版"条目: 无 inheritsFrom, 名字就是版本号
                MinecraftVersion V(string id, string? parent = null) =>
                    new() { Id = id, Dir = "?", InheritsFrom = parent };
                var fixture = new List<MinecraftVersion>
                {
                    V("1.20.1"), V("1.21.1"),
                    V("1.20.1-forge-47.4.28", "1.20.1"),
                    V("我的Forge测试", "1.20.1"),
                    V("fabric-loader-0.20.1-1.20.1", "1.20.1"),
                    V("neoforge-21.1.80", "1.21.1"),
                    V("我的1.20.1"),                       // 改名过的纯原版
                    V("fabric-loader-0.20.1-1.21.1"),      // 断链 (没记 inheritsFrom)
                };
                void Case(string id, string expect)
                {
                    var v = fixture.First(x => x.Id == id);
                    var got = VersionHelper.ResolveBaseVersion(v, fixture);
                    L($"  {id,-32} → [{got}] (应 {expect}) {(got == expect ? "OK" : "★错")}");
                }
                Case("1.20.1", "1.20.1");
                Case("1.20.1-forge-47.4.28", "1.20.1");
                Case("我的Forge测试", "1.20.1");          // ← 用户报的那个坑
                Case("fabric-loader-0.20.1-1.20.1", "1.20.1");
                Case("neoforge-21.1.80", "1.21.1");
                Case("我的1.20.1", "1.20.1");
                Case("fabric-loader-0.20.1-1.21.1", "1.21.1"); // 断链靠正则 + 本地核对
                // 纯字符串重载
                L($"  ResolveBaseVersion(\"1.20.1-forge-test\") = [{VersionHelper.ResolveBaseVersion("1.20.1-forge-test", fixture)}] (应 1.20.1)");

                // 版本号数值比较: 字符串降序会把 1.9.4 排到 1.20.1 前面 (老 bug)
                var raw = new[] { "1.9.4", "1.20.1", "1.21", "1.20.10", "1.7.10" };
                var num = raw.OrderBy(x => x, Comparer<string>.Create(VersionHelper.CompareVersionDesc)).ToList();
                var str = raw.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                L($"  数值降序: [{string.Join(" ", num)}] (应 1.21 1.20.10 1.20.1 1.9.4 1.7.10)");
                L($"  字符串降序(错的样子): [{string.Join(" ", str)}]");
                L($"  数值排序{(num[0] == "1.21" && num[^1] == "1.7.10" ? "正确" : "★错误")}");

                // Forge 1.13+ 的 ignoreList 修补 (用户报的 ResolutionException 根因)
                var patchM = typeof(GameLauncher).GetMethod("PatchIgnoreList",
                    BindingFlags.NonPublic | BindingFlags.Static)!;
                var forgeVer = new MinecraftVersion
                {
                    Id = "1.20.1-forge-test",
                    Dir = "?",
                    InheritsFrom = "1.20.1",
                    ClientJar = @"D:\mc\versions\1.20.1\1.20.1.jar",
                };
                var testArgs = new List<string>
                {
                    "-DignoreList=bootstraplauncher,forge-,${version_name}.jar".Replace("${version_name}", "1.20.1-forge-test"),
                };
                patchM.Invoke(null, new object[] { testArgs, forgeVer });
                L($"  ignoreList 修补: {testArgs[0]}");
                L($"  含真实 jar 名 1.20.1.jar: {testArgs[0].EndsWith(",1.20.1.jar")} (应 True; 名字对不上会导致模块解析崩溃)");
            }

            // ===== 版本独立设置 (学 PCL) =====
            L("=== Version settings ===");
            {
                var cfg = new LauncherConfig
                {
                    MaxMemoryMb = 2048,
                    MinMemoryMb = 512,
                    WindowWidth = 854,
                    WindowHeight = 480,
                };
                var tv = new MinecraftVersion { Id = "1.20.1-forge-test", Dir = "?", InheritsFrom = "1.20.1" };

                var e0 = VersionSettings.Resolve(cfg, tv, 0);
                L($"  无覆盖: 内存 {e0.MinMemoryMb}~{e0.MaxMemoryMb} 窗口 {e0.WindowWidth}x{e0.WindowHeight} (应 512~2048 / 854x480)");

                VersionSettings.Set(cfg, tv.Id, new VersionOption
                {
                    RamMode = 1,
                    MaxMemoryMb = 6144,
                    MinMemoryMb = 1024,
                    WindowWidth = 1280,
                    WindowHeight = 720,
                });
                var e1 = VersionSettings.Resolve(cfg, tv, 0);
                L($"  手动覆盖: 内存 {e1.MinMemoryMb}~{e1.MaxMemoryMb} 窗口 {e1.WindowWidth}x{e1.WindowHeight} (应 1024~6144 / 1280x720)");
                L($"  HasOverride={VersionSettings.HasOverride(cfg, tv.Id)} (应 True)");

                VersionSettings.Set(cfg, tv.Id, new VersionOption { RamMode = 0 });
                var e2 = VersionSettings.Resolve(cfg, tv, 90);
                L($"  自动内存(90 mod): {e2.MaxMemoryMb} MB auto={e2.MemoryFromAuto} (应约 2560, auto=True)");

                VersionSettings.Reset(cfg, tv.Id);
                L($"  初始化后 HasOverride={VersionSettings.HasOverride(cfg, tv.Id)} (应 False)");

                L($"  AutoMaxMemory(0)={VersionSettings.AutoMaxMemory(0, 16384)} AutoMaxMemory(300)={VersionSettings.AutoMaxMemory(300, 16384)} (应 1536 / 5120, 都是 256 的倍数)");
            }

            // ===== Mod 扫描 + 元数据解析 (造临时 jar 实测) =====
            L("=== Mod scanner ===");
            {
                var tmp = Path.Combine(Path.GetTempPath(), "lll_modtest_" + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    Directory.CreateDirectory(tmp);

                    void MakeJar(string fileName, string entryName, string content)
                    {
                        using var fs = File.Create(Path.Combine(tmp, fileName));
                        using var zip = new System.IO.Compression.ZipArchive(
                            fs, System.IO.Compression.ZipArchiveMode.Create);
                        var entry = zip.CreateEntry(entryName);
                        using var w = new StreamWriter(entry.Open());
                        w.Write(content);
                    }

                    MakeJar("test-fabric.jar", "fabric.mod.json",
                        "{\"id\":\"testmod\",\"name\":\"Test Fabric\",\"version\":\"1.2.3\",\"description\":\"demo\"}");
                    MakeJar("test-forge.jar", "META-INF/mods.toml",
                        "modLoader=\"javafml\"\nloaderVersion=\"[47,)\"\n[[mods]]\nmodId=\"forgemod\"\nversion=\"4.5.6\"\ndisplayName=\"Test Forge\"\n");
                    MakeJar("off.jar.disabled", "fabric.mod.json",
                        "{\"id\":\"offmod\",\"name\":\"Disabled One\",\"version\":\"0.1\"}");
                    File.WriteAllText(Path.Combine(tmp, "readme.txt"), "不是 mod，应被忽略");

                    var scanSw = System.Diagnostics.Stopwatch.StartNew();
                    var mods = ModScanner.Scan(tmp);
                    scanSw.Stop();
                    L($"  扫描: {mods.Count} 个 (应 3, 忽略 txt), {scanSw.ElapsedMilliseconds}ms");
                    foreach (var m in mods)
                        L($"    {(m.Enabled ? "✓" : "·")} {m.Name} | {m.Version} | {m.Loader} | {m.FileName}");

                    var fabric = mods.FirstOrDefault(m => m.FileName == "test-fabric.jar");
                    L($"  fabric 元数据: name={fabric?.Name} ver={fabric?.Version} id={fabric?.ModId} loader={fabric?.Loader} (应 Test Fabric / 1.2.3 / testmod / fabric)");

                    var forge = mods.FirstOrDefault(m => m.FileName == "test-forge.jar");
                    L($"  forge 元数据: name={forge?.Name} ver={forge?.Version} id={forge?.ModId} loader={forge?.Loader} (应 Test Forge / 4.5.6 / forgemod / forge)");

                    var off = mods.First(m => !m.Enabled);
                    L($"  禁用项: EnabledName={off.EnabledName} Enabled={off.Enabled} (应 off.jar / False)");

                    // 启用 → 文件名应变成 off.jar
                    var ok = ModScanner.SetEnabled(off, true, out var msg);
                    L($"  启用: ok={ok} msg=[{msg}] 新文件存在={File.Exists(Path.Combine(tmp, "off.jar"))} (应 True/True)");

                    // 再扫一次，应变成启用状态
                    ModScanner.ClearCache();
                    var again = ModScanner.Scan(tmp);
                    L($"  重新扫描: off.jar Enabled={again.FirstOrDefault(m => m.FileName == "off.jar")?.Enabled} (应 True)");
                }
                catch (Exception ex)
                {
                    L($"  ★ Mod 扫描测试异常: {ex.Message}");
                }
                finally
                {
                    try { Directory.Delete(tmp, true); } catch { /* 清理失败无所谓 */ }
                }
            }

            // ===== 多账户（学 PCL：存多个账户一键切）=====
            L("=== Accounts ===");
            {
                var cfg = new LauncherConfig { PlayerName = "Peter", AuthMode = "offline" };
                cfg.Accounts.Clear();

                var e1 = AccountManager.Sync(cfg);
                L($"  首次 Sync: {cfg.Accounts.Count} 个账户, 当前={e1.DisplayName} (应 1 / Peter)");

                var e2 = AccountManager.AddOffline(cfg, "Steve");
                L($"  添加离线: {cfg.Accounts.Count} 个, 当前 PlayerName={cfg.PlayerName} (应 2 / Steve)");

                AccountManager.Apply(cfg, e1);
                L($"  切回第一个: PlayerName={cfg.PlayerName} (应 Peter)");

                AccountManager.Remove(cfg, e1);
                L($"  删掉当前账户: 剩 {cfg.Accounts.Count} 个, 自动切到={cfg.PlayerName} (应 1 / Steve)");

                AccountManager.Remove(cfg, e2);
                L($"  全删光: 剩 {cfg.Accounts.Count} 个, PlayerName={cfg.PlayerName} (应 0 / Steve 兜底)");
            }

            // ===== 启动动画收尾状态 =====
            L("=== Startup animation ===");
            {
                Pump(200); // 等动画跑完 (生长 ~200ms + 闪烁 ~800ms)
                L($"  borderStyle={_win.BorderStyle} (应为 Single/Double)");
                foreach (var v in _win.SubViews)
                    L($"  {v.GetType().Name} Visible={v.Visible} Frame={v.Frame}");
            }

            // ===== 任务更新不能覆盖日志行 (槽位→索引偏移回归) =====
            L("=== Row index mapping ===");
            {
                var tp = (TaskPanel)typeof(MainWindow).GetField("_taskPanel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_win)!;
                var itemsField2 = typeof(TaskPanel).GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var t = TaskManager.Submit("索引测试", async (item, ct) =>
                {
                    item.Report(0.4);
                    await Task.Delay(1500, ct);
                });
                Pump(3);
                var items = (System.Collections.IList)itemsField2.GetValue(tp)!;
                L($"  row0={items[0]}");
                L($"  row1={items[1]}");
                L($"  row2={items[2]}");
                var tl2 = (ListView<string>)typeof(TaskPanel).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tp)!;
                L($"  auto-selected=[{tl2.Value}] (应=新提交的任务行)");
                var logLbl = (Label)typeof(TaskPanel).GetField("_taskPctLbl", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tp)!;
                tl2.SelectedItem = items[0]!.ToString();
                Pump(3);
                L($"  log row label=[{logLbl.Text}] (选中日志应为 LOG)");
                t.Cancel();
                Pump(3);
                var totalLbl = (Label)typeof(TaskPanel).GetField("_totalPctLbl", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tp)!;
                var taskLbl = (Label)typeof(TaskPanel).GetField("_taskPctLbl", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tp)!;
                var tList = (ListView<string>)typeof(TaskPanel).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tp)!;
                L($"  total label=[{totalLbl.Text}] (无运行任务应为 DONE)");
                tList.SelectedItem = "[2] IDLE";
                Pump(3);
                L($"  task label=[{taskLbl.Text}] (选中 IDLE 应为 IDLE)");
                var cBtn2 = (Button)typeof(TaskPanel).GetField("_cancelBtn", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tp)!;
                L($"  cancel button=[{cBtn2.Text}] (应为 结束任务)");
            }

            // ===== 社区资源页 (本地逻辑; 真实搜索见 --modtest) =====
            L("=== Community page ===");
            {
                var dp = (DownloadPage)_pages[PageId.Download];
                _win.ShowPage(PageId.Download);
                Pump(4);

                var typeDrop = (DropDownList)typeof(DownloadPage).GetField("_typeDrop", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;
                var refreshB = (Button)typeof(DownloadPage).GetField("_refreshBtn", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;
                var sf = (TextField)typeof(DownloadPage).GetField("_searchField", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;
                var switchM = typeof(DownloadPage).GetMethod("SwitchCategory", BindingFlags.NonPublic | BindingFlags.Instance)!;

                var cats = (string[])typeof(DownloadPage).GetField("Categories", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                L($"  分类: [{string.Join(" | ", cats)}] (应 5 个, 无数据包)");

                var verField = (TextField)typeof(DownloadPage).GetField("_verField", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;
                var verLbl = (Label)typeof(DownloadPage).GetField("_verLbl", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;

                // 切到 Mod: 应隐藏 类型/刷新, 并清空搜索框
                sf.Text = "残留关键词";
                switchM.Invoke(dp, new object[] { "Mod" });
                Pump(4);
                L($"  Mod 页: 类型下拉={typeDrop.Visible}(应False) 刷新={refreshB.Visible}(应False) 搜索框=[{sf.Text}](应空)");
                L($"  Mod 页: 版本框={verField.Visible}(应True) 版本标签={verLbl.Visible}(应True)");
                var sortLblV = (Label)typeof(DownloadPage).GetField("_sortLbl", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;
                var vl = verLbl.Frame; var vf = verField.Frame; var sl = sortLblV.Frame;
                L($"  布局: 版本标签 X={vl.X} W={vl.Width} → 右边界 {vl.X + vl.Width}");
                L($"        版本框   X={vf.X} W={vf.Width} → 右边界 {vf.X + vf.Width}");
                L($"        排序标签 X={sl.X}   (版本框右边界应 <= 排序标签X, 且版本框X == 版本标签右边界)");
                L($"        是否重合: {vf.X < sl.X + sl.Width && vf.X + vf.Width > sl.X}");

                // 任务栏应该出现"Mod 搜索"任务 (而不是默默在后台跑)
                var running = TaskManager.Slots()
                    .Where(t => t is { IsDone: false })
                    .Select(t => t!.Name).ToList();
                L($"  任务栏进行中: [{string.Join(" | ", running)}] (应含 Mod 搜索)");

                // 让刚才 SwitchCategory 触发的真实搜索作废 (避免它回来把假数据覆盖)
                var epochField = typeof(DownloadPage).GetField("_searchEpoch", BindingFlags.NonPublic | BindingFlags.Instance)!;
                epochField.SetValue(dp, (int)epochField.GetValue(dp)! + 99);

                // ===== 加载器下拉 (Mod/整合包显示, 改了立刻重新搜索) =====
                var loaderLbl = (Label)typeof(DownloadPage).GetField("_loaderLbl", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;
                var loaderDrop = (DropDownList)typeof(DownloadPage).GetField("_loaderDrop", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;
                L($"  Mod 页: 加载器标签={loaderLbl.Visible}(应True) 加载器下拉={loaderDrop.Visible}(应True) 当前项=[{loaderDrop.Text}](应 自动)");
                var loaderOpts = loaderDrop.Source!.ToList().Cast<string>().ToArray();
                L($"  加载器选项: [{string.Join(" | ", loaderOpts)}] (应 自动|不限|Fabric|Forge|NeoForge|Quilt)");
                var sortDropC = (DropDownList)typeof(DownloadPage).GetField("_sortDrop", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;
                var refreshC = (Button)typeof(DownloadPage).GetField("_refreshBtn", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;
                var ll = loaderLbl.Frame; var ldf = loaderDrop.Frame; var sdf = sortDropC.Frame;
                L($"  布局: 排序下拉右边界={sdf.X + sdf.Width} 加载器标签X={ll.X}(应>=排序右边界) 加载器下拉X={ldf.X}(应={ll.X + ll.Width + 1})");
                L($"  布局: 刷新按钮X={refreshC.Frame.X}(应={ldf.X + ldf.Width + 2}, 即加载器下拉右边)");
                // 模拟用户在下拉里选 "Forge": 搜索代次应 +1 (即改了立刻重新联网搜)
                var epochBefore = (int)epochField.GetValue(dp)!;
                loaderDrop.Text = "Forge";
                Pump(4);
                var epochAfter = (int)epochField.GetValue(dp)!;
                L($"  切换加载器=Forge: 搜索代次 {epochBefore} → {epochAfter} (应 +1)");
                epochField.SetValue(dp, (int)epochField.GetValue(dp)! + 99); // 作废这次搜索

                // ===== 分类下拉 (只对 Mod 显示) + 排序改服务端 =====
                var catLbl = (Label)typeof(DownloadPage).GetField("_catLbl", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;
                var catDrop = (DropDownList)typeof(DownloadPage).GetField("_catDrop", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;
                L($"  Mod 页: 分类标签={catLbl.Visible}(应True) 分类下拉={catDrop.Visible}(应True) 当前项=[{catDrop.Text}](应 全部分类)");
                var catOpts = catDrop.Source!.ToList().Cast<string>().Take(4).ToArray();
                L($"  分类选项(前4): [{string.Join(" | ", catOpts)}] (应 全部分类|世界生成|科技|魔法)");
                var sortNamesF2 = (string[])typeof(DownloadPage).GetField("_sortNames", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;
                L($"  社区排序项: [{string.Join(" | ", sortNamesF2)}] (应为 默认|下载量|关注量|最新发布|最近更新)");
                // 改排序 → 代次 +1 (服务端排序, 改了重新联网搜, 不再本地重排)
                Pump(4); // 先排空 pending 搜索, 避免上一轮任务延迟启动混进来
                var e0 = (int)epochField.GetValue(dp)!;
                sortDropC.Text = Loc.T("dl.sortDownloads");
                Pump(4);
                var e1 = (int)epochField.GetValue(dp)!;
                L($"  切换排序=下载量: 搜索代次 {e0} → {e1} (应 +1)");
                epochField.SetValue(dp, (int)epochField.GetValue(dp)! + 99);
                // 改分类 → 代次 +1
                var e2 = (int)epochField.GetValue(dp)!;
                catDrop.Text = Loc.T("dl.cat.technology");
                Pump(4);
                var e3 = (int)epochField.GetValue(dp)!;
                L($"  切换分类=科技: 搜索代次 {e2} → {e3} (应 +1)");
                epochField.SetValue(dp, (int)epochField.GetValue(dp)! + 99);
                catDrop.Text = Loc.T("dl.catFilterAll");
                Pump(2);

                // 整合包也显示加载器; 资源包/光影没有加载器概念; 分类只有 Mod 有
                switchM.Invoke(dp, new object[] { Loc.T("dl.cat.modpack") });
                Pump(4);
                L($"  整合包页: 加载器下拉={loaderDrop.Visible}(应True) 分类下拉={catDrop.Visible}(应False)");
                epochField.SetValue(dp, (int)epochField.GetValue(dp)! + 99);
                switchM.Invoke(dp, new object[] { Loc.T("dl.cat.resourcepack") });
                Pump(4);
                L($"  资源包页: 加载器下拉={loaderDrop.Visible}(应False) 分类下拉={catDrop.Visible}(应False)");
                epochField.SetValue(dp, (int)epochField.GetValue(dp)! + 99);
                switchM.Invoke(dp, new object[] { Loc.T("dl.cat.shader") });
                Pump(4);
                L($"  光影包页: 加载器下拉={loaderDrop.Visible}(应False) 分类下拉={catDrop.Visible}(应False)");
                epochField.SetValue(dp, (int)epochField.GetValue(dp)! + 99);

                // 切回 Mod: 版本框应已按当前实例预填 (学 PCL2 的 targetVersion)
                switchM.Invoke(dp, new object[] { "Mod" });
                Pump(4);
                epochField.SetValue(dp, (int)epochField.GetValue(dp)! + 99);
                L($"  Mod 页: 版本框预填=[{verField.Text}] (应是**基础 MC 版本**, 不是目录名)");

                // 换版本 (模拟用户在启动页切了实例) → 版本框必须跟着更新
                {
                    var cfgField = typeof(PageBase).GetField("Config", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var cfg = (LauncherConfig)cfgField.GetValue(dp)!;
                    var oldSel = cfg.SelectedVersion;
                    cfg.SelectedVersion = "1.21.1-forge-test"; // 故意用用户报的那种怪名字
                    switchM.Invoke(dp, new object[] { Loc.T("dl.cat.resourcepack") });
                    Pump(4);
                    epochField.SetValue(dp, (int)epochField.GetValue(dp)! + 99);
                    L($"  换版本→版本框=[{verField.Text}] (应 1.21.1, 不能还是老版本)");
                    cfg.SelectedVersion = oldSel;
                    switchM.Invoke(dp, new object[] { "Mod" });
                    Pump(4);
                    epochField.SetValue(dp, (int)epochField.GetValue(dp)! + 99);
                }
                var catDf = catDrop.Frame; var ll2 = loaderLbl.Frame;
                L($"  布局: 分类下拉右边界={catDf.X + catDf.Width} 加载器标签X={ll2.X}(应={catDf.X + catDf.Width + 2}) 加载器下拉X={loaderDrop.Frame.X}(应={ll2.X + ll2.Width + 1})");

                // 切回原版: 控件恢复
                switchM.Invoke(dp, new object[] { "原版游戏" });
                Pump(6);
                var sortNamesF3 = (string[])typeof(DownloadPage).GetField("_sortNames", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;
                L($"  原版页: 类型下拉={typeDrop.Visible}(应True) 刷新={refreshB.Visible}(应True) 排序项[0]={sortNamesF3[0]}(应为时间倒序)");
                L($"  原版页: 版本框={verField.Visible}(应False) 版本标签={verLbl.Visible}(应False)");
                var sortDropV = (DropDownList)typeof(DownloadPage).GetField("_sortDrop", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dp)!;
                L($"  原版页: 刷新按钮X={refreshB.Frame.X}(应={sortDropV.Frame.X + sortDropV.Frame.Width + 2}, 紧挨排序下拉不留缝)");

                // 已安装标记 (装了 1.20.1-forge 也算 1.20.1 已装) + 版本顺序
                var itemsFieldLocal = typeof(DownloadPage).GetField("_resultItems", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var vanillaItems = (System.Collections.IList)itemsFieldLocal.GetValue(dp)!;
                var tag = Loc.T("dl.installedTag");
                var tagged = vanillaItems.Cast<object>().Count(o => o is string s && s.Contains(tag));
                var head = vanillaItems.Cast<object>().Take(3).Select(o => o?.ToString() ?? "");
                L($"  原版列表: 共{vanillaItems.Count}项, 带{tag}标记 {tagged} 项");
                L($"  原版列表前3: [{string.Join(" | ", head)}]");
            }

            // ===== 设置页每个分类的控件树 (看有没有被边框裁掉) =====
            L("=== Settings tree ===");
            {
                var sp2 = (SettingsPage)_pages[PageId.Settings];
                _win.ShowPage(PageId.Settings);

                void Dump(View v, string indent, int boxH)
                {
                    foreach (var c in v.SubViews)
                    {
                        var fr = c.Frame;
                        var text = (c as Label)?.Text?.ToString() ?? (c as CheckBox)?.Text?.ToString()
                                   ?? (c as Button)?.Text?.ToString() ?? "";
                        if (text.Length > 28) text = text[..28] + "…";
                        // 关键: 判断是否落在父 FrameView 内容区之外 (被边框裁掉)
                        var clipped = boxH > 0 && fr.Y + Math.Max(fr.Height, 1) > boxH - 2;
                        L($"  {indent}{c.GetType().Name} Y={fr.Y} H={fr.Height} W={fr.Width}"
                          + (text.Length > 0 ? $" [{text}]" : "")
                          + (clipped ? "  ★被裁" : ""));
                        if (c is FrameView fv)
                            Dump(c, indent + "  ", fv.Frame.Height);
                        else if (c.SubViews.Count > 0)
                            Dump(c, indent + "  ", 0);
                    }
                }
                var pnl2 = (View)typeof(SettingsPage).GetField("_panel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sp2)!;
                for (int sec = 0; sec < 4; sec++)
                {
                    typeof(SettingsPage).GetMethod("ShowSection", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(sp2, new object[] { sec });
                    Pump(5);
                    L($"  --- 分类 {sec} ---");
                    Dump(pnl2, "  ", 0);
                }
            }

            // ===== 设置页竖滚动条联动 =====
            L("=== Settings scrollbar ===");
            {
                var sp3 = (SettingsPage)_pages[PageId.Settings];
                _win.ShowPage(PageId.Settings);
                typeof(SettingsPage).GetMethod("ShowSection", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(sp3, new object[] { 1 }); // 个性化: 内容超高
                Pump(8);

                var pnl3 = (View)typeof(SettingsPage).GetField("_panel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sp3)!;
                var bar = (ScrollBar)typeof(SettingsPage).GetField("_scrollBar", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sp3)!;

                var content = pnl3.GetContentSize();
                L($"  content={content.Height} viewport={pnl3.Viewport.Height} bar.scrollable={bar.ScrollableContentSize} bar.visible={bar.VisibleContentSize} bar.value={bar.Value}");

                pnl3.ScrollVertical(5);
                Pump(3);
                typeof(SettingsPage).GetMethod("SyncScrollBar", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(sp3, null);
                L($"  面板滚 5 行后: panelY={pnl3.Viewport.Y} bar.value={bar.Value} (应相等)");

                // 反向: 拖滑块到 2, 面板应跟到 2
                bar.Value = 2;
                Pump(3);
                L($"  bar 设为 2 后: panelY={pnl3.Viewport.Y} (应=2)");

                pnl3.ScrollVertical(-1000);
                typeof(SettingsPage).GetMethod("SyncScrollBar", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(sp3, null);
                Pump(2);
            }

            // ===== 任务栏宽度变化后内部控件是否重排 =====
            L("=== TaskPanel resize ===");
            {
                var tp2 = (TaskPanel)typeof(MainWindow).GetField("_taskPanel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_win)!;
                _win.ShowPage(PageId.Launch);
                Pump(3);
                void DumpWidths(string tag)
                {
                    var parts = tp2.SubViews.Select(v =>
                        $"{v.GetType().Name}={v.Frame.Width}").ToList();
                    L($"  [{tag}] taskPanel={tp2.Frame.Width} | " + string.Join(" ", parts));

                    // 左侧页面里的 groupbox 也要跟着 _content 变
                    var lp4 = _pages[PageId.Launch];
                    var gbs = lp4.SubViews.OfType<FrameView>()
                        .Select(f => $"{(f.Title.Length > 6 ? f.Title[..6] : f.Title)}={f.Frame.Width}");
                    L($"  [{tag}] 启动页 groupbox: " + string.Join(" ", gbs) + $" | 页面宽={lp4.Frame.Width}");
                }
                DumpWidths("初始");
                typeof(MainWindow).GetMethod("UpdatePanelWidth", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(_win, new object[] { 70 });
                Pump(6);
                DumpWidths("面板=70");
                typeof(MainWindow).GetMethod("UpdatePanelWidth", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(_win, new object[] { 44 });
                Pump(6);
                DumpWidths("面板=44");
                DumpWidths("面板=44");

                // === 拖动节流: 拖动中宽度不动, 只有标题变; 松手才应用 ===
                var preview = typeof(MainWindow).GetMethod("PreviewDragWidth", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var apply = typeof(MainWindow).GetMethod("ApplyDragWidth", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var content = (View)typeof(MainWindow).GetField("_content", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_win)!;
                var lp5 = _pages[PageId.Launch];
                var gbW = lp5.SubViews.OfType<FrameView>().FirstOrDefault()?.Frame.Width ?? 0;
                int wBefore = tp2.Frame.Width, cBefore = content.Frame.Width;
                int r0 = tp2.Frame.X + tp2.Frame.Width;
                // 模拟"按住分隔条往左拖": 鼠标 X 逐渐变小 → 目标宽度变大
                for (int mx = r0 - wBefore; mx >= r0 - 80; mx -= 5)
                    preview.Invoke(_win, new object[] { mx });
                Pump(3);
                L($"  拖动中: 任务栏宽 {wBefore} → {tp2.Frame.Width} (应变), 目标宽 {(int)typeof(MainWindow).GetField("_dragWidth", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_win)!}");
                L($"  拖动中: 左侧页面 {cBefore} → {content.Frame.Width}, groupbox {gbW}→{lp5.SubViews.OfType<FrameView>().FirstOrDefault()?.Frame.Width} (跟着变, 但不强排)");
                apply.Invoke(_win, null);
                Pump(4);
                L($"  松手后: 任务栏 {tp2.Frame.Width}, 左侧页面 {content.Frame.Width} (应变), groupbox={lp5.SubViews.OfType<FrameView>().FirstOrDefault()?.Frame.Width} (应变)");
            }

            // ===== 账户名变更后刷新 =====
            L("=== Player name refresh ===");
            {
                var lp3 = _pages[PageId.Launch];
                // 必须用页面持有的 Config 实例 (MainWindow 传进去的那个)
                var cfg3 = (LauncherConfig)typeof(LaunchPage).BaseType!
                    .GetField("Config", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(lp3)!;
                var oldName = cfg3.PlayerName;
                cfg3.PlayerName = "RefreshProbe";
                typeof(LaunchPage).GetMethod("RefreshPlayerLabel", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(lp3, null);
                var pl = (Label)typeof(LaunchPage).GetField("_playerLabel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(lp3)!;
                L($"  label after change: {pl.Text}");
                cfg3.PlayerName = oldName;
                typeof(LaunchPage).GetMethod("RefreshPlayerLabel", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(lp3, null);
                L($"  label restored: {pl.Text}");
            }

            // ===== Java 检测 + 排序下拉 =====
            L("=== Java detect ===");
            var javas = JavaDetector.Detect();
            L($"found {javas.Count} java(s):");
            foreach (var j in javas.Take(6))
                L($"  {j.Major} | {j.Version} | {j.Path}");
            L($"auto pick: {JavaDetector.AutoPick()?.Path ?? "(none)"}");

            // ===== 启动页版本数据 =====
            L("=== Launch version data ===");
            {
                var lpage = _pages[PageId.Launch];
                var vf = typeof(LaunchPage).GetField("_versions", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var vo = typeof(LaunchPage).GetField("_versionObjs", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var vl = typeof(LaunchPage).GetField("_versionLabel", BindingFlags.NonPublic | BindingFlags.Instance)!;
                _win.ShowPage(PageId.Launch);
                Pump(3);
                var cfgField = typeof(LaunchPage).BaseType!.GetField("Config", BindingFlags.NonPublic | BindingFlags.Instance);
                var cfgObj = cfgField?.GetValue(lpage);
                L($"  pageConfig.MinecraftPath={(cfgObj?.GetType().GetProperty("MinecraftPath")?.GetValue(cfgObj))}");
                L($"  directScan={VersionScanner.Scan(LauncherConfig.Load().MinecraftPath).Count}");
                L($"  versions={(int)vf.GetValue(lpage)!.GetType().GetProperty("Count")!.GetValue(vf.GetValue(lpage))!} objs={(int)vo.GetValue(lpage)!.GetType().GetProperty("Count")!.GetValue(vo.GetValue(lpage))!} label={((Label)vl.GetValue(lpage)!).Text}");
            }

            // ===== 启动页三按钮布局 =====
            L("=== Launch buttons ===");
            L($"  page viewport={_pages[PageId.Launch].Viewport} contentSize={_pages[PageId.Launch].GetContentSize()}");
            _win.ShowPage(PageId.Launch);
            Pump(3);
            foreach (var sub in _pages[PageId.Launch].SubViews)
            {
                if (sub is Button b)
                    L($"  btn {b.Text}: Frame={b.Frame} Visible={b.Visible} Enabled={b.Enabled} HasScheme={b.HasScheme} Scheme={b.SchemeName}");
                if (sub is FrameView fv)
                {
                    L($"  groupbox [{fv.Title}]: Frame={fv.Frame}");
                    foreach (var inner in fv.SubViews)
                        L($"    inner {inner.GetType().Name}: Frame={inner.Frame} Text={(inner as Label)?.Text ?? (inner as Button)?.Text?.ToString() ?? "-"}");
                }
                if (sub is Label plainLbl)
                    L($"  label {plainLbl.Text}: Frame={plainLbl.Frame}");
                if (sub is TextView tv)
                    L($"  homeView: Frame={tv.Frame} Visible={tv.Visible}");
            }

            // ===== 底部控件完整性 =====
            // ===== 启动时槽位铺满验证 =====
            var itemsField = typeof(TaskPanel).GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var startItems = (System.Collections.ObjectModel.ObservableCollection<string>)itemsField.GetValue(taskPanel)!;
            L($"startup slots: {startItems.Count} rows, first={startItems[0]}, last={startItems[^1]} (应为 Capacity 行, 全 IDLE)");



            L("=== Bottom controls ===");
            var panelVp = taskPanel.GetType().GetProperty("Viewport")!.GetValue(taskPanel)!;
            L($"panel viewport: {panelVp}");
            foreach (var sub in ((View)taskPanel).SubViews)
            {
                var f = sub.Frame;
                L($"  sub {sub.GetType().Name}: Frame={f} inViewport={((System.Drawing.Rectangle)panelVp).Contains(f)}");
            }

            // ===== 中文任务名截断 + 移除后双击 (崩溃回归) =====
            L("=== Chinese name truncate + double-click ===");
            TaskManager.Submit("检查版本列表", async (item, ct) => await Task.CompletedTask);
            Pump(5);
            var taskListField = typeof(TaskPanel).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var taskListView = (ListView<string>)taskListField.GetValue(taskPanel)!;
            // 选中中文名已完成任务 → RefreshSelected → TruncateByColumns 中文路径
            taskListView.SelectedItem = "检查版本列表  [已完成]  100%";
            Pump(3);
            L("chinese-name select survived (无 EX 即通过)");
            System.Threading.Thread.Sleep(3500);
            Pump(5);
            // ⚠️ 别依赖 FinishedVisible 的时长（现在是 20 秒，留给用户双击看日志的时间）
            // 直接手动清场，测试要的是"槽位回到 IDLE 后双击不崩"
            foreach (var t in TaskManager.Slots())
            {
                if (t is not null)
                    TaskManager.Remove(t);
            }
            Pump(5);
            var idleSnap = TaskManager.Slots().Where(t => t != null).ToList();
            L($"after manual cleanup: {(idleSnap.Count == 0 ? "empty/IDLE" : idleSnap.Count + " tasks")}");
            // 先选中一个空槽位 (第 0 行是启动器日志, 选中它双击会开模态日志窗)
            taskListView.SelectedItem = taskListView.Source is null ? null : "[2] IDLE";
            Pump(2);
            var dblPt = taskListView.ViewportToScreen(new System.Drawing.Point(2, 3));
            RaiseMouse(dblPt.X, dblPt.Y, MouseFlags.LeftButtonDoubleClicked);
            Pump(5);
            L("idle double-click survived (无 EX 即通过)");

            // ===== TG Button 按钮: 点 [折叠] =====
            L("=== TG Button routing ===");
            var cancelBtn = (Button)typeof(TaskPanel).GetField("_cancelBtn", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(taskPanel)!;
            var foldBtn = (Button)typeof(TaskPanel).GetField("_foldBtn", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(taskPanel)!;
            L($"cancelBtn Frame={cancelBtn.Frame} Text={cancelBtn.Text}");
            L($"foldBtn Frame={foldBtn.Frame} Text={foldBtn.Text}");
            var foldPt = foldBtn.ViewportToScreen(new System.Drawing.Point(2, 0));
            RaiseMouse(foldPt.X, foldPt.Y, MouseFlags.LeftButtonClicked);
            Pump(3);
            L($"after click [折叠]: panel.Visible={((View)taskPanel).Visible} (应为 false)");

            // ===== 折叠态单击标题栏展开 =====
            L("=== Titlebar click to expand ===");
            var titlePt = new System.Drawing.Point(_win.Frame.X + _win.Frame.Width / 2, _win.Frame.Y);
            RaiseMouse(titlePt.X, titlePt.Y, MouseFlags.LeftButtonClicked);
            Pump(3);
            L($"after titlebar click: panel.Visible={((View)taskPanel).Visible} (应为 true)");
        }
        catch (Exception ex)
        {
            L("EX: " + ex);
        }
        finally
        {
            File.WriteAllText("selftest.log", Log.ToString());
            Application.Shutdown();
        }
    }

    private static void Pump(int iterations = 3)
    {
        // AOT 下 ILLink 会裁掉"只被反射引用"的 TG 内部成员 —— 逐步诊断, 别让 NRE 裸抛
        var implType = typeof(Application).Assembly.GetType("Terminal.Gui.App.ApplicationImpl");
        if (implType is null) { Console.Error.WriteLine("[Pump] ApplicationImpl 类型未找到 (AOT 裁剪?)"); return; }
        var instProp = implType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (instProp is null) { Console.Error.WriteLine("[Pump] ApplicationImpl.Instance 属性未找到 (AOT 裁剪?)"); return; }
        var impl = instProp.GetValue(null);
        if (impl is null) { Console.Error.WriteLine("[Pump] ApplicationImpl.Instance 为 null (未 Init?)"); return; }
        var coordProp = implType.GetProperty("Coordinator", BindingFlags.NonPublic | BindingFlags.Instance);
        if (coordProp is null) { Console.Error.WriteLine("[Pump] ApplicationImpl.Coordinator 属性未找到 (AOT 裁剪?)"); return; }
        var coord = coordProp.GetValue(impl);
        if (coord is null) { Console.Error.WriteLine("[Pump] Coordinator 为 null"); return; }
        var runIter = coord.GetType().GetMethod("RunIteration");
        if (runIter is null) { Console.Error.WriteLine("[Pump] Coordinator.RunIteration 方法未找到 (AOT 裁剪?)"); return; }
        for (int i = 0; i < iterations; i++)
            runIter.Invoke(coord, null);
    }

    private static void Click(View view, int x, int y)
    {
        var screen = view.ViewportToScreen(new System.Drawing.Point(x, y));
        var mouse = new Mouse { ScreenPosition = screen, Flags = MouseFlags.LeftButtonClicked };
        Application.Instance.Mouse.RaiseMouseEvent(mouse);
        Pump();
    }

    /// <summary>向窗口直接注入鼠标事件 (拖动模拟用)</summary>
    private static void RaiseMouse(int x, int y, MouseFlags flags)
    {
        var mouse = new Mouse { ScreenPosition = new System.Drawing.Point(x, y), Flags = flags };
        Application.Instance.Mouse.RaiseMouseEvent(mouse);
    }

    private static Label TabLabel(PageId id) => _tabItems.First(t => t.Id == id).Label;

    private static void ClickTab(PageId id)
    {
        var lbl = TabLabel(id);
        Click(lbl, lbl.Viewport.Width / 2, 0);
    }

    private static void Dump(string step)
    {
        var current = (PageId)typeof(MainWindow).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_win)!;
        var focused = _win.MostFocused;
        var visible = string.Join(",", _pages.Where(kv => kv.Value.Visible).Select(kv => kv.Key));
        L($"{step}: current={current} | visiblePages=[{visible}] | focused={focused?.GetType().Name}:{focused?.Title}");
        foreach (var (id, lbl) in _tabItems)
            L($"    tab {id}: Text={lbl.Text}");
    }

    private static List<View> FindAllFocusable(View v)
    {
        var result = new List<View>();
        if (v.CanFocus && v.Visible && v.Enabled)
            result.Add(v);
        foreach (var sub in v.SubViews)
            result.AddRange(FindAllFocusable(sub));
        return result;
    }

    private static void L(string s)
    {
        Log.AppendLine(s);
        Console.Error.WriteLine(s); // 实时输出, 便于定位挂死点
    }
}
