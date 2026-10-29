using System.Net;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// CurseForge API 客户端。
///
/// 和 Modrinth 不同, CurseForge 的 API **必须带 key**
///   (https://console.curseforge.com 免费申请, 无 key 直接 403)。
///   所以这个源默认不可用, 用户在设置里填了 key 才会被调用。
/// </summary>
public static class CurseForgeApi
{
    private const string Base = "https://api.curseforge.com/v1";
    /// <summary>Minecraft 的 gameId 固定是 432</summary>
    private const int GameId = 432;

    private static readonly HttpClient Http = Net.CreateClient(TimeSpan.FromSeconds(30));

    /// <summary>运行时 key (由配置注入, 每次搜索前设一次即可)</summary>
    public static string ApiKey { get; set; } = "";

    public static bool Available => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>classId 映射 (CurseForge 的分类 id)</summary>
    private static int ClassId(AddonType type) => type switch
    {
        AddonType.Modpack => 4471,
        AddonType.ResourcePack => 12,
        AddonType.Shader => 6552,
        AddonType.DataPack => 6945, // CurseForge 的 "Data Packs"
        _ => 6,
    };

    /// <summary>modLoaderType: 1=Forge 4=Fabric 5=Quilt 6=NeoForge</summary>
    private static int? LoaderType(string? loader) => loader?.ToLowerInvariant() switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => null,
    };

    public static async Task<List<RemoteProject>> SearchAsync(
        string query, AddonType type, string? gameVersion, string? loader, string? tag = null,
        string? sort = null, string apiKey = "", int offset = 0, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("CurseForge 需要 API key (在设置 → 下载 里填)");

        // sort 映射到 CF sortField (与 PCL2 一致): 相关度=4 下载量=6 关注量=2 最新发布=11 最近更新=3
        var sortField = sort switch
        {
            "relevance" => 4,
            "downloads" => 6,
            "follows" => 2,
            "newest" => 11,
            "updated" => 3,
            _ => 2,
        };

        var q = new List<string>
        {
            $"gameId={GameId}",
            $"classId={ClassId(type)}",
            $"index={offset}",
            $"pageSize={limit}",
            $"sortField={sortField}",
            $"sortOrder=desc",
        };
        // tag 格式 "CurseForgeId/ModrinthId": 取斜杠前的 CF 分类 id; "/xxx" 开头说明只在 Modrinth 有, CF 不过滤
        var cfTag = tag?.Contains('/') == true ? tag[..tag.IndexOf('/')] : tag;
        if (!string.IsNullOrEmpty(cfTag))
            q.Add($"categoryId={cfTag}");
        if (!string.IsNullOrWhiteSpace(query))
            q.Add($"searchFilter={Uri.EscapeDataString(query)}");
        if (!string.IsNullOrEmpty(gameVersion))
            q.Add($"gameVersion={Uri.EscapeDataString(gameVersion)}");
        if (LoaderType(loader) is { } lt && type is AddonType.Mod or AddonType.Modpack)
            q.Add($"modLoaderType={lt}");

        var url = $"{Base}/mods/search?" + string.Join("&", q);
        using var resp = await SendAsync(url, apiKey, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException("CurseForge 搜索失败: " + Describe(resp, json));

        using var doc = JsonDocument.Parse(json);
        var result = new List<RemoteProject>();
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var m in data.EnumerateArray())
            result.Add(ParseProject(m));

        return result;
    }

    /// <summary>取项目适用文件 (新的在前)</summary>
    public static async Task<List<RemoteFile>> GetFilesAsync(
        string modId, string? gameVersion, string? loader,
        string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("CurseForge 需要 API key");

        var q = new List<string> { $"pageSize=50" };
        if (!string.IsNullOrEmpty(gameVersion))
            q.Add($"gameVersion={Uri.EscapeDataString(gameVersion)}");
        if (LoaderType(loader) is { } lt)
            q.Add($"modLoaderType={lt}");

        var url = $"{Base}/mods/{Uri.EscapeDataString(modId)}/files?" + string.Join("&", q);
        using var resp = await SendAsync(url, apiKey, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException("CurseForge 读取文件列表失败: " + Describe(resp, json));

        using var doc = JsonDocument.Parse(json);
        var result = new List<RemoteFile>();
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var f in data.EnumerateArray())
        {
            var fileName = GetStr(f, "fileName") ?? "";
            var fileId = GetLong(f, "id");

            // downloadUrl 有时是 null (作者禁止第三方直链), 按 CurseForge CDN 规则拼
            var url2 = GetStr(f, "downloadUrl") ?? CdnUrl(fileId, fileName);

            var deps = new List<string>();
            var optDeps = new List<string>();
            if (f.TryGetProperty("dependencies", out var ds) && ds.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in ds.EnumerateArray())
                {
                    // relationType 3 = RequiredDependency; 2/4 = Optional/Tool (展示用, 不自动装)
                    var rel = GetLong(d, "relationType");
                    if (rel != 3 && rel != 2 && rel != 4)
                        continue;
                    var mid = GetLong(d, "modId");
                    if (mid <= 0)
                        continue;
                    if (rel == 3)
                        deps.Add(mid.ToString());
                    else
                        optDeps.Add(mid.ToString());
                }
            }

            result.Add(new RemoteFile
            {
                ProjectId = modId,
                Url = url2,
                FileName = fileName,
                DisplayName = GetStr(f, "displayName") ?? fileName,
                Size = GetLong(f, "fileLength"),
                GameVersions = GetStrArray(f, "gameVersions"),
                Loaders = new List<string>(),
                RequiredDependencies = deps,
                OptionalDependencies = optDeps,
                // CF 的 releaseType: 1=Release 2=Beta 3=Alpha
                VersionType = GetLong(f, "releaseType") switch
                {
                    1 => "release",
                    2 => "beta",
                    3 => "alpha",
                    _ => "",
                },
            });
        }
        return result;
    }

    /// <summary>CurseForge CDN 直链规则: files/{id/1000}/{id%1000}/{fileName}</summary>
    private static string CdnUrl(long fileId, string fileName) =>
        fileId <= 0 || string.IsNullOrEmpty(fileName)
            ? ""
            : $"https://edge.forgecdn.net/files/{fileId / 1000}/{fileId % 1000}/{Uri.EscapeDataString(fileName)}";

    private static RemoteProject ParseProject(JsonElement m)
    {
        var authors = new List<string>();
        if (m.TryGetProperty("authors", out var au) && au.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in au.EnumerateArray())
            {
                var n = GetStr(a, "name");
                if (!string.IsNullOrEmpty(n))
                    authors.Add(n);
            }
        }

        var cats = new List<string>();
        if (m.TryGetProperty("categories", out var cs) && cs.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cs.EnumerateArray())
            {
                var n = GetStr(c, "name");
                if (!string.IsNullOrEmpty(n))
                    cats.Add(n);
            }
        }

        var versions = new List<string>();
        if (m.TryGetProperty("latestFilesIndexes", out var idx) && idx.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in idx.EnumerateArray())
            {
                var gv = GetStr(i, "gameVersion");
                if (!string.IsNullOrEmpty(gv) && !versions.Contains(gv))
                    versions.Add(gv);
            }
        }

        return new RemoteProject
        {
            Id = GetLong(m, "id").ToString(),
            Slug = GetStr(m, "slug") ?? "",
            Title = GetStr(m, "name") ?? "",
            Description = GetStr(m, "summary") ?? "",
            Author = string.Join(", ", authors),
            Downloads = GetLong(m, "downloadCount"),
            Source = "curseforge",
            Categories = cats,
            GameVersions = versions,
        };
    }

    private static async Task<HttpResponseMessage> SendAsync(string url, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("Accept", "application/json");
        return await Http.SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量按 modId 反查项目名（<c>POST /v1/mods</c> 带 modIds 数组）。
    /// 依赖 id 是纯数字，光看 id 用户不知道是什么，详情页得把名字显出来。
    /// </summary>
    public static async Task<Dictionary<string, string>> GetProjectTitlesAsync(
        IReadOnlyCollection<string> modIds, string apiKey, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(apiKey) || modIds.Count == 0)
            return result;

        // CF 只认数字 id, 混进来的脏数据先剔掉
        var ids = modIds
            .Select(s => long.TryParse(s, out var v) ? v : 0)
            .Where(v => v > 0)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return result;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/mods")
            {
                Content = new StringContent(
                    "{\"modIds\":[" + string.Join(",", ids) + "]}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("Accept", "application/json");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return result;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var m in data.EnumerateArray())
            {
                var id = GetLong(m, "id");
                var name = GetStr(m, "name");
                if (id > 0 && !string.IsNullOrEmpty(name))
                    result[id.ToString()] = name;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 反查失败不阻塞详情页, 调用方会退回显示 id
        }
        return result;
    }

    /// <summary>取单个项目名 + slug（依赖列表点进去要用）</summary>
    public static async Task<(string Title, string Slug)> GetProjectAsync(
        string modId, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (modId, modId);
        try
        {
            using var resp = await SendAsync($"{Base}/mods/{Uri.EscapeDataString(modId)}", apiKey, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (modId, modId);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return (modId, modId);
            return (GetStr(data, "name") ?? modId, GetStr(data, "slug") ?? modId);
        }
        catch
        {
            return (modId, modId);
        }
    }

    private static string Describe(HttpResponseMessage resp, string body)
    {
        if (resp.StatusCode is HttpStatusCode.Forbidden)
            return "API key 无效或已过期(去 console.curseforge.com 重新申请)";
        return $"HTTP {(int)resp.StatusCode}" + (body.Length > 0 ? ": " + (body.Length > 160 ? body[..160] : body) : "");
    }

    private static string? GetStr(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.TryGetInt64(out var i) ? i : 0;

    private static List<string> GetStrArray(JsonElement e, string prop)
    {
        var list = new List<string>();
        if (e.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    list.Add(item.GetString() ?? "");
        }
        return list;
    }
}
