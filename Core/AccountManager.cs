namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 一个保存下来的账户（学 PCL 的多账户）。
/// 离线账户只存名字；微软账户存 refresh_token（长期有效），切过来时自动换新 access_token。
/// </summary>
public sealed class AccountEntry
{
    public string Id { get; set; } = "";
    /// <summary>offline | microsoft</summary>
    public string Kind { get; set; } = "offline";
    /// <summary>列表里显示的名字</summary>
    public string DisplayName { get; set; } = "";

    // ---- 离线 ----
    public string PlayerName { get; set; } = "";

    // ---- 微软 ----
    public string MicrosoftClientId { get; set; } = "";
    public string MsRefreshToken { get; set; } = "";
    public string MsUuid { get; set; } = "";
    public string MsName { get; set; } = "";
    public long MsExpiresAtMs { get; set; }

    public bool IsMicrosoft => Kind == "microsoft";

    public string KindName => IsMicrosoft ? "正版" : "离线";
}

/// <summary>
/// 多账户的保存 / 切换。
///
/// 存储策略：`Config.Accounts` 是账户列表，`Config.CurrentAccountId` 指向当前那个。
/// 当前生效的账户信息仍然放在 Config 原有的字段（PlayerName / AuthMode / MsXxx）里,
/// 这样启动链路、AuthState 全都不用改，只是多了一层"快照"。
/// </summary>
public static class AccountManager
{
    /// <summary>
    /// 把当前 Config 的账户状态刷进账户列表（打开账户页时调一次就够）。
    /// 当前账户不存在就新建一条，存在就原地更新。
    /// </summary>
    public static AccountEntry Sync(LauncherConfig cfg)
    {
        var entry = string.IsNullOrEmpty(cfg.CurrentAccountId)
            ? null
            : cfg.Accounts.FirstOrDefault(a => a.Id == cfg.CurrentAccountId);

        if (entry is null)
        {
            // 老配置里没有账户列表：把现有的登录状态升级成第一条账户
            entry = new AccountEntry { Id = NewId() };
            cfg.Accounts.Add(entry);
            cfg.CurrentAccountId = entry.Id;
        }

        entry.Kind = cfg.AuthMode == "microsoft" ? "microsoft" : "offline";
        entry.PlayerName = cfg.PlayerName;
        entry.MicrosoftClientId = cfg.MicrosoftClientId;
        entry.MsRefreshToken = cfg.MsRefreshToken;
        entry.MsUuid = cfg.MsUuid;
        entry.MsName = cfg.MsName;
        entry.MsExpiresAtMs = cfg.MsExpiresAtMs;
        entry.DisplayName = entry.IsMicrosoft
            ? (string.IsNullOrEmpty(entry.MsName) ? entry.PlayerName : entry.MsName)
            : entry.PlayerName;

        return entry;
    }

    /// <summary>切到某个账户：把条目的字段写回 Config 的当前账户字段</summary>
    public static void Apply(LauncherConfig cfg, AccountEntry entry)
    {
        cfg.CurrentAccountId = entry.Id;
        cfg.AuthMode = entry.IsMicrosoft ? "microsoft" : "offline";
        cfg.PlayerName = entry.PlayerName;
        cfg.MicrosoftClientId = entry.MicrosoftClientId;
        cfg.MsRefreshToken = entry.MsRefreshToken;
        cfg.MsUuid = entry.MsUuid;
        cfg.MsName = entry.MsName;
        cfg.MsExpiresAtMs = entry.MsExpiresAtMs;
        LauncherLog.Info($"切换账户: {entry.DisplayName} ({entry.KindName})");
    }

    /// <summary>新增一个离线账户（同时切过去）</summary>
    public static AccountEntry AddOffline(LauncherConfig cfg, string playerName)
    {
        var entry = new AccountEntry
        {
            Id = NewId(),
            Kind = "offline",
            PlayerName = playerName,
            DisplayName = playerName,
        };
        cfg.Accounts.Add(entry);
        Apply(cfg, entry);
        return entry;
    }

    /// <summary>删除账户。删的是当前账户时，切到剩下的第一个；一个不剩就给个默认离线名</summary>
    public static void Remove(LauncherConfig cfg, AccountEntry entry)
    {
        cfg.Accounts.RemoveAll(a => a.Id == entry.Id);
        if (cfg.CurrentAccountId != entry.Id)
            return;

        cfg.CurrentAccountId = "";
        var next = cfg.Accounts.FirstOrDefault();
        if (next is not null)
        {
            Apply(cfg, next);
            return;
        }

        // 一个账户都没有了：退回默认离线状态
        cfg.AuthMode = "offline";
        cfg.PlayerName = "Steve";
        cfg.MsRefreshToken = "";
        cfg.MsUuid = "";
        cfg.MsName = "";
        cfg.MsExpiresAtMs = 0;
    }

    /// <summary>列表里名字重复时自动加序号（免得两个 Peter 分不清）</summary>
    public static string UniqueName(LauncherConfig cfg, string baseName)
    {
        if (cfg.Accounts.All(a => !string.Equals(a.DisplayName, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;
        for (var i = 2; i < 100; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (cfg.Accounts.All(a => !string.Equals(a.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return baseName + " (?)";
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];
}
