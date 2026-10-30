using System.Text;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 回声洞的内容源。
/// <para>
/// 默认用内置句子；配置了 GitHub 讨论帖编号后，从该讨论的评论里取。
/// GitHub 官方只给 Discussions 开放了 GraphQL 接口（必须带 token），这里用的是未收录进官方
/// REST 描述的端点 <c>/repos/{repo}/discussions/{n}/comments</c>：匿名可读，限流 60 次/小时。
/// </para>
/// <para>
/// 接口变动或断网时静默回退（缓存 → 内置句子），不影响页面显示。
/// 缓存 6 小时内不重复请求，写 `<数据目录>/cache/echoes.json`。
/// </para>
/// </summary>
public static class EchoHole
{
    public const string DefaultRepo = "LargeHardTech/LargeHardLiteLauncher";

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    /// <summary>网络不可用、或还没建讨论帖时的兜底内容</summary>
    public static readonly string[] Builtin =
    {
        "能正常运行的代码,就不要为了风格去改它。",
        "先保证可用,再考虑美观。",
        "内存不足时优先增加分配,其次才是更换硬件。",
        "启动失败应先查看日志,日志含义不清时再使用崩溃分析。",
        "修改整合包之前,建议先备份存档。",
        "更换 Mod 前后各做一次存档备份,可以避免绝大多数损失。",
        "F3 是联机页,不是刷新。",
        "把 jar 拖到程序图标上可以直接打开启动 JAR 页。",
        "导出整合包时启用联网匹配,可以显著减小体积。",
        "Java 版本不匹配会引发大量难以定位的错误。",
    };

    /// <summary>取到的结果。<paramref name="Source"/> 是给界面显示的一行来源说明</summary>
    public sealed record Result(List<string> Items, string Source);

    public static string RepoOf(LauncherConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.EchoRepo)
            ? DefaultRepo
            : cfg.EchoRepo.Trim().Trim('/');

    /// <summary>讨论编号大于 0 才算配置过</summary>
    public static bool IsConfigured(LauncherConfig cfg) => cfg.EchoDiscussion > 0;

    public static string ApiUrl(LauncherConfig cfg) =>
        $"https://api.github.com/repos/{RepoOf(cfg)}/discussions/{cfg.EchoDiscussion}/comments?per_page=100";

    private static string CachePath => Path.Combine(LauncherConfig.RootDir, "cache", "echoes.json");

    /// <summary>
    /// 拿回声列表。优先用未过期的缓存；否则请求 GitHub，失败就退回缓存或内置句子。
    /// 不抛异常，调用方拿到的一定是能显示的列表。
    /// </summary>
    public static async Task<Result> LoadAsync(LauncherConfig cfg, CancellationToken ct = default)
    {
        if (!IsConfigured(cfg))
            return new Result(Builtin.ToList(), "内置");

        var url = ApiUrl(cfg);
        var cached = ReadCache(url);

        if (cached is not null && DateTime.UtcNow - cached.At < Ttl)
            return new Result(cached.Items, $"GitHub · {cached.Items.Count} 条");

        try
        {
            using var http = Net.CreateClient(TimeSpan.FromSeconds(10));
            http.DefaultRequestHeaders.Add("User-Agent", "LargeHardLiteLauncher");

            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // 403 是限流，404 是讨论号不对 / 仓库没开 Discussions
                return Fallback(cached, $"GitHub 返回 {(int)resp.StatusCode}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var items = ParseComments(json);
            if (items.Count == 0)
                return Fallback(cached, "讨论里还没有内容");

            WriteCache(url, items);
            return new Result(items, $"GitHub · {items.Count} 条");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Fallback(cached, "拉取失败");
        }
    }

    private static Result Fallback(Cache? cached, string why) =>
        cached is not null
            ? new Result(cached.Items, $"GitHub 缓存 · {cached.Items.Count} 条（{why}）")
            : new Result(Builtin.ToList(), $"内置（{why}）");

    /// <summary>
    /// 从评论列表 JSON 里抽出句子：每条取第一行有效文本。
    /// 跳过机器人、纯链接、纯图片、代码块。
    /// </summary>
    public static List<string> ParseComments(string json)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var c in doc.RootElement.EnumerateArray())
            {
                if (c.TryGetProperty("user", out var user)
                    && user.TryGetProperty("type", out var type)
                    && type.GetString() == "Bot")
                    continue;

                var body = c.TryGetProperty("body", out var b) ? b.GetString() : null;
                var line = FirstLine(body);
                if (line is not null)
                    list.Add(line);
            }
        }
        catch
        {
            // 返回格式变了就当没取到，交给调用方回退
        }
        return list;
    }

    private static string? FirstLine(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var inCode = false;
        foreach (var raw in body.Split('\n'))
        {
            var trimmed = raw.Trim();

            // 代码块整段跳过（围栏 ``` 之间的内容不当句子）
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode)
                continue;

            var line = Clean(raw);
            if (line.Length == 0)
                continue;
            if (line.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                continue; // 纯链接
            if (line.StartsWith('|'))
                continue; // 表格行

            if (line.Length > 90)
                line = line[..90].TrimEnd() + "…";
            return line;
        }
        return null;
    }

    /// <summary>去掉 Markdown 标记，留下能直接显示的一行文本</summary>
    private static string Clean(string raw)
    {
        var s = raw.Trim();

        // 行首的标题 / 引用 / 列表标记
        s = s.TrimStart('#', '>', '-', '*', '+', ' ');

        // 有序列表 "1. "
        var i = 0;
        while (i < s.Length && char.IsDigit(s[i]))
            i++;
        if (i > 0 && i + 1 < s.Length && s[i] == '.' && s[i + 1] == ' ')
            s = s[(i + 2)..];

        // 行内强调标记
        s = s.Replace("**", string.Empty)
             .Replace("__", string.Empty)
             .Replace("~~", string.Empty)
             .Replace("`", string.Empty);

        return StripLinks(s).Trim();
    }

    /// <summary>[文字](链接) 保留文字；![图片](链接) 整段丢掉</summary>
    private static string StripLinks(string s)
    {
        if (s.IndexOf('[') < 0)
            return s;

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '!' && i + 1 < s.Length && s[i + 1] == '[')
            {
                var end = s.IndexOf(')', i);
                if (end > 0)
                {
                    i = end;
                    continue;
                }
            }

            if (s[i] == '[')
            {
                var mid = s.IndexOf("](", i, StringComparison.Ordinal);
                if (mid > 0)
                {
                    var end = s.IndexOf(')', mid + 2);
                    if (end > 0)
                    {
                        sb.Append(s, i + 1, mid - i - 1);
                        i = end;
                        continue;
                    }
                }
            }

            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    // ---------- 缓存 ----------

    private sealed class Cache
    {
        public DateTime At { get; init; }
        public List<string> Items { get; init; } = new();
    }

    private static Cache? ReadCache(string url)
    {
        try
        {
            if (!File.Exists(CachePath))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("url", out var u) || u.GetString() != url)
                return null; // 换了仓库或讨论号，旧缓存作废

            var at = root.TryGetProperty("fetchedAt", out var t)
                     && DateTime.TryParse(t.GetString(), out var parsed)
                ? parsed.ToUniversalTime()
                : DateTime.MinValue;

            var items = new List<string>();
            if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var text = e.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        items.Add(text);
                }
            }

            return items.Count > 0 ? new Cache { At = at, Items = items } : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(string url, List<string> items)
    {
        try
        {
            var dir = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.Append("{\"url\":").Append(JsonText.Str(url));
            sb.Append(",\"fetchedAt\":").Append(JsonText.Str(DateTime.UtcNow.ToString("o")));
            sb.Append(",\"items\":[");
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(JsonText.Str(items[i]));
            }
            sb.Append("]}");

            File.WriteAllText(CachePath, sb.ToString());
        }
        catch
        {
            // 缓存写不了不影响使用
        }
    }
}
