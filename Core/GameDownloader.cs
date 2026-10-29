using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>待下载的单个文件</summary>
public sealed class DownloadFile
{
    public required string Url { get; init; }
    /// <summary>另一个下载源的地址 (主源 SSL/连接失败时自动换)</summary>
    public string? FallbackUrl { get; init; }
    public required string Path { get; init; }
    public string? Sha1 { get; init; }
    public long Size { get; init; }
}

/// <summary>
/// 原版游戏安装器。思路参考 HMCL:
/// 拉版本 json → 生成完整文件清单 (client jar / libraries / assets) → 先校验本地已有文件
/// → 并发下载缺失部分 → 每个文件 SHA1 校验 → 失败重试。
/// </summary>
public static class GameDownloader
{
    private const string Bmcl = "https://bmclapi2.bangbang93.com";
    private const int Threads = 32;  // 并发连接数 (HMCL 默认 64, 32 兼顾速度与镜像压力)
    private const int Retries = 3;   // 单文件重试次数

    // 走 Net.CreateClient: 显式 TLS 1.2/1.3 + 证书错误诊断
    private static HttpClient? _http;
    private static bool _ignoreSsl;

    /// <summary>按设置重建连接 (只有"忽略证书错误"开关变化时才重建)</summary>
    public static void Configure(bool ignoreSslErrors)
    {
        if (_http is not null && _ignoreSsl == ignoreSslErrors)
            return;
        _ignoreSsl = ignoreSslErrors;
        _http = Net.CreateClient(TimeSpan.FromMinutes(5), ignoreSslErrors);
    }

    private static HttpClient Http => _http ??= Net.CreateClient(TimeSpan.FromMinutes(5));

    /// <summary>下载源尝试顺序: 选定的优先, 失败自动退另一个</summary>
    private static string[] SourceOrder(string source) => source == "official"
        ? new[] { "official", Bmcl }
        : new[] { Bmcl, "official" };

    private static string OtherSource(string source) => source == "official" ? Bmcl : "official";

    private static string SourceName(string source) => source == "official" ? "官方源" : "BMCLAPI 镜像";

    /// <summary>
    /// 双源拉文本: 主源失败自动换另一个。
    /// 单源时代最坑的就是"版本列表能刷出来(走官方),一装就 SSL 失败(走镜像)"，换源即可绕过。
    /// </summary>
    private static async Task<(string Text, string Source)> FetchTextAsync(
        string url, string source, TaskItem? task, string what, CancellationToken ct)
    {
        var order = SourceOrder(source);
        var errors = new List<string>();
        Exception? last = null;

        foreach (var src in order)
        {
            ct.ThrowIfCancellationRequested();
            var target = Mirror(url, src);

            // 502/503/504 是网关临时故障, 同一个源重试一次经常就过了
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var text = await Http.GetStringAsync(target, ct);
                    if (src != order[0])
                        task?.LogLine($"[{what}] 主源不通,已切换到{SourceName(src)}");
                    return (text, src);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    var transient = (ex as HttpRequestException)?.StatusCode is HttpStatusCode sc
                                    && (int)sc >= 500;
                    if (transient && attempt == 1)
                    {
                        task?.LogLine($"[{what}] {SourceName(src)} 网关错误,1 秒后重试...");
                        await Task.Delay(1000, ct);
                        continue;
                    }

                    var detail = Net.DescribeHttp(ex, target);
                    task?.LogLine($"[{what}] {SourceName(src)} 失败: {detail}");
                    errors.Add($"{SourceName(src)}: {detail}");
                    break;
                }
            }
        }

        var msg = $"{what}获取失败(镜像源和官方源都试过了)\n" + string.Join("\n", errors);
        if (last is not null && Net.Hint(last) is { } hint)
            msg += "\n\n" + hint;
        throw new HttpRequestException(msg, last);
    }

    /// <summary>
    /// 生成安装计划: 版本 json 落盘 + 收集所有需要下载的文件。
    /// 仅支持原版 (含 inheritsFrom 的 Forge/Fabric 版本暂不处理)。
    /// </summary>
    public static async Task<List<DownloadFile>> BuildPlanAsync(
        VersionEntry entry, string mcDir, string source, TaskItem? task, CancellationToken ct)
    {
        var versionDir = Path.Combine(mcDir, "versions", entry.Id);
        Directory.CreateDirectory(versionDir);

        var (jsonText, usedSource) = await FetchTextAsync(entry.Url, source, task, "版本 json", ct);
        var jsonPath = Path.Combine(versionDir, entry.Id + ".json");
        await File.WriteAllTextAsync(jsonPath, jsonText, ct);

        // 后续所有文件都按成功的那个源生成, 同时带上另一个源做备用
        var altSource = OtherSource(usedSource);
        string Primary(string u) => Mirror(u, usedSource);
        string? Fallback(string u) => Mirror(u, altSource);

        var files = new List<DownloadFile>();
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        if (root.TryGetProperty("inheritsFrom", out _))
            throw new InvalidOperationException("暂不支持带 inheritsFrom 的版本 (Forge/Fabric),请只安装原版");

        // 1) 客户端 jar
        if (root.TryGetProperty("downloads", out var dl) && dl.TryGetProperty("client", out var client))
        {
            var url = GetStr(client, "url");
            if (!string.IsNullOrEmpty(url))
            {
                files.Add(new DownloadFile
                {
                    Url = Primary(url),
                    FallbackUrl = Fallback(url),
                    Path = Path.Combine(versionDir, entry.Id + ".jar"),
                    Sha1 = GetStr(client, "sha1"),
                    Size = GetLong(client, "size"),
                });
            }
        }

        // 2) libraries (按 rules 过滤 windows)
        if (root.TryGetProperty("libraries", out var libs))
        {
            foreach (var lib in libs.EnumerateArray())
            {
                if (!AllowedOnWindows(lib))
                    continue;
                if (!lib.TryGetProperty("downloads", out var ld) || !ld.TryGetProperty("artifact", out var art))
                    continue;

                var rel = GetStr(art, "path");
                var url = GetStr(art, "url");
                if (string.IsNullOrEmpty(rel) || string.IsNullOrEmpty(url))
                    continue;

                files.Add(new DownloadFile
                {
                    Url = Primary(url),
                    FallbackUrl = Fallback(url),
                    Path = Path.Combine(mcDir, "libraries", rel.Replace('/', Path.DirectorySeparatorChar)),
                    Sha1 = GetStr(art, "sha1"),
                    Size = GetLong(art, "size"),
                });
            }
        }

        // 3) asset index + 所有 asset 对象
        if (root.TryGetProperty("assetIndex", out var ai))
        {
            var aiUrl = GetStr(ai, "url");
            var aiId = GetStr(ai, "id") ?? "legacy";
            var aiRel = ai.TryGetProperty("id", out _) ? $"indexes/{aiId}.json" : "indexes/legacy.json";

            if (!string.IsNullOrEmpty(aiUrl))
            {
                var aiPath = Path.Combine(mcDir, "assets", aiRel.Replace('/', Path.DirectorySeparatorChar));
                files.Add(new DownloadFile
                {
                    Url = Primary(aiUrl),
                    FallbackUrl = Fallback(aiUrl),
                    Path = aiPath,
                    Sha1 = GetStr(ai, "sha1"),
                    Size = GetLong(ai, "size"),
                });

                var (idxText, _) = await FetchTextAsync(aiUrl, usedSource, task, "asset 索引", ct);
                Directory.CreateDirectory(Path.GetDirectoryName(aiPath)!);
                await File.WriteAllTextAsync(aiPath, idxText, ct);

                using var idx = JsonDocument.Parse(idxText);
                if (idx.RootElement.TryGetProperty("objects", out var objects))
                {
                    foreach (var prop in objects.EnumerateObject())
                    {
                        var hash = GetStr(prop.Value, "hash");
                        if (hash is null || hash.Length < 2)
                            continue;
                        var sub = hash[..2];
                        var objUrl = $"https://resources.download.minecraft.net/{sub}/{hash}";
                        files.Add(new DownloadFile
                        {
                            Url = Primary(objUrl),
                            FallbackUrl = Fallback(objUrl),
                            Path = Path.Combine(mcDir, "assets", "objects", sub, hash),
                            Sha1 = hash,
                            Size = GetLong(prop.Value, "size"),
                        });
                    }
                }
            }
        }

        return files;
    }

    /// <summary>
    /// 执行下载。先跳过本地完好文件 (SHA1 一致), 再并发拉缺失部分。
    /// 进度按总字节推进, 全部完成才到 1.0。
    /// </summary>
    /// <param name="quickCheck">
    /// true = 只比对文件大小，不算 SHA1。补全版本时 3000+ 个资源文件全算哈希要好几分钟，
    /// 大小对得上就认为没问题（PCL 的"仅校验文件大小"模式同理）。
    /// </param>
    public static async Task DownloadAsync(
        List<DownloadFile> files, TaskItem task, CancellationToken ct, bool quickCheck = false)
    {
        task.LogLine($"清单共 {files.Count} 个文件,先校验本地已有文件...");
        task.Report(0.02);

        var todo = new List<DownloadFile>();
        long totalBytes = 0;
        int checkedCount = 0;

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!await IsValidLocalAsync(f, ct, quickCheck))
            {
                todo.Add(f);
                totalBytes += Math.Max(f.Size, 0);
            }
            if (++checkedCount % 200 == 0)
                task.LogLine($"已校验 {checkedCount}/{files.Count}...");
        }

        var skipped = files.Count - todo.Count;
        var sizeMb = totalBytes / 1048576.0;
        task.LogLine($"需下载 {todo.Count} 个(跳过 {skipped} 个已存在),约 {sizeMb:0.0} MB");
        task.Report(0.05);

        if (todo.Count == 0)
        {
            task.LogLine("全部文件已存在,无需下载");
            task.Report(1.0);
            return;
        }

        long doneBytes = 0;
        int finished = 0;
        int failedCount = 0;
        var gate = new SemaphoreSlim(Threads);
        var total = Math.Max(totalBytes, 1);

        var workers = todo.Select(async f =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var ok = await DownloadOneAsync(f, task, ct);
                if (ok)
                {
                    Interlocked.Add(ref doneBytes, Math.Max(f.Size, 0));
                    var n = Interlocked.Increment(ref finished);
                    if (n % 50 == 0 || n == todo.Count)
                        task.LogLine($"进度 {n}/{todo.Count} 个文件");
                }
                else
                {
                    Interlocked.Increment(ref failedCount);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failedCount);
                task.LogLine($"[失败] {Path.GetFileName(f.Path)}: {ex.Message}");
            }
            finally
            {
                gate.Release();
                var frac = (double)Interlocked.Read(ref doneBytes) / total;
                task.Report(Math.Clamp(0.05 + frac * 0.94, 0, 0.99));
            }
        });

        await Task.WhenAll(workers);

        if (failedCount > 0)
        {
            // 统计失败的域名, 让用户一眼看出是哪个源挂了
            var hosts = string.Join(", ", todo.Select(f => Net.HostOf(f.Url)).Distinct().Take(4));
            throw new InvalidOperationException(
                $"有 {failedCount} 个文件下载失败(涉及域名: {hosts}),详见任务日志");
        }

        task.Report(1.0);
        task.LogLine("安装完成");
    }

    /// <summary>
    /// 下单个文件。支持**断点续传**：上次留下的 <c>.tmp</c> 会被接着下（Range 请求），
    /// 服务器不支持 Range（回 200 而不是 206）才从头来。
    /// 网络类失败保留 .tmp 以便下次续；校验类失败删掉 .tmp（内容已经坏了，续下去也没用）。
    /// </summary>
    private static async Task<bool> DownloadOneAsync(DownloadFile f, TaskItem task, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(f.Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = f.Path + ".tmp";
        var fileName = Path.GetFileName(f.Path);

        for (var attempt = 1; attempt <= Retries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // 奇数次走主源, 偶数次换另一个源, SSL/连接问题往往只出在某个域名上
            var url = attempt % 2 == 1 ? f.Url : (f.FallbackUrl ?? f.Url);
            try
            {
                // ---- 已有多少能续 ----
                long existing = 0;
                if (File.Exists(tmp))
                {
                    try { existing = new FileInfo(tmp).Length; }
                    catch { existing = 0; }
                }

                // .tmp 比目标还大 → 是坏文件，重下
                if (f.Size > 0 && existing > f.Size)
                {
                    TryDelete(tmp);
                    existing = 0;
                }

                // 已经下满但还没校验（上次卡在最后一步）→ 直接校验收尾
                if (f.Size > 0 && existing == f.Size)
                {
                    if (f.Sha1 is null || await Sha1MatchesAsync(tmp, f.Sha1, ct))
                    {
                        File.Move(tmp, f.Path, overwrite: true);
                        return true;
                    }
                    TryDelete(tmp);
                    existing = 0;
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (existing > 0)
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                var resumed = resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (existing > 0 && !resumed)
                {
                    // 服务器不认 Range，只能从头来
                    task.LogLine($"[续传不支持] {fileName}: 服务器没给 206,从头下载");
                    TryDelete(tmp);
                    existing = 0;
                }
                resp.EnsureSuccessStatusCode();

                var mode = resumed ? FileMode.Append : FileMode.Create;
                await using (var fs = new FileStream(tmp, mode, FileAccess.Write, FileShare.None,
                                 81920, FileOptions.Asynchronous))
                    await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);

                // ---- 校验 ----
                if (f.Size > 0)
                {
                    var actual = new FileInfo(tmp).Length;
                    if (actual != f.Size)
                        throw new InvalidDataException($"大小不符:期望 {f.Size},实际 {actual}");
                }
                if (f.Sha1 is not null && !await Sha1MatchesAsync(tmp, f.Sha1, ct))
                    throw new InvalidDataException("SHA1 校验不通过");

                File.Move(tmp, f.Path, overwrite: true);
                if (existing > 0)
                    task.LogLine($"[续传] {fileName}: 从 {existing / 1024} KB 处接着下完了");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw; // 取消时保留 .tmp，下次能续
            }
            catch (InvalidDataException ex)
            {
                // 内容坏了，续下去也是坏的 → 丢 .tmp 从头来
                task.LogLine($"[内容异常] {fileName}: {ex.Message}(丢弃已下载部分)");
                TryDelete(tmp);
                if (attempt >= Retries)
                    return false;
                await Task.Delay(400 * attempt, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < Retries)
            {
                var srcName = attempt % 2 == 1 ? "换源重试" : "重试";
                task.LogLine($"[{srcName} {attempt}/{Retries}] {fileName}: "
                             + Net.DescribeHttp(ex, url));
                await Task.Delay(400 * attempt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                task.LogLine($"[失败] {fileName}: " + Net.DescribeHttp(ex, url));
                // 网络类失败保留 .tmp, 下次启动还能续
                return false;
            }
        }
        return false;
    }

    private static async Task<bool> IsValidLocalAsync(DownloadFile f, CancellationToken ct, bool quickCheck = false)
    {
        if (!File.Exists(f.Path))
            return false;

        long len;
        try
        {
            len = new FileInfo(f.Path).Length;
        }
        catch
        {
            return false;
        }
        if (len == 0)
            return false;
        // 大小给了且对不上 → 肯定不对，别费劲算哈希了
        if (f.Size > 0 && len != f.Size)
            return false;
        if (f.Sha1 is null || quickCheck)
            return true;
        return await Sha1MatchesAsync(f.Path, f.Sha1, ct);
    }

    private static async Task<bool> Sha1MatchesAsync(string path, string expected, CancellationToken ct)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var hash = await SHA1.HashDataAsync(fs, ct);
            return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>
    /// 官方地址 → 镜像地址。仅 <c>bmclapi</c>/<c>auto</c> 源生效；<c>official</c> 原样返回。
    /// 加载器 maven 也要换，否则国内从 maven.minecraftforge.net 拉库能卡到超时。
    /// </summary>
    public static string Mirror(string url, string source)
    {
        if (string.IsNullOrEmpty(url) || source == "official")
            return url;

        return url
            .Replace("https://launchermeta.mojang.com", Bmcl)
            .Replace("https://piston-meta.mojang.com", Bmcl)
            .Replace("https://launcher.mojang.com", Bmcl)
            .Replace("https://libraries.minecraft.net", Bmcl + "/maven")
            .Replace("https://resources.download.minecraft.net", Bmcl + "/assets")
            .Replace("https://maven.minecraftforge.net", Bmcl + "/maven")
            .Replace("https://maven.neoforged.net/releases", Bmcl + "/maven")
            .Replace("https://maven.fabricmc.net", Bmcl + "/maven");
    }

    /// <summary>rules 过滤: 默认允许, 按 os.name=windows 判定</summary>
    private static bool AllowedOnWindows(JsonElement lib)
    {
        if (!lib.TryGetProperty("rules", out var rules))
            return true;

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var action = GetStr(rule, "action") ?? "allow";
            var matched = true;

            if (rule.TryGetProperty("os", out var os))
            {
                var name = GetStr(os, "name");
                if (name is not null)
                    matched = name == "windows";
            }

            if (matched)
                allowed = action == "allow";
        }
        return allowed;
    }

    private static string? GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
}
