using System.Text;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 自定义主页的加载：本地文件 / 远程 URL / 占位符替换 / 离线缓存。
///
/// 设计要点：
/// - 远程内容**先显示缓存**再后台刷新，不能因为对方服务器慢把界面卡住
/// - 拉失败就用上次的缓存（离线也能看到东西）
/// - 占位符用 <c>${name}</c>，和 PCL 的习惯一致
/// </summary>
public static class HomePageLoader
{
    private const int TimeoutSeconds = 8;

    private static string CacheDir => Path.Combine(LauncherConfig.RootDir, "cache");
    private static string CachePath => Path.Combine(CacheDir, "homepage.txt");
    private static string MetaPath => Path.Combine(CacheDir, "homepage.url");

    /// <summary>支持在主页文本里写的占位符</summary>
    public static readonly (string Key, string Desc)[] Placeholders =
    {
        ("${date}", "日期 2026-10-28"),
        ("${time}", "时间 21:32"),
        ("${datetime}", "日期 + 时间"),
        ("${player}", "当前玩家名"),
        ("${version}", "当前选中的版本"),
        ("${launcher}", "启动器名称和版本"),
        ("${memory}", "内存设置"),
        ("${java}", "自动选择的 Java 版本"),
    };

    /// <summary>
    /// 把占位符换成实际值。
    /// 内存和 Java 必须跟**实际启动时用的一致**（走 VersionSettings 合成 + 按版本要求挑 Java），
    /// 不能直接拿全局配置，否则主页显示的值和真启动的对不上。
    /// </summary>
    public static string Render(string raw, LauncherConfig cfg, MinecraftVersion? ver)
    {
        if (string.IsNullOrEmpty(raw) || raw.IndexOf("${", StringComparison.Ordinal) < 0)
            return raw;

        var now = DateTime.Now;
        var (memText, javaText) = ResolveRuntime(cfg, ver);

        return raw
            .Replace("${date}", now.ToString("yyyy-MM-dd"), StringComparison.Ordinal)
            .Replace("${time}", now.ToString("HH:mm"), StringComparison.Ordinal)
            .Replace("${datetime}", now.ToString("yyyy-MM-dd HH:mm"), StringComparison.Ordinal)
            .Replace("${player}", cfg.PlayerName, StringComparison.Ordinal)
            .Replace("${version}", ver?.Id ?? "(未选择)", StringComparison.Ordinal)
            .Replace("${launcher}", "LargeHardLiteLauncher v0.1.0", StringComparison.Ordinal)
            .Replace("${memory}", memText, StringComparison.Ordinal)
            .Replace("${java}", javaText, StringComparison.Ordinal);
    }

    /// <summary>
    /// 算「这个版本真启动时会用的内存和 Java」。
    /// 版本为空时退回全局配置。
    /// </summary>
    public static (string Memory, string Java) ResolveRuntime(LauncherConfig cfg, MinecraftVersion? ver)
    {
        string memText;
        string javaText;

        if (ver is null)
        {
            memText = cfg.RamMode == 1 ? $"{cfg.MaxMemoryMb} MB" : "自动分配";
            var any = JavaDetector.ResolveInfo(cfg.JavaPath);
            javaText = any is null ? "未检测到" : $"Java {any.Major}";
            return (memText, javaText);
        }

        var eff = VersionSettings.Resolve(cfg, ver, VersionSettings.CountMods(cfg, ver));
        memText = eff.MemoryFromAuto
            ? $"{eff.MaxMemoryMb} MB(自动)"
            : $"{eff.MaxMemoryMb} MB";

        var java = JavaDetector.ResolveInfo(eff.JavaPath, ver.RequiredJava);
        javaText = java is null
            ? "未检测到"
            : $"Java {java.Major}";
        if (java is not null && ver.RequiredJava > 0 && java.Major < ver.RequiredJava)
            javaText += $"(需要 {ver.RequiredJava})";

        return (memText, javaText);
    }

    /// <summary>读本地主页文件（找不到返回提示文本，不抛异常）</summary>
    public static string LoadLocal(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return "(还没设置主页文件路径)";
            if (!File.Exists(path))
                return $"(找不到主页文件: {path})";
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return $"(读取主页文件失败: {ex.Message})";
        }
    }

    /// <summary>读缓存（只在缓存对应的 URL 和当前一致时才算数）</summary>
    public static string? LoadCache(string url)
    {
        try
        {
            if (!File.Exists(CachePath) || !File.Exists(MetaPath))
                return null;
            var cachedUrl = File.ReadAllText(MetaPath).Trim();
            if (!string.Equals(cachedUrl, url, StringComparison.OrdinalIgnoreCase))
                return null;
            var text = File.ReadAllText(CachePath);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 拉远程主页。成功返回 (文本, true)，失败返回 (null, false)。
    /// 成功后写缓存。
    /// </summary>
    public static async Task<(string? Text, bool FromNetwork)> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LargeHardLiteLauncher/0.1 (homepage)");

            var text = await http.GetStringAsync(url, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return (null, false);

            // 写缓存（失败不影响显示）
            try
            {
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllTextAsync(CachePath, text, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(MetaPath, url, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LauncherLog.Warn($"写主页缓存失败: {ex.Message}");
            }

            return (text, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"拉取远程主页失败 ({url}): {ex.Message}");
            return (null, false);
        }
    }

    /// <summary>
    /// 判断内容是不是 HTML 网页。用户很容易把普通网址粘进来（返回整个网页），
    /// TUI 里显示一堆 <div> 标签没有任何意义，不如直接提示换个地址。
    /// </summary>
    public static bool LooksLikeHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var len = Math.Min(text.Length, 200);
        var head = text.AsSpan(0, len).TrimStart();
        return head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
               || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
               || head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>内置预设主页（Preset 模式的默认内容）</summary>
    public static string PresetText(LauncherConfig cfg, MinecraftVersion? ver)
    {
        var (memText, javaText) = ResolveRuntime(cfg, ver);

        var sb = new StringBuilder();
        sb.AppendLine(Loc.T("launch.welcome"));
        sb.AppendLine();
        sb.AppendLine($"玩家      {cfg.PlayerName}");
        sb.AppendLine($"版本      {ver?.Id ?? "（未选择）"}");
        sb.AppendLine($"内存      {memText}");
        sb.AppendLine($"Java      {javaText}");
        return sb.ToString();
    }
}
