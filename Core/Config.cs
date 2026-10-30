using System.Text.Json;
using System.Text.Json.Serialization;

namespace LargeHardLiteLauncher.Core;

/// <summary>主页预设模式</summary>
public enum HomePageMode
{
    /// <summary>空白</summary>
    Blank,
    /// <summary>预设文本</summary>
    Preset,
    /// <summary>读取本地文件</summary>
    LocalFile,
    /// <summary>联网 URL</summary>
    Url,
}

/// <summary>日志详细程度</summary>
public enum LogLevelMode { Quiet, Normal, Verbose, Debug }

/// <summary>
/// 版本隔离档位，与 HMCL 的 DefaultIsolationType 一致：Never 都不隔离，Modded 只隔离带加载器的
/// 版本，Always 全部隔离（整目录隔离，不只是 mods 目录）。
/// </summary>
public enum IsolationMode
{
    /// <summary>不隔离</summary>
    Never = 0,
    /// <summary>有 mod 加载器的版本才隔离 (HMCL 默认)</summary>
    Modded = 1,
    /// <summary>全部版本都隔离</summary>
    Always = 2,
}

/// <summary>
/// 启动器全局配置，保存为 ~/.largehardlitelauncher/config.json。
/// 序列化走 SourceGenerator，AOT 下可直接用。
/// </summary>
public class LauncherConfig
{
    /// <summary>启动器数据目录: C:\Users\&lt;用户名&gt;\.largehardlitelauncher</summary>
    public static readonly string RootDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".largehardlitelauncher");

    // ===== 基础 =====
    public string PlayerName { get; set; } = "Steve";
    /// <summary>官方 .minecraft 目录 (默认 %APPDATA%\.minecraft, 与官方启动器/HMCL 一致)</summary>
    public string MinecraftPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    public string JavaPath { get; set; } = "";
    public string JvmArgs { get; set; } = "";

    /// <summary>全局内存模式：0=自动（按 mod 数估算） 1=手动（用下面两个字段）</summary>
    public int RamMode { get; set; } = 0;
    /// <summary>最大堆内存 (MB)</summary>
    public int MaxMemoryMb { get; set; } = 2048;
    /// <summary>最小堆内存 (MB)</summary>
    public int MinMemoryMb { get; set; } = 512;
    /// <summary>游戏窗口宽度</summary>
    public int WindowWidth { get; set; } = 854;
    /// <summary>游戏窗口高度</summary>
    public int WindowHeight { get; set; } = 480;
    /// <summary>全屏启动</summary>
    public bool FullScreen { get; set; }
    public string GameArgs { get; set; } = "";

    // ===== 账户 =====
    /// <summary>offline (离线名) | microsoft (正版)</summary>
    public string AuthMode { get; set; } = "offline";
    /// <summary>Azure 应用的 Application (client) ID (正版登录必须)</summary>
    public string MicrosoftClientId { get; set; } = "";
    /// <summary>微软 refresh_token (长期有效, 用来换 access token)</summary>
    public string MsRefreshToken { get; set; } = "";
    /// <summary>正版账户 UUID (带连字符)</summary>
    public string MsUuid { get; set; } = "";
    /// <summary>正版账户名 (游戏内名字)</summary>
    public string MsName { get; set; } = "";
    /// <summary>上次拿到的 MC token 过期时间 (unix ms, 仅参考)</summary>
    public long MsExpiresAtMs { get; set; }

    // ===== 多账户 (学 PCL: 存多个账户一键切换) =====    /// <summary>保存过的账户列表。当前生效的账户信息仍在上面那些 MsXxx/PlayerName 字段里</summary>
    public List<AccountEntry> Accounts { get; set; } = new();
    /// <summary>当前账户在 <see cref="Accounts"/> 里的 Id</summary>
    public string CurrentAccountId { get; set; } = "";

    // ===== 下载 =====
    /// <summary>auto | official | bmclapi</summary>
    public string DownloadSource { get; set; } = "auto";
    /// <summary>CurseForge API key (去 console.curseforge.com 免费申请, 不填就只有 Modrinth)</summary>
    public string CurseForgeApiKey { get; set; } = "";
    public int MaxDownloadThreads { get; set; } = 64;
    public int DownloadTimeoutSec { get; set; } = 60;
    /// <summary>
    /// 忽略 HTTPS 证书错误 (应急开关)。
    /// 系统根证书缺失 / 杀软中间人 / 公司代理拦截时, 这是唯一能让下载跑起来的办法, 但会牺牲安全性。
    /// </summary>
    public bool IgnoreSslErrors { get; set; }
    /// <summary>下载分片大小 (MB)，0 = 自动</summary>
    public int DownloadChunkSizeMb { get; set; } = 0;
    /// <summary>
    /// Mod 文件名中文前缀样式 (学 PCL2): 0=不加 1=【名】 2=[名] 3=名- 4=-名。
    /// 只影响社区资源安装时的文件名, 不改变文件内容。
    /// </summary>
    public int ModNameStyle { get; set; } = 0;

    // ===== 版本独立设置 (学 PCL2) =====
    /// <summary>
    /// 每个版本的设置覆盖项，key = 版本目录名。
    /// 没写进这里 = 全部跟随上面的全局设置（懒创建，只存改过的版本）。
    /// </summary>
    public Dictionary<string, VersionOption> VersionOptions { get; set; } = new();

    // ===== 性能（终端渲染 / 网络） =====
    /// <summary>渲染帧率上限，0 = 不限制</summary>
    public int RenderFpsLimit { get; set; } = 0;
    /// <summary>禁用 UI 动画以提升流畅度</summary>
    public bool DisableAnimations { get; set; } = true;
    /// <summary>低内存模式：更激进的 GC 行为与列表裁剪</summary>
    public bool LowMemoryMode { get; set; } = false;
    /// <summary>最大渲染缓冲行数（ListView 虚拟化阈值）</summary>
    public int ListViewCacheLines { get; set; } = 2000;
    /// <summary>HTTP 连接复用（Keep-Alive 池大小）</summary>
    public int HttpConnectionPoolSize { get; set; } = 32;
    /// <summary>网络 DNS 预热</summary>
    public bool DnsPreheat { get; set; } = true;
    /// <summary>输入事件去抖 (ms)，降低高刷新率终端下的 CPU 占用</summary>
    public int InputDebounceMs { get; set; } = 15;
    /// <summary>缓存目录大小上限 (MB)</summary>
    public int CacheMaxSizeMb { get; set; } = 2048;
    /// <summary>任务栏容量 (4~256), 超出后最旧的已完成任务被挤出</summary>
    public int TaskCapacity { get; set; } = 16;
    /// <summary>任务栏宽度 (列), 可拖动分隔条调整</summary>
    public int TaskPanelWidth { get; set; } = 34;
    /// <summary>任务栏是否展开 (折叠状态记忆)</summary>
    public bool TaskPanelExpanded { get; set; } = true;

    // ===== 界面 / 日志 =====
    /// <summary>Terminal.Gui 主题名 (Default/Dark/Light/8-Bit/...)</summary>
    public string Theme { get; set; } = "Default";
    /// <summary>界面语言: zh-CN | en-US (切换后重启生效)</summary>
    public string Language { get; set; } = "zh-CN";
    /// <summary>已知的 .minecraft 文件夹列表 (多目录支持)</summary>
    public List<string> Folders { get; set; } = new();
    /// <summary>当前使用的文件夹索引</summary>
    public int CurrentFolder { get; set; }
    /// <summary>版本隔离档位</summary>
    public IsolationMode Isolation { get; set; } = IsolationMode.Modded;
    /// <summary>启动动画 (假边框生长 + 控件闪烁)</summary>
    public bool StartupAnimation { get; set; } = true;
    /// <summary>上次选中的游戏版本 id (空 = 自动取第一个)</summary>
    public string SelectedVersion { get; set; } = "";
    /// <summary>柔和配色方案 id (空 = 跟随主题)</summary>
    public string ColorSchemeId { get; set; } = "";
    public LogLevelMode LogLevel { get; set; } = LogLevelMode.Normal;
    public HomePageMode HomePage { get; set; } = HomePageMode.Blank;
    /// <summary>主页预设来源：本地文件路径或 URL（由 HomePage 决定）</summary>
    public string HomePageSource { get; set; } = "";
    /// <summary>显示启动器调试信息</summary>
    public bool ShowDebugInfo { get; set; } = false;
    /// <summary>回声洞的数据源仓库 owner/repo（空 = 用内置默认仓库）</summary>
    public string EchoRepo { get; set; } = "";
    /// <summary>回声洞投稿讨论的编号（0 = 还没建，回退到内置句子）</summary>
    public int EchoDiscussion { get; set; } = 0;

    private static string ConfigDir => RootDir;

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        TypeInfoResolver = LauncherJsonContext.Default,
    };

    public static LauncherConfig Load()
    {
        LauncherConfig? cfg = null;
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                cfg = JsonSerializer.Deserialize(json, LauncherJsonContext.Default.LauncherConfig);
            }
        }
        catch { /* 损坏则重建 */ }

        cfg ??= new LauncherConfig();

        // 旧配置里可能存了空字符串 → 回退到官方 .minecraft
        if (string.IsNullOrWhiteSpace(cfg.MinecraftPath))
            cfg.MinecraftPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

        // 老配置没有 Folders 列表: 用当前 MinecraftPath 初始化一个
        if (cfg.Folders.Count == 0)
            cfg.Folders.Add(cfg.MinecraftPath);

        // 历史默认值迁移：老版本的内存模式默认是「手动 2048/1024」，绝大多数人从没改过。
        // 结果是装了几十个 mod 的整合包也只给 2G。改成自动（按 mod 数估算）才合理；
        // 想锁死数值的到「设置 → 启动 → 内存模式」改回手动即可。
        if (cfg.RamMode == 1 && cfg.MaxMemoryMb == 2048 && cfg.MinMemoryMb == 1024)
        {
            cfg.RamMode = 0;
            LauncherLog.Info("内存模式:使用旧默认值(手动 2048MB),已切换为[自动(按 mod 数)]");
        }

        // 索引越界回退, 并把 MinecraftPath 同步成当前文件夹
        if (cfg.CurrentFolder < 0 || cfg.CurrentFolder >= cfg.Folders.Count)
            cfg.CurrentFolder = 0;
        cfg.MinecraftPath = cfg.Folders[cfg.CurrentFolder];

        return cfg;
    }

    /// <summary>文件夹显示名: 目录名 (父目录), 便于区分同名 .minecraft</summary>
    public static string FolderDisplay(string path)
    {
        try
        {
            var name = new DirectoryInfo(path).Name;
            var parent = Directory.GetParent(path)?.Name;
            return string.IsNullOrEmpty(parent) ? name : $"{name} ({parent})";
        }
        catch
        {
            return path;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, LauncherJsonContext.Default.LauncherConfig));
    }
}

/// <summary>AOT 安全的 JSON 上下文</summary>
[JsonSerializable(typeof(LauncherConfig))]
[JsonSerializable(typeof(LogLevelMode))]
[JsonSerializable(typeof(HomePageMode))]
internal partial class LauncherJsonContext : JsonSerializerContext;
