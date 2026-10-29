using System.Net;
using System.Text;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>社区资源类型</summary>
public enum AddonType
{
    Mod = 0,
    Modpack = 1,
    ResourcePack = 2,
    Shader = 3,
    /// <summary>数据包（1.13+ 的 datapacks）</summary>
    DataPack = 4,
}

/// <summary>搜索结果里的一个项目</summary>
public sealed class RemoteProject
{
    public string Id { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public long Downloads { get; init; }
    /// <summary>来源: modrinth / curseforge</summary>
    public string Source { get; init; } = "";
    public List<string> Categories { get; init; } = new();
    public List<string> GameVersions { get; init; } = new();

    /// <summary>列表里只显示标题 (作者/下载数放到详情框)</summary>
    public string Display => Title;
}

/// <summary>一个可下载的文件 (某个版本)</summary>
public sealed class RemoteFile
{
    public string ProjectId { get; init; } = "";
    public string Url { get; init; } = "";
    public string FileName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public long Size { get; init; }
    public string? Sha1 { get; init; }
    public List<string> GameVersions { get; init; } = new();
    public List<string> Loaders { get; init; } = new();
    /// <summary>该文件依赖的项目 id (只含必选)</summary>
    public List<string> RequiredDependencies { get; init; } = new();
    /// <summary>该文件的可选依赖项目 id (展示用, 不会自动装)</summary>
    public List<string> OptionalDependencies { get; init; } = new();
    /// <summary>发布频道: release / beta / alpha (Modrinth version_type)</summary>
    public string VersionType { get; init; } = "";

    /// <summary>频道标签: [正式]/[测试]/[预览], 未知频道返回空</summary>
    public string ChannelTag => VersionType switch
    {
        "release" => Loc.T("dl.chRelease"),
        "beta" => Loc.T("dl.chBeta"),
        "alpha" => Loc.T("dl.chAlpha"),
        _ => "",
    };

    public string Display
    {
        get
        {
            // 只挑 MC 版本号 (CurseForge 的 gameVersions 里混着 "Fabric" 之类的加载器名)
            var vers = GameVersions.Where(v => v.Length > 0 && char.IsDigit(v[0])).TakeLast(3);
            var v = string.Join(",", vers);
            var ch = ChannelTag;
            return $"{DisplayName}{(ch.Length > 0 ? " [" + ch + "]" : "")}   [{v}]   {Size / 1024.0 / 1024.0:0.0} MB";
        }
    }
}

/// <summary>
/// Modrinth API 客户端 (https://docs.modrinth.com), 完全免 key。
/// </summary>
public static class ModrinthApi
{
    private const string Base = "https://api.modrinth.com/v2";
    private static readonly HttpClient Http = Net.CreateClient(TimeSpan.FromSeconds(30));

    public static string TypeFacet(AddonType type) => type switch
    {
        AddonType.Modpack => "modpack",
        AddonType.ResourcePack => "resourcepack",
        AddonType.Shader => "shader",
        AddonType.DataPack => "datapack",
        _ => "mod",
    };

    /// <summary>
    /// 搜索。gameVersion / loader 传 null 表示不过滤。
    /// tag 格式 "CurseForgeId/ModrinthId" (学 PCL2), 只用斜杠后的 Modrinth 分类 id; 空 = 不限分类。
    /// facets 是 JSON 数组的数组: [[ "project_type:mod" ], [ "versions:1.21.1" ]]
    /// </summary>
    public static async Task<List<RemoteProject>> SearchAsync(
        string query, AddonType type, string? gameVersion, string? loader, string? tag = null,
        string? sort = null, int offset = 0, int limit = 20, CancellationToken ct = default)
    {
        var facets = new List<string> { $"[\"project_type:{TypeFacet(type)}\"]" };
        if (!string.IsNullOrEmpty(gameVersion))
            facets.Add($"[\"versions:{gameVersion}\"]");
        if (!string.IsNullOrEmpty(loader) && type is AddonType.Mod or AddonType.Modpack)
            facets.Add($"[\"categories:{loader}\"]");
        // tag 取 Modrinth 那半 (斜杠后); 只有 "/" 结尾说明该分类只在 CF 有, Modrinth 不过滤
        var modrinthTag = tag?.Contains('/') == true ? tag[(tag.IndexOf('/') + 1)..] : tag;
        if (!string.IsNullOrEmpty(modrinthTag))
            facets.Add($"[\"categories:'{modrinthTag}'\"]");

        var url = $"{Base}/search?query={Uri.EscapeDataString(query)}"
                  + $"&limit={limit}&offset={offset}"
                  // sort 为空时按老规矩: 空关键词看下载量, 有关键词看相关度
                  + $"&index={sort ?? (string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance")}"
                  + $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";

        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Modrinth 搜索失败: " + Net.DescribeHttp(
                new HttpRequestException(json.Length > 200 ? json[..200] : json, null, resp.StatusCode), url));

        using var doc = JsonDocument.Parse(json);
        var result = new List<RemoteProject>();
        if (!doc.RootElement.TryGetProperty("hits", out var hits))
            return result;

        foreach (var h in hits.EnumerateArray())
        {
            result.Add(new RemoteProject
            {
                Id = GetStr(h, "project_id") ?? "",
                Slug = GetStr(h, "slug") ?? "",
                Title = GetStr(h, "title") ?? "",
                Description = GetStr(h, "description") ?? "",
                Author = GetStr(h, "author") ?? "",
                Downloads = GetLong(h, "downloads"),
                Source = "modrinth",
                Categories = GetStrArray(h, "categories"),
                GameVersions = GetStrArray(h, "versions"),
            });
        }
        return result;
    }

    /// <summary>取某个项目适用于指定版本/加载器的文件列表 (新的在前)</summary>
    public static async Task<List<RemoteFile>> GetFilesAsync(
        string projectId, string? gameVersion, string? loader, CancellationToken ct = default)
    {
        // include_changelog=false 必须带: 不带的话 Modrinth 会给**每个版本**返回完整更新日志,
        // 版本多的 mod (几百个版本) 响应能有几 MB, 解析+传输都卡 (HMCL 也这么做)
        var q = new List<string> { "include_changelog=false" };
        if (!string.IsNullOrEmpty(gameVersion))
            q.Add($"game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}");
        if (!string.IsNullOrEmpty(loader))
            q.Add($"loaders={Uri.EscapeDataString($"[\"{loader}\"]")}");

        var url = $"{Base}/project/{Uri.EscapeDataString(projectId)}/version";
        if (q.Count > 0)
            url += "?" + string.Join("&", q);

        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException("Modrinth 读取版本列表失败: " + DescribeSimple(resp, json));

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new List<RemoteFile>();

        var result = new List<RemoteFile>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            if (ParseVersion(v, projectId) is { } f)
                result.Add(f);
        }
        return result;
    }

    /// <summary>
    /// 把 Modrinth 的一个 version 对象解析成 <see cref="RemoteFile"/>。
    /// 一个 version 可能挂多个文件，取 primary（没有 primary 标记就取第一个）。
    /// </summary>
    private static RemoteFile? ParseVersion(JsonElement v, string projectId)
    {
        if (v.ValueKind != JsonValueKind.Object)
            return null;
        if (!v.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return null;
        var primary = files.EnumerateArray().FirstOrDefault(f =>
            f.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True);
        if (primary.ValueKind != JsonValueKind.Object)
            primary = files.EnumerateArray().FirstOrDefault();
        if (primary.ValueKind != JsonValueKind.Object)
            return null;

        var deps = new List<string>();
        var optDeps = new List<string>();
        if (v.TryGetProperty("dependencies", out var ds) && ds.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in ds.EnumerateArray())
            {
                // dependency_type: required / optional / incompatible / embedded
                var depType = GetStr(d, "dependency_type");
                if (depType != "required" && depType != "optional")
                    continue;
                var pid = GetStr(d, "project_id");
                if (string.IsNullOrEmpty(pid))
                    continue;
                if (depType == "required")
                    deps.Add(pid);
                else
                    optDeps.Add(pid);
            }
        }

        return new RemoteFile
        {
            ProjectId = projectId,
            Url = GetStr(primary, "url") ?? "",
            FileName = GetStr(primary, "filename") ?? "",
            DisplayName = GetStr(v, "name") ?? GetStr(v, "version_number") ?? "",
            Size = GetLong(primary, "size"),
            Sha1 = primary.TryGetProperty("hashes", out var hh) ? GetStr(hh, "sha1") : null,
            GameVersions = GetStrArray(v, "game_versions"),
            Loaders = GetStrArray(v, "loaders"),
            RequiredDependencies = deps,
            OptionalDependencies = optDeps,
            VersionType = GetStr(v, "version_type") ?? "",
        };
    }

    /// <summary>
    /// 批量查「每个已装文件的最新版本」, Mod 更新检查用的就是它。
    /// 接口是 <c>POST /v2/version_files/update</c>：塞一组 SHA1 + 加载器 + 游戏版本，
    /// 返回 **hash → 最新的匹配版本**（已经在最新的会原样返回自己，不在 Modrinth 上的不返回）。
    /// 返回的字典 key 是传入的 hash（小写）。
    /// </summary>
    public static async Task<Dictionary<string, RemoteFile>> GetLatestVersionsAsync(
        IReadOnlyCollection<string> sha1s, string? gameVersion, string? loader, CancellationToken ct = default)
    {
        var result = new Dictionary<string, RemoteFile>(StringComparer.OrdinalIgnoreCase);
        if (sha1s.Count == 0)
            return result;

        // Modrinth 对单次条数有上限, 200 一批稳一点 (整合包里几百个 mod 也不用改代码)
        const int batchSize = 200;
        var list = sha1s.ToList();
        for (var start = 0; start < list.Count; start += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = list.Skip(start).Take(batchSize).ToList();

            var sb = new StringBuilder();
            sb.Append("{\"hashes\":[");
            for (var i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonText.Str(batch[i]));
            }
            sb.Append("],\"algorithm\":\"sha1\"");
            if (!string.IsNullOrEmpty(loader))
                sb.Append(",\"loaders\":[").Append(JsonText.Str(loader)).Append(']');
            if (!string.IsNullOrEmpty(gameVersion))
                sb.Append(",\"game_versions\":[").Append(JsonText.Str(gameVersion)).Append(']');
            sb.Append('}');

            using var content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync($"{Base}/version_files/update", content, ct)
                .ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException("Modrinth 查询更新失败: " + DescribeSimple(resp, json));

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // 响应的 key 就是请求里的 hash
                if (ParseVersion(prop.Value, GetStr(prop.Value, "project_id") ?? "") is { } f)
                    result[prop.Name] = f;
            }
        }
        return result;
    }

    /// <summary>
    /// 批量按项目 id 反查标题（<c>GET /projects?ids=[...]</c>）。
    /// 比一个个 <see cref="GetProjectAsync"/> 快得多, 依赖多的时候差别很明显。
    /// </summary>
    public static async Task<Dictionary<string, string>> GetProjectTitlesAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
            return result;

        var wanted = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
        if (wanted.Count == 0)
            return result;

        var arr = "[" + string.Join(",", wanted.Select(JsonText.Str)) + "]";
        var url = $"{Base}/projects?ids={Uri.EscapeDataString(arr)}";
        try
        {
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return result;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                var id = GetStr(p, "id");
                var title = GetStr(p, "title");
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(title))
                    result[id] = title;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 反查失败不阻塞主流程, 调用方会退回显示 id
        }
        return result;
    }

    /// <summary>取项目详情 (用来拿名字, 依赖安装时显示)</summary>
    public static async Task<(string Title, string Slug)> GetProjectAsync(string id, CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"{Base}/project/{Uri.EscapeDataString(id)}", ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (id, id);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return (GetStr(doc.RootElement, "title") ?? id, GetStr(doc.RootElement, "slug") ?? id);
        }
        catch
        {
            return (id, id);
        }
    }

    private static string DescribeSimple(HttpResponseMessage resp, string body) =>
        $"HTTP {(int)resp.StatusCode}" + (body.Length > 0 ? ": " + (body.Length > 160 ? body[..160] : body) : "");

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
