using System.Text.RegularExpressions;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 版本目录名与基础 MC 版本的换算。目录名由用户或安装器随意指定（1.20.1-forge-test、
/// 我的整合包、fabric-loader-0.20.1-1.20.1），而 Modrinth / CurseForge 只认标准版本号，
/// 所以搜索和安装前必须先换算。顺序：inheritsFrom 链上本地存在的原版 → 链尾本身像版本号
/// → 正则抽取（优先取本地已有的原版号）→ 原样返回。
/// </summary>
public static partial class VersionHelper
{
    /// <summary>标准 MC 版本号: 1.20 / 1.20.1 / 1.21.10</summary>
    [GeneratedRegex(@"^\d+\.\d+(?:\.\d+)?$")]
    private static partial Regex McVersionRegex();

    /// <summary>快照版本号: 24w14a / 1.20.5-pre1 之类带后缀的老快照</summary>
    [GeneratedRegex(@"^\d{2}w\d{2}[a-z]$", RegexOptions.IgnoreCase)]
    private static partial Regex SnapshotRegex();

    /// <summary>从任意字符串里捞版本号候选</summary>
    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?")]
    private static partial Regex AnyVersionRegex();

    /// <summary>长得像不像一个 MC 版本号（release 或快照）</summary>
    public static bool LooksLikeMcVersion(string s) =>
        !string.IsNullOrWhiteSpace(s) &&
        (McVersionRegex().IsMatch(s) || SnapshotRegex().IsMatch(s));

    /// <summary>
    /// 取实例的基础 MC 版本。`all` 传 VersionScanner.Scan 的结果, 用来判断"本地装没装这个原版"。
    /// </summary>
    public static string ResolveBaseVersion(MinecraftVersion v, IReadOnlyList<MinecraftVersion>? all = null)
    {
        all ??= Array.Empty<MinecraftVersion>();

        // 本地存着的原版版本 id（无 inheritsFrom 且名字就是版本号）, 判定时的"真值表"
        var vanilla = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in all)
            if (x.InheritsFrom is null && LooksLikeMcVersion(x.Id))
                vanilla.Add(x.Id);

        // 候选链: 自己 → 父 → 祖父 …（断链时也把 inheritsFrom 字符串本身收进来）
        var chain = new List<string> { v.Id };
        var cur = v;
        var guard = 0;
        while (guard++ < 16 && !string.IsNullOrEmpty(cur.InheritsFrom))
        {
            chain.Add(cur.InheritsFrom!);
            var parent = all.FirstOrDefault(x => string.Equals(x.Id, cur.InheritsFrom, StringComparison.OrdinalIgnoreCase));
            if (parent is null)
                break; // 父版本没装/被删: 字符串已入链，够用了
            cur = parent;
        }

        // 1) 链上命中了本地原版 → 最权威
        foreach (var c in chain)
            if (vanilla.Contains(c))
                return c;

        // 2) 从链尾往前找"长得像版本号"的（链尾是原版）
        for (var i = chain.Count - 1; i >= 0; i--)
            if (LooksLikeMcVersion(chain[i]))
                return chain[i];

        // 3) 正则抽。链尾优先，抽到多个取最后一个（`fabric-loader-0.20.1-1.20.1` 的末位才是 MC 版本）
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var ms = AnyVersionRegex().Matches(chain[i]);
            if (ms.Count == 0)
                continue;
            // 先看有没有命中本地原版
            for (var j = ms.Count - 1; j >= 0; j--)
                if (vanilla.Contains(ms[j].Value))
                    return ms[j].Value;
            return ms[^1].Value;
        }

        // 4) 认不出来，原样返回
        return v.Id;
    }

    /// <summary>便利重载: 从版本 id 直接算（拿不到完整信息时用）</summary>
    public static string ResolveBaseVersion(string versionId, IReadOnlyList<MinecraftVersion>? all = null)
    {
        all ??= Array.Empty<MinecraftVersion>();
        var v = all.FirstOrDefault(x => string.Equals(x.Id, versionId, StringComparison.OrdinalIgnoreCase));
        if (v is not null)
            return ResolveBaseVersion(v, all);

        // 没扫到这个版本: 只能靠字符串推断
        if (LooksLikeMcVersion(versionId))
            return versionId;
        var ms = AnyVersionRegex().Matches(versionId);
        if (ms.Count == 0)
            return versionId;
        var vanilla = all.Where(x => x.InheritsFrom is null && LooksLikeMcVersion(x.Id))
                         .Select(x => x.Id)
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = ms.Count - 1; i >= 0; i--)
            if (vanilla.Contains(ms[i].Value))
                return ms[i].Value;
        return ms[^1].Value;
    }

    /// <summary>
    /// 版本号"降序"比较器（配 OrderBy 用；返回值 &gt; 0 表示 a 比 b 旧）。
    /// 按数字段比，不是字符串比, 字符串降序会把 1.9.4 排到 1.20.1 前面。
    /// </summary>
    public static int CompareVersionDesc(string a, string b)
    {
        static int CompareOne(string x, string y)
        {
            var pa = x.TrimStart('v', 'V').Split('.');
            var pb = y.TrimStart('v', 'V').Split('.');
            for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                var sa = i < pa.Length ? pa[i] : "0";
                var sb = i < pb.Length ? pb[i] : "0";
                // 非数字段 (0.20.0-beta.9 的 "0-beta") 按字典序
                if (int.TryParse(sa, out var na) && int.TryParse(sb, out var nb))
                {
                    if (na != nb)
                        return na.CompareTo(nb);
                }
                else
                {
                    var c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                    if (c != 0)
                        return c;
                }
            }
            return 0;
        }

        return CompareOne(b, a); // 倒序: 大的在前
    }
}
