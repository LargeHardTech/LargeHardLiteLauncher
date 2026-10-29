using System.Text.Json;
using System.Text.Json.Serialization;

namespace LargeHardLiteLauncher.Core;

/// <summary>版本类型 (对齐 Mojang manifest 的字符串值)</summary>
public enum ReleaseType
{
    Release,
    Snapshot,
    OldBeta,
    OldAlpha,
    Unknown,
}

/// <summary>Mojang version_manifest.json 的单条版本</summary>
public sealed class VersionEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>Mojang 原始字符串: release | snapshot | old_beta | old_alpha</summary>
    [JsonPropertyName("type")] public string TypeRaw { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("time")] public DateTime Time { get; set; }
    [JsonPropertyName("releaseTime")] public DateTime ReleaseTime { get; set; }

    public ReleaseType Type => TypeRaw switch
    {
        "release" => ReleaseType.Release,
        "snapshot" => ReleaseType.Snapshot,
        "old_beta" => ReleaseType.OldBeta,
        "old_alpha" => ReleaseType.OldAlpha,
        _ => ReleaseType.Unknown,
    };

    public string TypeName => Type switch
    {
        ReleaseType.Release => "正式版",
        ReleaseType.Snapshot => "快照",
        ReleaseType.OldBeta => "远古 Beta",
        ReleaseType.OldAlpha => "远古 Alpha",
        _ => "未知",
    };

    /// <summary>列表显示: 1.21.4  [正式版]  2024-12-03</summary>
    public string Display => $"{Id}  [{TypeName}]  {ReleaseTime:yyyy-MM-dd}";
}

/// <summary>Mojang version_manifest.json 根</summary>
public sealed class VersionManifest
{
    [JsonPropertyName("latest")] public LatestVersions Latest { get; set; } = new();
    [JsonPropertyName("versions")] public List<VersionEntry> Versions { get; set; } = new();
}

public sealed class LatestVersions
{
    [JsonPropertyName("release")] public string Release { get; set; } = "";
    [JsonPropertyName("snapshot")] public string Snapshot { get; set; } = "";
}

/// <summary>
/// Mojang 版本列表获取。结构对齐 HMCL 的 GameRemoteVersions。
/// auto 模式: 官方源失败自动切 BMCLAPI 镜像 (HMCL 的 DownloadCandidates 思路)。
/// </summary>
public static class MojangApi
{
    private const string OfficialManifest = "https://piston-meta.mojang.com/mc/game/version_manifest.json";
    private const string BmclManifest = "https://bmclapi2.bangbang93.com/mc/game/version_manifest.json";

    private static string CachePath => Path.Combine(LauncherConfig.RootDir, "version_manifest.json");

    /// <summary>读取本地缓存的 manifest (无则 null)</summary>
    public static VersionManifest? LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
                return JsonSerializer.Deserialize(File.ReadAllText(CachePath), MojangJsonContext.Default.VersionManifest);
        }
        catch { /* 缓存损坏则忽略 */ }
        return null;
    }

    /// <summary>
    /// 联网拉取 manifest。source: auto | official | bmclapi。
    /// auto: 先官方, 失败切 BMCLAPI。
    /// </summary>
    public static async Task<VersionManifest> FetchAsync(
        string source, bool ignoreSslErrors = false, CancellationToken ct = default)
    {
        using var http = Net.CreateClient(TimeSpan.FromSeconds(30), ignoreSslErrors);

        if (source == "bmclapi")
            return await FetchFrom(http, BmclManifest, ct);

        try
        {
            return await FetchFrom(http, OfficialManifest, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception first) when (source == "auto")
        {
            // 官方源失败, 切 BMCLAPI; 两个都失败时把两边的真实原因都报出来
            try
            {
                return await FetchFrom(http, BmclManifest, ct);
            }
            catch (Exception second)
            {
                var msg = "版本列表获取失败(官方源和镜像都试过了)\n"
                          + "官方: " + Net.Describe(first, includeHint: false) + "\n"
                          + "镜像: " + Net.Describe(second, includeHint: false);
                if (Net.Hint(second) is { } hint)
                    msg += "\n\n" + hint;
                throw new HttpRequestException(msg, second);
            }
        }
    }

    private static async Task<VersionManifest> FetchFrom(HttpClient http, string url, CancellationToken ct)
    {
        var json = await http.GetStringAsync(url, ct);
        var manifest = JsonSerializer.Deserialize(json, MojangJsonContext.Default.VersionManifest)
                       ?? throw new InvalidDataException("manifest 解析失败");
        if (manifest.Versions.Count == 0)
            throw new InvalidDataException("manifest 版本列表为空");
        SaveCache(manifest);
        return manifest;
    }

    /// <summary>写缓存 (失败静默, 不影响主流程)</summary>
    public static void SaveCache(VersionManifest manifest)
    {
        try
        {
            Directory.CreateDirectory(LauncherConfig.RootDir);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(manifest, MojangJsonContext.Default.VersionManifest));
        }
        catch { /* 缓存写失败无所谓 */ }
    }

    /// <summary>按类型筛选。用索引不用显示文案, 文案会随语言变，判断就失效了。</summary>
    public static List<VersionEntry> Filter(List<VersionEntry> all, int filterIndex) => filterIndex switch
    {
        1 => all.FindAll(v => v.Type == ReleaseType.Release),
        2 => all.FindAll(v => v.Type == ReleaseType.Snapshot),
        3 => all.FindAll(v => v.Type is ReleaseType.OldBeta or ReleaseType.OldAlpha),
        _ => all,
    };
}

/// <summary>AOT 安全的 JSON 上下文 (manifest 部分)</summary>
[JsonSerializable(typeof(VersionManifest))]
[JsonSerializable(typeof(VersionEntry))]
internal partial class MojangJsonContext : JsonSerializerContext;
