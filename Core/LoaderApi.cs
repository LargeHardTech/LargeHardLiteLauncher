using System.Text.Json;
using System.Xml.Linq;

namespace LargeHardLiteLauncher.Core;

/// <summary>可安装的版本类型 (原版 + 各加载器)</summary>
public enum LoaderKind
{
    Vanilla = 0,
    Forge = 1,
    NeoForge = 2,
    Fabric = 3,
    Quilt = 4,
}

/// <summary>列表里的一条可安装版本</summary>
public sealed record InstallableVersion(string Version, string Note, string? Sha1 = null);

/// <summary>
/// 各加载器的版本列表数据源 (BMCLAPI 镜像优先, 官方兜底)。
/// 借鉴 PCL2/HMCL: 列表只给"这个 MC 版本下能装哪些加载器版本", 安装另走 LoaderInstaller。
/// </summary>
public static class LoaderApi
{
    private static readonly HttpClient Http = Net.CreateClient(TimeSpan.FromSeconds(30));

    /// <summary>取某加载器在指定 MC 版本下的全部可安装版本 (新的在前)</summary>
    public static async Task<List<InstallableVersion>> ListAsync(
        LoaderKind kind, string mcVersion, CancellationToken ct = default)
    {
        return kind switch
        {
            LoaderKind.Forge => await ForgeAsync(mcVersion, ct).ConfigureAwait(false),
            LoaderKind.Fabric => await FabricAsync(mcVersion, ct).ConfigureAwait(false),
            LoaderKind.Quilt => await QuiltAsync(mcVersion, ct).ConfigureAwait(false),
            LoaderKind.NeoForge => await NeoForgeAsync(mcVersion, ct).ConfigureAwait(false),
            _ => new List<InstallableVersion>(),
        };
    }

    /// <summary>Forge: BMCLAPI /forge/minecraft/{mc} → [{version, modified, build}]</summary>
    private static async Task<List<InstallableVersion>> ForgeAsync(string mc, CancellationToken ct)
    {
        var url = $"https://bmclapi2.bangbang93.com/forge/minecraft/{Uri.EscapeDataString(mc)}";
        var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var list = new List<(string Ver, string Date)>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var ver = Str(e, "version");
                if (string.IsNullOrEmpty(ver))
                    continue;
                var date = Str(e, "modified") ?? "";
                if (date.Length >= 10)
                    date = date[..10];
                list.Add((ver, date));
            }
        }
        // BMCLAPI 按 build 升序返回, 改成新的在前
        // 注意 CompareVersionDesc 是"降序比较器", 配 OrderBy 用; 配 OrderByDescending 会双重反转
        return list.OrderBy(x => x.Ver, Comparer<string>.Create(CompareVersionDesc))
                   .Select(x => new InstallableVersion(x.Ver, x.Date))
                   .ToList();
    }

    /// <summary>Fabric: BMCLAPI fabric-meta /v2/versions/loader/{mc} → [{loader:{version, stable}}]</summary>
    private static async Task<List<InstallableVersion>> FabricAsync(string mc, CancellationToken ct)
    {
        var json = await GetWithFallbackAsync(
            $"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{Uri.EscapeDataString(mc)}",
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mc)}", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var result = new List<InstallableVersion>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("loader", out var loader))
                continue;
            var ver = Str(loader, "version");
            if (string.IsNullOrEmpty(ver))
                continue;
            var stable = loader.TryGetProperty("stable", out var st) && st.ValueKind == JsonValueKind.True;
            result.Add(new InstallableVersion(ver, stable ? "稳定" : ""));
        }
        return result.OrderBy(x => x.Version, Comparer<string>.Create(CompareVersionDesc)).ToList();
    }

    /// <summary>Quilt: meta /v3/versions/loader/{mc} → [{loader:{version}}]</summary>
    private static async Task<List<InstallableVersion>> QuiltAsync(string mc, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(
            $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mc)}", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var result = new List<InstallableVersion>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("loader", out var loader))
                continue;
            var ver = Str(loader, "version");
            if (!string.IsNullOrEmpty(ver))
                result.Add(new InstallableVersion(ver, ver.Contains('-') ? "" : "稳定"));
        }
        return result.OrderBy(x => x.Version, Comparer<string>.Create(CompareVersionDesc)).ToList();
    }

    /// <summary>
    /// NeoForge: 官方 maven metadata (XML) → 过滤出对应 MC 的版本。
    /// NeoForge 版本号形如 20.4.237 (MC 1.20.4) / 21.1.77 (MC 1.21.1), 用 MC 去掉前导 "1." 做前缀。
    /// </summary>
    private static async Task<List<InstallableVersion>> NeoForgeAsync(string mc, CancellationToken ct)
    {
        // 1.20.1 及更早没有 NeoForge (那时还是 Forge 分支), 直接说清楚
        // 注意 CompareVersionDesc(a,b) > 0 表示 a 比 b 旧 (它是降序比较器)
        var prefix = mc.StartsWith("1.", StringComparison.Ordinal) ? mc[2..] : mc;
        if (CompareVersionDesc(prefix, "20.2") > 0)
            throw new InvalidOperationException(
                $"NeoForge 从 MC 1.20.2 才开始,{mc} 请选 Forge");

        var xml = await Http.GetStringAsync(
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", ct)
            .ConfigureAwait(false);
        var doc = XDocument.Parse(xml);
        var result = new List<InstallableVersion>();
        foreach (var el in doc.Descendants("version"))
        {
            var ver = el.Value.Trim();
            // 只要前缀匹配的: 20.4.x / 21.1.x
            if (ver.StartsWith(prefix + ".", StringComparison.Ordinal) && !ver.Contains('-'))
                result.Add(new InstallableVersion(ver, ""));
        }
        return result.OrderBy(x => x.Version, Comparer<string>.Create(CompareVersionDesc)).ToList();
    }

    private static async Task<string> GetWithFallbackAsync(string primary, string fallback, CancellationToken ct)
    {
        try
        {
            return await Http.GetStringAsync(primary, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LauncherLog.Warn($"镜像源失败,换官方源: {ex.Message}");
            return await Http.GetStringAsync(fallback, ct).ConfigureAwait(false);
        }
    }

    /// <summary>版本号倒序比较 (按数字段, 忽略前导 v); 只用于列表排序。实现统一在 VersionHelper</summary>
    private static int CompareVersionDesc(string a, string b) => VersionHelper.CompareVersionDesc(a, b);

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
