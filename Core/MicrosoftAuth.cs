using System.Text;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>device code 流程第一步拿到的信息</summary>
public sealed class DeviceCodeInfo
{
    public string UserCode { get; init; } = "";
    public string DeviceCode { get; init; } = "";
    public string VerificationUri { get; init; } = "";
    public string Message { get; init; } = "";
    public int IntervalSec { get; init; } = 5;
    public int ExpiresInSec { get; init; } = 900;
}

/// <summary>一个已登录的微软账户 (含 Minecraft 会话)</summary>
public sealed class MsAccount
{
    public string Uuid { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Minecraft 服务的 access token (不是微软的)</summary>
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    /// <summary>Minecraft token 过期时间 (unix 毫秒)</summary>
    public long ExpiresAtMs { get; init; }
}

/// <summary>
/// 微软正版登录（device code 流程，与 HMCL 一致）：device code → 微软 access_token
/// → Xbox Live（XBL token + uhs）→ XSTS → login_with_xbox（MC access_token）
/// → minecraft/profile（uuid + 名字）。client_id 必须是自己注册的 Azure 应用，
/// 没有就拿不到 token，属平台限制。
/// </summary>
public static class MicrosoftAuth
{
    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    /// <summary>登录请求的权限范围 (和 HMCL 一致)</summary>
    private const string Scope = "XboxLive.signin offline_access";

    private static readonly HttpClient Http = Net.CreateClient(TimeSpan.FromSeconds(30));

    // ==================== 第一步: 申请 device code ====================

    /// <summary>
    /// 请求 device code。返回的 UserCode/VerificationUri 要显示给用户,
    /// 用户在浏览器里输入后, 调用方再开始轮询。
    /// </summary>
    public static async Task<DeviceCodeInfo> RequestDeviceCodeAsync(string clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("还没填 Azure client id (设置 → 账户)");

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = Scope,
        });

        using var resp = await Http.PostAsync(DeviceCodeUrl, content, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!resp.IsSuccessStatusCode || root.TryGetProperty("error", out var err))
            throw new InvalidOperationException("申请验证码失败: " + DescribeOAuthError(root, (int)resp.StatusCode));

        return new DeviceCodeInfo
        {
            UserCode = GetStr(root, "user_code") ?? "",
            DeviceCode = GetStr(root, "device_code") ?? "",
            VerificationUri = GetStr(root, "verification_uri") ?? "https://microsoft.com/link",
            Message = GetStr(root, "message") ?? "",
            IntervalSec = Math.Max(GetInt(root, "interval", 5), 1),
            ExpiresInSec = Math.Max(GetInt(root, "expires_in", 900), 60),
        };
    }

    // ==================== 第二步: 轮询拿微软 token ====================

    /// <summary>
    /// 轮询 token 端点直到用户完成授权。成功返回 Microsoft access_token (不是 MC 的)。
    /// </summary>
    public static async Task<(string AccessToken, string RefreshToken)> WaitForMicrosoftTokenAsync(
        string clientId, DeviceCodeInfo info, Action<string>? progress, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(info.IntervalSec);
        var deadline = DateTime.UtcNow.AddSeconds(Math.Min(info.ExpiresInSec, 900));

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("登录超时 (15 分钟内没完成授权),请重新登录");

            await Task.Delay(interval, ct).ConfigureAwait(false);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["code"] = info.DeviceCode,
                ["client_id"] = clientId,
            });

            using var resp = await Http.PostAsync(TokenUrl, content, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var error = GetStr(root, "error");
            switch (error)
            {
                case "authorization_pending":
                    progress?.Invoke("等待浏览器里完成授权...");
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    progress?.Invoke("服务器要求放慢轮询...");
                    continue;
                case "expired_token":
                    throw new TimeoutException("验证码已过期,请重新登录");
                case "authorization_declined":
                    throw new InvalidOperationException("你在浏览器里拒绝了授权");
                case not null:
                    throw new InvalidOperationException("登录失败: " + DescribeOAuthError(root, (int)resp.StatusCode));
            }

            var access = GetStr(root, "access_token");
            var refresh = GetStr(root, "refresh_token");
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
                throw new InvalidOperationException("微软返回的 token 不完整");

            return (access, refresh);
        }
    }

    /// <summary>用 refresh_token 续期微软 token</summary>
    public static async Task<(string AccessToken, string RefreshToken)> RefreshMicrosoftTokenAsync(
        string clientId, string refreshToken, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        });

        using var resp = await Http.PostAsync(TokenUrl, content, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out _))
        {
            var desc = GetStr(root, "error_description") ?? "";
            // refresh_token 被吊销 / 过期 → 必须重新走一次 device code
            if (desc.Contains("AADSTS70000", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("登录凭证已失效,请重新登录微软账户");
            throw new InvalidOperationException("刷新 token 失败: " + DescribeOAuthError(root, (int)resp.StatusCode));
        }

        var access = GetStr(root, "access_token") ?? "";
        // 微软有时不返回新的 refresh_token, 沿用旧的
        var refresh = GetStr(root, "refresh_token") ?? refreshToken;
        return (access, refresh);
    }

    // ==================== 第三步: 换 Minecraft 会话 ====================

    /// <summary>微软 token → XBL → XSTS → Minecraft token → profile</summary>
    public static async Task<MsAccount> ExchangeForMinecraftAsync(
        string microsoftAccessToken, string refreshToken, Action<string>? progress, CancellationToken ct)
    {
        progress?.Invoke("Xbox Live 认证...");
        var (xblToken, uhs) = await XboxLiveAsync(microsoftAccessToken, ct).ConfigureAwait(false);

        progress?.Invoke("XSTS 授权...");
        var xstsToken = await XstsAsync(xblToken, uhs, ct).ConfigureAwait(false);

        progress?.Invoke("登录 Minecraft 服务...");
        var (mcToken, expiresIn) = await MinecraftLoginAsync(uhs, xstsToken, ct).ConfigureAwait(false);

        progress?.Invoke("读取账户档案...");
        var (uuid, name) = await MinecraftProfileAsync(mcToken, ct).ConfigureAwait(false);

        return new MsAccount
        {
            Uuid = uuid,
            Name = name,
            AccessToken = mcToken,
            RefreshToken = refreshToken,
            ExpiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)expiresIn * 1000,
        };
    }

    // ---- 请求体构造 ----
    // 全部手拼 JSON：NativeAOT 下匿名类型走 JsonSerializer.Serialize(object) 会抛
    // NotSupportedException（匿名类型没有 source-gen 元数据）。抽成 internal 方法便于自测。

    internal static string BuildXblBody(string msaToken) =>
        "{\"Properties\":{\"AuthMethod\":\"RPS\"," +
        "\"SiteName\":\"user.auth.xboxlive.com\"," +
        "\"RpsTicket\":" + JsonStr("d=" + msaToken) + "}," +
        "\"RelyingParty\":\"http://auth.xboxlive.com\",\"TokenType\":\"JWT\"}";

    internal static string BuildXstsBody(string xblToken) =>
        "{\"Properties\":{\"SandboxId\":\"RETAIL\"," +
        "\"UserTokens\":[" + JsonStr(xblToken) + "]}," +
        "\"RelyingParty\":\"rp://api.minecraftservices.com/\",\"TokenType\":\"JWT\"}";

    internal static string BuildMcLoginBody(string uhs, string xstsToken) =>
        "{\"identityToken\":" + JsonStr($"XBL3.0 x={uhs};{xstsToken}") + "}";

    private static async Task<(string Token, string Uhs)> XboxLiveAsync(string msaToken, CancellationToken ct)
    {
        using var resp = await PostJsonAsync(XblUrl, BuildXblBody(msaToken), ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var xerr = GetLong(root, "XErr");
        if (xerr != 0)
            throw new InvalidOperationException("Xbox Live 认证失败: " + XboxErrorText(xerr));

        var token = GetStr(root, "Token");
        var uhs = TryGetUhs(root);
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(uhs))
            throw new InvalidOperationException("Xbox Live 返回内容异常(缺 Token 或 uhs)");

        return (token, uhs);
    }

    private static async Task<string> XstsAsync(string xblToken, string expectedUhs, CancellationToken ct)
    {
        using var resp = await PostJsonAsync(XstsUrl, BuildXstsBody(xblToken), ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var xerr = GetLong(root, "XErr");
        if (xerr != 0)
            throw new InvalidOperationException("XSTS 授权失败: " + XboxErrorText(xerr));

        var token = GetStr(root, "Token");
        var uhs = TryGetUhs(root);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("XSTS 返回内容异常(缺 Token)");

        // HMCL 也做这个校验: 两步拿到的 uhs 必须一致
        if (!string.IsNullOrEmpty(uhs) && uhs != expectedUhs)
            throw new InvalidOperationException("XSTS 返回的用户哈希和 Xbox Live 不一致");

        return token;
    }

    private static async Task<(string Token, int ExpiresIn)> MinecraftLoginAsync(
        string uhs, string xstsToken, CancellationToken ct)
    {
        using var resp = await PostJsonAsync(McLoginUrl, BuildMcLoginBody(uhs, xstsToken), ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException("登录 Minecraft 失败: "
                + (GetStr(root, "errorMessage") ?? GetStr(root, "error") ?? $"HTTP {(int)resp.StatusCode}"));

        var token = GetStr(root, "access_token");
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Minecraft 登录返回内容异常(缺 access_token)");

        return (token, GetInt(root, "expires_in", 86400));
    }

    private static async Task<(string Uuid, string Name)> MinecraftProfileAsync(string mcToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, McProfileUrl);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!resp.IsSuccessStatusCode)
        {
            // 404 通常是"这个微软账号没买 Java 版"
            if ((int)resp.StatusCode == 404)
                throw new InvalidOperationException("这个微软账户没有 Minecraft Java 版(或还没创建档案)");
            throw new InvalidOperationException("读取 Minecraft 档案失败: "
                + (GetStr(root, "errorMessage") ?? $"HTTP {(int)resp.StatusCode}"));
        }

        var id = GetStr(root, "id") ?? "";
        var name = GetStr(root, "name") ?? "";
        if (id.Length == 32) // 微软返回无连字符 uuid, 补成标准格式
        {
            id = $"{id[..8]}-{id[8..12]}-{id[12..16]}-{id[16..20]}-{id[20..]}";
        }
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            throw new InvalidOperationException("Minecraft 档案内容异常");

        return (id, name);
    }

    // ==================== 辅助 ====================

    /// <summary>POST 一段已经拼好的 JSON（调用方保证格式）。</summary>
    private static async Task<HttpResponseMessage> PostJsonAsync(string url, string jsonBody, CancellationToken ct)
    {
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        content.Headers.Add("Accept", "application/json");
        return await Http.PostAsync(url, content, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把字符串包成 JSON 字面量（带引号 + 转义）。实现挪到 <see cref="JsonText.Str"/> 了，
    /// 这里保留一个转发，免得历史调用点全要改。
    /// </summary>
    private static string JsonStr(string? s) => JsonText.Str(s);

    private static string? TryGetUhs(JsonElement root)
    {
        if (!root.TryGetProperty("DisplayClaims", out var dc) || dc.ValueKind != JsonValueKind.Object)
            return null;
        if (!dc.TryGetProperty("xui", out var xui) || xui.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in xui.EnumerateArray())
        {
            if (item.TryGetProperty("uhs", out var u) && u.ValueKind == JsonValueKind.String)
                return u.GetString();
        }
        return null;
    }

    /// <summary>Xbox 的 XErr 错误码 → 人话 (和 HMCL 的常量一致)</summary>
    private static string XboxErrorText(long xerr) => xerr switch
    {
        2148916227L => "该 Xbox 账户已被封禁",
        2148916233L => "这个微软账户没有 Xbox 账户,请先去 xbox.com 创建一个",
        2148916235L => "你的所在地区不支持 Xbox Live",
        2148916238L => "未满 18 岁的账号需要先加入家庭组(见 https://start.ui.xboxlive.com/AddChildToFamily)",
        _ => $"XErr={xerr} (未知 Xbox 错误)",
    };

    private static string DescribeOAuthError(JsonElement root, int status)
    {
        var error = GetStr(root, "error") ?? "";
        var desc = GetStr(root, "error_description") ?? "";
        // 描述里带一长串 trace id, 只取第一句
        var cut = desc.IndexOf(" Trace ID:", StringComparison.Ordinal);
        if (cut > 0)
            desc = desc[..cut];

        // 最常见的两种配错
        if (desc.Contains("AADSTS700016", StringComparison.OrdinalIgnoreCase))
            return $"client id 无效(AADSTS700016):请检查 Azure 应用的 Application (client) ID 是否正确\n{desc}";
        if (desc.Contains("AADSTS7000218", StringComparison.OrdinalIgnoreCase))
            return $"Azure 应用未启用[允许公共客户端流](AADSTS7000218):请在应用的[身份验证]中开启\n{desc}";

        return string.IsNullOrEmpty(desc) ? $"{error} (HTTP {status})" : desc;
    }

    private static string? GetStr(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement e, string prop, int def) =>
        e.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i) ? i : def;

    private static long GetLong(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.TryGetInt64(out var i) ? i : 0;
}
