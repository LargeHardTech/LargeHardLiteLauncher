using System.Text;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 回声洞的取数逻辑：从仓库讨论帖的评论里读句子。
/// <para>
/// GitHub 只给 Discussions 开放了 GraphQL（必须带 token），这里用的是未收录进官方 REST 描述的
/// 端点 <c>/repos/{repo}/discussions/{n}/comments</c>：匿名可读，限流 60 次/小时。
/// </para>
/// <para>
/// 没有内置兜底。拉不到就抛 <see cref="EchoException"/>，由界面显示失败原因。
/// 缓存只用于 6 小时内不重复请求，过期后照样请求；请求失败不会拿旧缓存顶替。
/// </para>
/// </summary>
public static class EchoHole
{
    public const string DefaultRepo = "LargeHardTech/LargeHardLiteLauncher";

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    /// <summary>取数失败，Message 可直接显示给用户</summary>
    public sealed class EchoException : Exception
    {
        public EchoException(string message) : base(message) { }
    }

    public static string RepoOf(LauncherConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.EchoRepo)
            ? DefaultRepo
            : cfg.EchoRepo.Trim().Trim('/');

    /// <summary>讨论编号大于 0 才算配置过</summary>
    public static bool IsConfigured(LauncherConfig cfg) => cfg.EchoDiscussion > 0;

    public static string ApiUrl(LauncherConfig cfg) =>
        $"https://api.github.com/repos/{RepoOf(cfg)}/discussions/{cfg.EchoDiscussion}/comments?per_page=100";

    private static string CachePath => Path.Combine(LauncherConfig.RootDir, "cache", "echoes.json");

    /// <summary>缓存还在有效期内就直接给，不发请求</summary>
    public static bool TryGetCached(LauncherConfig cfg, out List<string> items)
    {
        items = new List<string>();
        if (!IsConfigured(cfg))
            return false;

        var cached = ReadCache(ApiUrl(cfg));
        if (cached is null || DateTime.UtcNow - cached.At >= Ttl)
            return false;

        items = cached.Items;
        return true;
    }

    /// <summary>
    /// 取回声列表。缓存有效时直接返回，否则请求 GitHub。
    /// 失败抛 <see cref="EchoException"/>（消息可直接显示），用户取消时抛 OperationCanceledException。
    /// </summary>
    public static async Task<List<string>> FetchAsync(
        LauncherConfig cfg, TaskItem? task, CancellationToken ct)
    {
        if (!IsConfigured(cfg))
            throw new EchoException("还没有配置展示来源");

        if (TryGetCached(cfg, out var cached))
        {
            task?.LogLine($"使用缓存（{cached.Count} 条）");
            return cached;
        }

        var url = ApiUrl(cfg);
        var repo = RepoOf(cfg);
        task?.LogLine($"请求 {repo} 的讨论 #{cfg.EchoDiscussion}");

        string json;
        try
        {
            using var http = Net.CreateClient(TimeSpan.FromSeconds(10));
            http.DefaultRequestHeaders.Add("User-Agent", "LargeHardLiteLauncher");

            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new EchoException(DescribeStatus((int)resp.StatusCode, cfg));

            json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 用户点的停止
        }
        catch (OperationCanceledException)
        {
            throw new EchoException("连接超时（10 秒）");
        }
        catch (EchoException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new EchoException("无法连接 GitHub，检查网络或代理");
        }
        catch (Exception ex)
        {
            throw new EchoException($"请求失败：{ex.Message}");
        }

        var items = ParseComments(json);
        if (items.Count == 0)
            throw new EchoException("讨论里还没有投稿");

        WriteCache(url, items);
        task?.LogLine($"拿到 {items.Count} 条");
        return items;
    }

    private static string DescribeStatus(int code, LauncherConfig cfg) => code switch
    {
        401 => "GitHub 拒绝匿名访问（返回 401）",
        403 => "GitHub 接口限流（每小时 60 次），稍后再试",
        404 => $"讨论 #{cfg.EchoDiscussion} 不存在，检查仓库和编号",
        _ => $"GitHub 返回 {code}",
    };

    /// <summary>
    /// 从评论列表 JSON 里抽出句子：每条取第一行有效文本。
    /// 跳过机器人、纯链接、纯图片、表格、代码块。
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
            // 格式变了当没取到，调用方会报"讨论里还没有投稿"
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
