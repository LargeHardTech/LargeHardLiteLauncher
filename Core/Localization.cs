namespace LargeHardLiteLauncher.Core;

/// <summary>界面语言</summary>
public enum Lang { ZhCn, EnUs }

/// <summary>
/// 轻量国际化: key → (中文, 英文) 字典。
/// 语言在启动时由 Config.Language 决定 (切换后重启生效)。
/// </summary>
public static class Loc
{
    public static Lang Current { get; set; } = Lang.ZhCn;

    public static bool IsEn => Current == Lang.EnUs;

    /// <summary>从配置字符串解析语言 ("zh-CN" | "en-US")</summary>
    public static Lang Parse(string? code) =>
        string.Equals(code, "en-US", StringComparison.OrdinalIgnoreCase) ? Lang.EnUs : Lang.ZhCn;

    private static readonly Dictionary<string, (string Zh, string En)> Map = new()
    {
        // ---- 窗口 / 导航 ----
        ["app.title"] = ("LargeHardLiteLauncher", "LargeHardLiteLauncher"),
        ["tab.launch"] = ("启动", "Launch"),
        ["tab.download"] = ("下载", "Download"),
        ["tab.settings"] = ("设置", "Settings"),
        ["tab.more"] = ("更多", "More"),

        // ---- 任务栏 ----
        ["task.title"] = ("任务", "Tasks"),
        ["task.idle"] = ("IDLE", "IDLE"),
        ["task.cancel"] = ("结束任务", "Terminate"),
        ["task.done"] = ("DONE", "DONE"),
        ["task.idleShort"] = ("IDLE", "IDLE"),
        ["task.logTag"] = ("LOG", "LOG"),
        ["task.cancelNamed"] = ("结束任务", "Terminate"),
        ["task.fold"] = ("折叠(F9)", "Fold(F9)"),
        ["task.running"] = ("进行中", "Running"),
        ["task.completed"] = ("已完成", "Done"),
        ["task.failed"] = ("失败", "Failed"),
        ["task.cancelled"] = ("已终止", "Cancelled"),
        ["task.logTitle"] = ("日志 - {0}", "Log - {0}"),
        ["task.noLog"] = ("(暂无日志)", "(no log)"),
        ["task.close"] = ("关闭", "Close"),
        ["task.launcherLog"] = ("日志记录线程", "Log thread"),
        ["task.launcherLogRow"] = ("[0] 日志记录线程", "[0] Log thread"),
        ["log.quiet"] = ("安静", "Quiet"),
        ["log.normal"] = ("正常", "Normal"),
        ["log.verbose"] = ("详细", "Verbose"),
        ["log.debug"] = ("调试", "Debug"),
        ["log.empty"] = ("(暂无日志)", "(no log)"),
        ["log.save"] = ("保存日志", "Save log"),
        ["log.saveTitle"] = ("保存日志到...", "Save log to..."),
        ["log.savePath"] = ("路径", "Path"),
        ["log.saved"] = ("日志已保存:\n{0}", "Log saved:\n{0}"),
        ["log.saveFailed"] = ("保存失败: {0}", "Save failed: {0}"),
        ["log.clear"] = ("清空", "Clear"),
        ["log.title"] = ("启动器日志", "Launcher log"),
        ["task.checkVersions"] = ("检查版本列表", "Check version list"),
        ["task.loading"] = ("(正在读取版本列表...)", "(loading version list...)"),
        ["task.cancelledByUser"] = ("(被用户取消)", "(cancelled by user)"),
        ["task.networkError"] = ("(网络异常,无法连接mojang api)", "(network error: cannot reach Mojang API)"),
        ["task.cacheRead"] = ("缓存读取: {0} 个版本", "cache: {0} versions"),
        ["task.netRefresh"] = ("联网刷新: {0} 个版本,最新正式版 {1}", "online: {0} versions, latest release {1}"),

        // ---- 下载页 ----
        ["dl.category"] = ("类别", "Category"),
        ["dl.name"] = ("名称", "Name"),
        ["dl.search"] = ("搜索", "Search"),
        ["dl.type"] = ("类型", "Type"),
        ["dl.install"] = ("安装", "Install"),
        ["dl.installWhich"] = ("把 {0} 安装到哪个游戏目录?", "Install {0} to which folder?"),
        ["dl.installing"] = ("安装 {0}", "Install {0}"),
        ["dl.noSelect"] = ("请先在列表里选一个版本", "Select a version first"),
        ["dl.installDone"] = ("{0} 安装完成,可以回启动页启动", "{0} installed"),
        ["dl.installFailed"] = ("安装失败: {0}", "Install failed: {0}"),
        ["dl.refresh"] = ("刷新列表", "Refresh"),
        ["dl.cat.vanilla"] = ("原版游戏", "Vanilla"),
        ["dl.cat.mod"] = ("Mod", "Mods"),
        ["dl.cat.modpack"] = ("整合包", "Modpacks"),
        ["dl.cat.datapack"] = ("数据包", "Datapacks"),
        ["dl.cat.resourcepack"] = ("资源包", "Resource Packs"),
        ["dl.cat.shader"] = ("光影包", "Shaders"),
        ["dl.filter.all"] = ("全部", "All"),
        ["dl.filter.release"] = ("正式版", "Releases"),
        ["dl.filter.snapshot"] = ("快照", "Snapshots"),
        ["dl.filter.old"] = ("历史版本", "Historical"),
        ["dl.loader"] = ("加载器", "Loader"),
        ["dl.loaderAuto"] = ("自动", "Auto"),
        ["dl.loaderAny"] = ("不限", "Any"),
        ["dl.mcVersion"] = ("版本", "MC ver"),
        ["dl.sort"] = ("排序", "Sort"),
        ["dl.sort.dlAsc"] = ("下载量正序", "Downloads ascending"),
        ["dl.sort.dlDesc"] = ("下载量倒序", "Downloads descending"),
        ["dl.sort.timeDesc"] = ("时间倒序", "Newest first"),
        ["dl.sort.timeAsc"] = ("时间正序", "Oldest first"),
        ["dl.sort.nameAsc"] = ("字典序 A-Z", "Name A-Z"),
        ["dl.sort.nameDesc"] = ("字典序 Z-A", "Name Z-A"),
        ["dl.noMatch"] = ("(没有匹配的版本)", "(no matching versions)"),
        ["dl.pending"] = ("({0}: 待实现 - 社区资源后续支持)", "({0}: not implemented yet)"),
        ["dl.autoLoad"] = ("(进入页面后自动加载版本列表...)", "(version list loads automatically...)"),

        // ---- 社区资源: 分类 / 服务端排序 / 依赖 / 落盘 ----
        ["dl.catFilter"] = ("分类", "Category"),
        ["dl.catFilterAll"] = ("全部分类", "All categories"),
        ["dl.cat.worldgen"] = ("世界生成", "World Gen"),
        ["dl.cat.technology"] = ("科技", "Technology"),
        ["dl.cat.magic"] = ("魔法", "Magic"),
        ["dl.cat.adventure"] = ("冒险", "Adventure"),
        ["dl.cat.decoration"] = ("装饰", "Decoration"),
        ["dl.cat.mobs"] = ("怪物", "Mobs"),
        ["dl.cat.utility"] = ("工具", "Utility"),
        ["dl.cat.equipment"] = ("装备", "Equipment"),
        ["dl.cat.storage"] = ("存储", "Storage"),
        ["dl.cat.food"] = ("食物", "Food"),
        ["dl.cat.farming"] = ("农业", "Farming"),
        ["dl.cat.transportation"] = ("交通运输", "Transport"),
        ["dl.cat.redstone"] = ("红石", "Redstone"),
        ["dl.cat.automation"] = ("自动化", "Automation"),
        ["dl.cat.energy"] = ("能源", "Energy"),
        ["dl.cat.mechanics"] = ("游戏机制", "Mechanics"),
        ["dl.cat.optimization"] = ("优化", "Optimization"),
        ["dl.cat.library"] = ("支持库", "Library"),
        ["dl.cat.server"] = ("服务器", "Server"),
        ["dl.sortDefault"] = ("默认", "Default"),
        ["dl.sortDownloads"] = ("下载量", "Downloads"),
        ["dl.sortFollows"] = ("关注量", "Follows"),
        ["dl.sortNewest"] = ("最新发布", "Newest"),
        ["dl.sortUpdated"] = ("最近更新", "Updated"),
        ["dl.fileAllVersions"] = ("全部版本", "All versions"),
        ["dl.fileSelected"] = ("(所选版本)", "(selected)"),
        ["dl.chRelease"] = ("正式", "Release"),
        ["dl.chBeta"] = ("测试", "Beta"),
        ["dl.chAlpha"] = ("预览", "Alpha"),
        ["dl.deps"] = ("前置", "Dependencies"),
        ["dl.depsRequired"] = ("必选前置", "Required"),
        ["dl.depsOptional"] = ("可选前置", "Optional"),
        ["dl.depsNone"] = ("无前置依赖", "No dependencies"),
        ["dl.depsUnresolved"] = ("(部分前置无法解析来源,仅显示个数)", "(some dependencies cannot be resolved)"),
        ["dl.installWithDeps"] = ("带前置安装", "With dependencies"),
        ["dl.installWithoutDeps"] = ("不带前置", "Without dependencies"),
        ["dl.depsAskTitle"] = ("该 Mod 有必选前置", "This mod has required dependencies"),
        ["dl.depsAsk"] = ("必选前置 {0} 个。缺少前置 Mod 会无法启动,要一起装吗?", "{0} required dependencies. Install them together?"),
        ["dl.saveTitle"] = ("保存文件", "Save file"),
        ["dl.saveAs"] = ("另存为...", "Save as..."),
        ["dl.saveAsQueued"] = ("已加入下载队列: {0}", "Queued: {0}"),
        ["dl.saveTarget"] = ("保存到", "Save to"),
        ["dl.saveName"] = ("文件名", "File name"),
        ["dl.saveOverwrite"] = ("目标目录已存在同名文件 {0},覆盖吗?", "File {0} already exists. Overwrite?"),
        ["dl.saveBadName"] = ("文件名不能为空,且不能包含 \\ / : * ? \" < > |", "File name must not be empty or contain \\ / : * ? \" < > |"),
        ["dl.nameStyle"] = ("文件名样式", "File name style"),
        ["dl.nameStyleNone"] = ("不加前缀", "No prefix"),
        ["dl.nameStyleCn"] = ("[名]文件名", "[Name]file"),
        ["dl.nameStyleSquare"] = ("[名] 文件名", "[Name] file"),
        ["dl.nameStyleDash"] = ("名-文件名", "Name-file"),
        ["dl.nameStyleLead"] = ("-名文件名", "-Namefile"),
        ["set.modNameStyle"] = ("Mod 中文名前缀", "Mod Chinese name prefix"),
        ["dl.zhConverted"] = ("中文关键词已按常用词表转换为: {0}", "Chinese keyword converted to: {0}"),
        ["dl.zhNoEntry"] = ("中文关键词未收录于常用词表,请尝试英文名搜索", "Chinese keyword not in dictionary, try the English name"),

        // ---- 安装游戏版本 (原版 / 加载器) ----
        ["dl.installLoader"] = ("装加载器", "Install loader"),
        ["dl.installVersionTitle"] = ("安装游戏版本", "Install game version"),
        ["dl.instName"] = ("版本名", "Version name"),
        ["dl.pickLoaderHint"] = ("(选加载器后,在这里选加载器版本)", "(pick a loader to choose its version)"),
        ["dl.nameExists"] = ("版本 {0} 已存在,换个名字", "Version {0} already exists"),
        ["dl.installDir"] = ("安装目录", "Install to"),
        ["dl.baseMc"] = ("基于 {0}", "Based on {0}"),
        ["dl.loaderKindVanilla"] = ("原版", "Vanilla"),
        ["dl.versions"] = ("版本列表", "Versions"),
        ["dl.loadingVersions"] = ("读取版本列表...", "Loading versions..."),
        ["dl.noLoaderVersions"] = ("(该 MC 版本下没有可装的 {0} 版本)", "(no {0} for this MC version)"),
        ["dl.needBaseVersion"] = ("先在启动页选一个已安装的游戏版本,加载器要装在它上面", "Select an installed version on the launch page first"),
        ["dl.installLoaderDone"] = ("{0} {1} 安装完成: {2}", "{0} {1} installed: {2}"),
        ["dl.installedCurrent"] = ("{0} 安装完成,已设为当前版本,回启动页即可启动", "{0} installed and selected"),
        ["dl.installedOtherDir"] = ("{0} 装在了另一个 .minecraft 文件夹:\n{1}\n\n切换过去并选中它吗?", "{0} was installed into another .minecraft:\n{1}\n\nSwitch to it?"),
        ["dl.switchTo"] = ("切换", "Switch"),
        ["dl.stayHere"] = ("留在这", "Stay here"),
        ["dl.installedTag"] = ("[已装]", "[installed]"),
        ["dl.modpackNotReady"] = ("整合包文件已下载到:\n{0}\n\n 完整安装流程(解压 overrides / 生成版本 / 装前置)还没实现,这个文件暂时不能直接游玩。", "Modpack downloaded to:\n{0}\n\nFull installation is not implemented yet."),

        // ---- 设置页 ----
        ["set.section.launch"] = ("启动", "Launch"),
        ["set.section.personalize"] = ("个性化", "Appearance"),
        ["set.section.performance"] = ("性能", "Performance"),
        ["set.section.misc"] = ("其他", "Misc"),
        ["set.categories"] = ("设置分类", "Categories"),
        ["set.save"] = ("保存设置", "Save"),
        ["set.saveBack"] = ("保存并返回", "Save & Back"),
        ["set.applyPerf"] = ("应用性能设置", "Apply"),
        ["set.msBox"] = ("正版登录(微软账户)", "Microsoft account"),
        ["set.msClientId"] = ("Azure client id", "Azure client id"),
        ["set.msLogin"] = ("登录微软账户", "Sign in with Microsoft"),
        ["set.msLogout"] = ("退出正版登录", "Sign out"),
        ["set.msHint"] = ("需要自己在 Azure 注册应用拿 client id (免费,见下方说明)", "Register an Azure app for a client id"),
        ["set.msStateOn"] = ("当前:正版 {0}", "Signed in: {0}"),
        ["set.msStateOff"] = ("当前:离线模式 ({0})", "Offline: {0}"),
        ["ms.title"] = ("微软登录", "Microsoft sign-in"),
        ["ms.step1"] = ("1. 浏览器打开这个网址:", "1. Open this URL:"),
        ["ms.step2"] = ("2. 输入下面的代码:", "2. Enter this code:"),
        ["ms.waiting"] = ("等待授权...", "Waiting for authorization..."),
        ["ms.success"] = ("登录成功: {0}", "Signed in as {0}"),
        ["ms.failed"] = ("登录失败: {0}", "Sign-in failed: {0}"),
        ["ms.needClientId"] = ("先在下面填 Azure client id 再登录", "Fill in the Azure client id first"),
        ["set.playerName"] = ("离线玩家名", "Offline player name"),
        ["set.mcPath"] = ("官方 .minecraft 路径", "Official .minecraft path"),
        ["set.javaPath"] = ("Java (自动检测或自选)", "Java (auto or pick)"),
        ["set.javaAuto"] = ("自动检测(推荐)", "Auto detect (recommended)"),
        ["set.versionIsolation"] = ("版本隔离", "Version isolation"),
        ["set.isolationNone"] = ("不隔离(全部共享)", "No isolation"),
        ["set.isolationMods"] = ("有 mod 的版本隔离", "Isolate modded instances"),
        ["set.isolationFull"] = ("全部版本隔离", "Isolate all instances"),
        ["set.maxMemory"] = ("最大内存 (MB)", "Max memory (MB)"),
        ["set.minMemory"] = ("最小内存 (MB)", "Min memory (MB)"),
        ["set.windowWidth"] = ("窗口宽度", "Window width"),
        ["set.windowHeight"] = ("窗口高度", "Window height"),
        ["set.fullscreen"] = ("全屏启动", "Start fullscreen"),
        ["acc.playerCap"] = ("玩家:", "Player:"),
        ["set.jvmArgs"] = ("JVM 参数", "JVM args"),
        ["set.homeContent"] = ("主页内容", "Home content"),
        ["set.homeSource"] = ("来源(路径或 URL)", "Source (path/URL)"),
        ["set.theme"] = ("主题(即时生效)", "Theme (applies immediately)"),
        ["set.themeScheme"] = ("主题 / 配色(即时生效)", "Theme / Palette (instant)"),
        ["set.appearance"] = ("外观", "Appearance"),
        ["app.duplicateTitle"] = ("重复启动", "Already running"),
        ["app.duplicateBody"] = ("已经有一个 LargeHardLiteLauncher 在运行了。\n请先关闭它再启动新的。", "LargeHardLiteLauncher is already running.\nClose the existing instance first."),
        ["set.startupAnimation"] = ("启动动画", "Startup animation"),
        ["set.startupAnimationOn"] = ("启用(关闭可加速启动)", "Enable (disable to start faster)"),
        ["set.language"] = ("语言(重启生效)", "Language (restart required)"),
        ["set.colorScheme"] = ("配色方案(即时生效)", "Color scheme (instant)"),
        ["set.colorFollowTheme"] = ("跟随主题", "Follow theme"),
        ["set.renderPerf"] = ("终端渲染性能", "Terminal rendering"),
        ["set.netPerf"] = ("网络性能", "Network"),
        ["set.taskBox"] = ("任务栏", "Task panel"),
        ["set.fpsLimit"] = ("渲染帧率上限 (0=不限)", "FPS limit (0=unlimited)"),
        ["set.disableAnim"] = ("禁用 UI 动画(提升流畅度)", "Disable animations"),
        ["set.inputDebounce"] = ("输入去抖 (ms)", "Input debounce (ms)"),
        ["set.listCache"] = ("列表虚拟化行数", "List cache lines"),
        ["set.poolSize"] = ("HTTP 连接池大小", "HTTP pool size"),
        ["set.dnsPreheat"] = ("DNS 预热(提前解析域名)", "DNS preheat"),
        ["set.cacheMax"] = ("缓存上限 (MB)", "Cache limit (MB)"),
        ["set.taskCapacity"] = ("容量 (4~256)", "Capacity (4~256)"),
        ["set.lowMem"] = ("低内存模式(激进 GC)", "Low memory mode"),
        ["set.dlBox"] = ("下载", "Download"),
        ["set.cfKey"] = ("CurseForge API key", "CurseForge API key"),
        ["set.maxThreads"] = ("最大线程数", "Max threads"),
        ["set.timeout"] = ("超时(秒)", "Timeout (s)"),
        ["set.dlSource"] = ("下载源", "Download source"),
        ["set.ignoreSsl"] = ("忽略 HTTPS 证书错误(谨慎)", "Ignore HTTPS cert errors (unsafe)"),
        ["set.showDebug"] = ("显示调试信息", "Show debug info"),
        ["set.logLevel"] = ("日志级别", "Log level"),
        ["set.savedTo"] = ("设置已保存到:\n{0}", "Settings saved to:\n{0}"),
        ["common.ok"] = ("确定", "OK"),
        // ---- 启动页 ----
        ["acc.title"] = ("账户", "Account"),
        ["acc.player"] = ("玩家: {0}", "Player: {0}"),
        ["acc.folder"] = ("目录", "Folder"),
        ["acc.versionBox"] = ("版本", "Version"),
        ["acc.addFolder"] = ("添加文件夹", "Add folder"),
        ["acc.folderPath"] = ("填入 .minecraft 文件夹完整路径:", "Full path to .minecraft:"),
        ["acc.folderMissing"] = ("目录不存在", "Directory not found"),
        ["acc.folderExists"] = ("这个目录已经在列表里了", "Folder already in list"),
        ["acc.noVersion"] = ("(无版本)", "(no version)"),
        ["launch.start"] = ("启动游戏", "Launch Game"),
        ["launch.selectVersion"] = ("版本选择", "Versions"),
        ["launch.versionSettings"] = ("版本设置", "Settings"),
        ["launch.versionSettingsPending"] = ("(版本设置待实现)", "(version settings not implemented)"),
        ["launch.versionPickTitle"] = ("版本选择", "Select Version"),
        ["launch.pending"] = ("离线名: {0}\n版本: {1}\n\n(启动逻辑待实现)", "Player: {0}\nVersion: {1}\n\n(launch not implemented)"),
        ["launch.noVersion"] = ("(未扫描到版本)", "(no version found)"),
        ["launch.needVersion"] = ("没有可启动的版本。\n请先在 下载 页安装游戏版本。", "No version to launch.\nInstall one from the Download page."),
        ["launch.scanning"] = ("正在扫描版本...", "Scanning versions..."),
        ["launch.failedTitle"] = ("启动失败", "Launch failed"),
        ["launch.welcome"] = ("欢迎使用 LargeHardLiteLauncher!\n\n· F1 启动   F2 下载   F3 联机\n· F4 启动 JAR   F5 设置   F6 更多\n· 主页内容可在 设置 → 启动 → 主页 中更改", "Welcome to LargeHardLiteLauncher!\n\n· F1 Launch   F2 Download   F3 Multiplayer\n· F4 Run JAR   F5 Settings   F6 More\n· Home page: Settings → Launch → Home"),

        // ---- 更多页 ----
        ["more.help"] = ("· F1 启动页:选择版本并启动游戏\n· F2 下载页:下载原版游戏(陆续支持社区资源)\n· F3 设置页:账号 / 主页 / 性能 / 下载源", "· F1 Launch: pick a version and start\n· F2 Download: install vanilla (community content later)\n· F3 Settings: account / home / performance / source"),
        ["more.about"] = ("LargeHardLiteLauncher v0.1.0 - by LargeHardTech (LHT)\nTUI Minecraft Launcher, C# + Terminal.Gui v2, NativeAOT\n鸣谢: Mojang (版本 API) / BMCLAPI (镜像源) / PCL2 (布局灵感)", "LargeHardLiteLauncher v0.1.0 - by LargeHardTech (LHT)\nTUI Minecraft Launcher, C# + Terminal.Gui v2, NativeAOT\nCredits: Mojang (version API) / BMCLAPI (mirror) / PCL2 (layout inspiration)"),
        ["more.toolbox"] = ("百宝箱", "Toolbox"),
        ["more.clean"] = ("清理游戏垃圾", "Clean junk files"),
        ["more.cleanPending"] = ("(清理逻辑待实现)", "(cleanup not implemented)"),
        ["more.customDl"] = ("下载自定义文件(待实现)", "Download custom file (todo)"),

        // ---- 通用 ----
        ["common.cancel"] = ("取消", "Cancel"),
        ["common.confirm"] = ("确定", "OK"),
        ["common.error"] = ("错误", "Error"),
        ["set.saveFailed"] = ("保存失败: {0}", "Save failed: {0}"),
        ["set.disableAnim"] = ("禁用 UI 动画(提升流畅度)", "Disable UI animations"),
        ["set.lowMem"] = ("低内存模式(激进 GC)", "Low memory mode"),
        ["set.dnsPreheat"] = ("DNS 预热(提前解析域名)", "DNS preheat"),
        ["set.showDebug"] = ("显示启动器调试信息", "Show debug info"),
        ["common.settings"] = ("设置", "Settings"),
    };

    /// <summary>按 key 取当前语言文本, 缺失则原样返回 key</summary>
    public static string T(string key)
    {
        if (!Map.TryGetValue(key, out var v))
            return key;
        return IsEn ? v.En : v.Zh;
    }

    /// <summary>带参数的文本</summary>
    public static string T(string key, params object[] args)
    {
        try
        {
            return string.Format(T(key), args);
        }
        catch (FormatException)
        {
            return T(key);
        }
    }

    /// <summary>语言选择列表 (设置页下拉框用)</summary>
    public static readonly string[] LanguageNames = { "简体中文", "English" };

    public static string LangCode(int index) => index == 1 ? "en-US" : "zh-CN";
    public static int LangIndex(string? code) => Parse(code) == Lang.EnUs ? 1 : 0;
}
