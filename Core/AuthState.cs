namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 登录态的运行时缓存 (不落盘)。
/// Minecraft 的 access token 有效期只有 24 小时, 存盘没意义, 存 refresh_token 就够,
/// 启动前用它换新的 access token。
/// </summary>
public static class AuthState
{
    /// <summary>当前有效的 Minecraft access token (正版模式用)</summary>
    public static string McAccessToken { get; private set; } = "";

    /// <summary>是否是真的正版账户 (有 uuid + 名字)</summary>
    public static bool IsMicrosoft(LauncherConfig cfg) =>
        cfg.AuthMode == "microsoft"
        && !string.IsNullOrEmpty(cfg.MsUuid)
        && !string.IsNullOrEmpty(cfg.MsName);

    /// <summary>当前应该用的玩家名 (正版用微软档案名, 离线用自定义名)</summary>
    public static string PlayerName(LauncherConfig cfg) =>
        IsMicrosoft(cfg) ? cfg.MsName : cfg.PlayerName;

    /// <summary>当前应该用的 UUID</summary>
    public static string Uuid(LauncherConfig cfg) =>
        IsMicrosoft(cfg) ? cfg.MsUuid : GameLauncher.OfflineUuidFor(cfg.PlayerName);

    /// <summary>启动时用 --userType (正版 msa, 离线 legacy)</summary>
    public static string UserType(LauncherConfig cfg) =>
        IsMicrosoft(cfg) ? "msa" : "legacy";

    /// <summary>access token: 正版用真 token, 离线固定 "0"</summary>
    public static string AccessToken(LauncherConfig cfg) =>
        IsMicrosoft(cfg) && !string.IsNullOrEmpty(McAccessToken) ? McAccessToken : "0";

    /// <summary>登录成功后写入</summary>
    public static void Set(MsAccount account) => McAccessToken = account.AccessToken;

    public static void Clear() => McAccessToken = "";

    /// <summary>
    /// 确保 access token 可用: 内存里有且没过期就直接用, 否则用 refresh_token 换一个。
    /// 换 token 时会顺带刷新 Minecraft 会话 (XBL→XSTS→MC)。
    /// </summary>
    public static async Task EnsureFreshAsync(
        LauncherConfig cfg, Action<string>? log, CancellationToken ct)
    {
        if (!IsMicrosoft(cfg))
            return;

        // 内存里有 token 就当有效 (同一次启动内不会过期)
        if (!string.IsNullOrEmpty(McAccessToken))
        {
            log?.Invoke("正版会话已就绪");
            return;
        }

        if (string.IsNullOrEmpty(cfg.MsRefreshToken))
            throw new InvalidOperationException("正版账户缺少登录凭证,请在设置里重新登录");
        if (string.IsNullOrEmpty(cfg.MicrosoftClientId))
            throw new InvalidOperationException("缺少 Azure client id, 请在设置 → 账户里填写");

        log?.Invoke("刷新正版会话...");
        var (msToken, refresh) = await MicrosoftAuth
            .RefreshMicrosoftTokenAsync(cfg.MicrosoftClientId, cfg.MsRefreshToken, ct)
            .ConfigureAwait(false);

        var account = await MicrosoftAuth
            .ExchangeForMinecraftAsync(msToken, refresh, m => log?.Invoke(m), ct)
            .ConfigureAwait(false);

        // 档案换了 (用户换了账号) 要提醒, 但不拦截
        if (!string.Equals(account.Uuid, cfg.MsUuid, StringComparison.OrdinalIgnoreCase))
            log?.Invoke($"[警告] 刷新出的账号 {account.Name} 和上次保存的 {cfg.MsName} 不一致");

        McAccessToken = account.AccessToken;
        cfg.MsRefreshToken = account.RefreshToken;
        cfg.MsUuid = account.Uuid;
        cfg.MsName = account.Name;
        cfg.MsExpiresAtMs = account.ExpiresAtMs;
        try { cfg.Save(); } catch { /* 写盘失败不影响启动 */ }
        log?.Invoke($"正版账户: {account.Name}");
    }

    /// <summary>完整登录 (device code), 成功后写配置并返回账户</summary>
    public static async Task<MsAccount> LoginAsync(
        LauncherConfig cfg, DeviceCodeInfo info, Action<string>? progress, CancellationToken ct)
    {
        var (msToken, refresh) = await MicrosoftAuth
            .WaitForMicrosoftTokenAsync(cfg.MicrosoftClientId, info, progress, ct)
            .ConfigureAwait(false);

        var account = await MicrosoftAuth
            .ExchangeForMinecraftAsync(msToken, refresh, progress, ct)
            .ConfigureAwait(false);

        McAccessToken = account.AccessToken;
        cfg.AuthMode = "microsoft";
        cfg.MsRefreshToken = account.RefreshToken;
        cfg.MsUuid = account.Uuid;
        cfg.MsName = account.Name;
        cfg.MsExpiresAtMs = account.ExpiresAtMs;
        try { cfg.Save(); } catch { }
        return account;
    }

    /// <summary>退出登录 (清掉凭证, 回离线模式)</summary>
    public static void Logout(LauncherConfig cfg)
    {
        McAccessToken = "";
        cfg.AuthMode = "offline";
        cfg.MsRefreshToken = "";
        cfg.MsUuid = "";
        cfg.MsName = "";
        cfg.MsExpiresAtMs = 0;
        try { cfg.Save(); } catch { }
    }
}
