using System.Diagnostics;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 加载器安装器。
/// Fabric / Quilt：取 profile json（含 inheritsFrom 和 libraries），落盘 json + 下库，不跑安装器。
/// Forge / NeoForge（1.13+ 带 processors）：下载 installer jar 后执行
/// <c>java -jar --installClient &lt;dir&gt;</c>。Forge 安装器要求目录里有 launcher_profiles.json，
/// 缺失时会自动补一个最小档。
/// </summary>
public static class LoaderInstaller
{
    private static readonly HttpClient Http = Net.CreateClient(TimeSpan.FromMinutes(5));

    /// <summary>Fabric / Quilt: profile json + 库下载。返回生成的版本 id</summary>
    public static async Task<string> InstallJsonLoaderAsync(
        LoaderKind kind, string mc, string loaderVer, string mcDir, TaskItem task, CancellationToken ct)
    {
        // Fabric: BMCLAPI 镜像优先后官方; Quilt: 只有官方
        var (primary, fallback) = kind == LoaderKind.Fabric
            ? ($"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{mc}/{loaderVer}/profile/json",
               $"https://meta.fabricmc.net/v2/versions/loader/{mc}/{loaderVer}/profile/json")
            : ($"https://meta.quiltmc.org/v3/versions/loader/{mc}/{loaderVer}/profile/json", (string?)null);

        task.LogLine($"获取 {kind} {loaderVer} 的 profile...");
        string json;
        try
        {
            json = await Http.GetStringAsync(primary, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && fallback is not null)
        {
            task.LogLine($"镜像失败,换官方源: {ex.Message}");
            json = await Http.GetStringAsync(fallback, ct).ConfigureAwait(false);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = Str(root, "id") ?? $"{kind.ToString().ToLowerInvariant()}-{loaderVer}-{mc}";

        // 1) 版本 json 落盘
        var versionDir = Path.Combine(mcDir, "versions", id);
        Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(Path.Combine(versionDir, id + ".json"), json, ct).ConfigureAwait(false);
        task.LogLine($"版本 json 已写入 versions/{id}/");
        task.Report(0.2);

        // 2) libraries: profile 里是 maven 坐标 + maven 根 url (带 sha1/size, intermediary 那类没有哈希)
        var files = new List<DownloadFile>();
        if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
        {
            foreach (var lib in libs.EnumerateArray())
            {
                var name = Str(lib, "name");
                var baseUrl = Str(lib, "url");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(baseUrl))
                    continue;
                var rel = MavenPath(name);
                if (rel is null)
                    continue;
                var url = baseUrl.EndsWith('/') ? baseUrl + rel : baseUrl + "/" + rel;
                files.Add(new DownloadFile
                {
                    Url = url,
                    Path = Path.Combine(mcDir, "libraries", rel.Replace('/', Path.DirectorySeparatorChar)),
                    Sha1 = Str(lib, "sha1"),
                    Size = lib.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var sv) ? sv : 0,
                });
            }
        }
        task.LogLine($"库文件 {files.Count} 个");
        await GameDownloader.DownloadAsync(files, task, ct).ConfigureAwait(false);

        task.LogLine($"完成: {id}");
        return id;
    }

    /// <summary>Forge / NeoForge: 下载安装器并用 java 跑 --installClient。返回生成的版本 id</summary>
    public static async Task<string> InstallByInstallerAsync(
        LoaderKind kind, string mc, string loaderVer, string mcDir, TaskItem task, CancellationToken ct)
    {
        // 1) 安装器地址 (Forge 走 BMCLAPI maven 镜像, 官方 maven 兜底; NeoForge 只有官方)
        string primary, fallback;
        if (kind == LoaderKind.Forge)
        {
            var rel = $"net/minecraftforge/forge/{mc}-{loaderVer}/forge-{mc}-{loaderVer}-installer.jar";
            primary = "https://bmclapi2.bangbang93.com/maven/" + rel;
            fallback = "https://maven.minecraftforge.net/" + rel;
        }
        else
        {
            primary = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVer}/neoforge-{loaderVer}-installer.jar";
            fallback = primary;
        }

        // 2) 下载安装器到临时目录
        var tempDir = Path.Combine(mcDir, ".lll-temp");
        Directory.CreateDirectory(tempDir);
        var installer = Path.Combine(tempDir, $"{kind.ToString().ToLowerInvariant()}-{loaderVer}-installer.jar");
        task.LogLine($"下载 {kind} 安装器...");
        var ok = await DownloadWithFallbackAsync(primary, fallback, installer, task, ct).ConfigureAwait(false);
        if (!ok)
            throw new HttpRequestException($"{kind} 安装器下载失败(镜像和官方源都试过了)");
        task.Report(0.3);

        // 3) Forge/NeoForge 安装器硬性要求目标目录有官方启动器的 launcher_profiles.json,
        //    没有会直接报 "There is no Minecraft launcher profile ... you need to run the launcher first!"
        var profiles = Path.Combine(mcDir, "launcher_profiles.json");
        if (!File.Exists(profiles))
        {
            await File.WriteAllTextAsync(profiles,
                "{\"profiles\":{},\"settings\":{},\"version\":3}", ct).ConfigureAwait(false);
            task.LogLine("已补 launcher_profiles.json (安装器要求)");
        }

        // 4) 选 Java: 装 1.18+ 要 17, 1.20.5+ 要 21, 更老的 8/16 都行, 取"满足要求的最小版本"
        var needMajor = RequiredJavaMajor(mc);
        var javas = JavaDetector.Detect();
        var java = javas.Where(j => j.Major >= needMajor).OrderBy(j => j.Major).FirstOrDefault()
                   ?? javas.OrderByDescending(j => j.Major).FirstOrDefault();
        if (java is null)
            throw new InvalidOperationException("没找到 Java, 无法运行安装器。请先在设置里配好 Java 或安装 JDK");
        if (java.Major < needMajor)
            throw new InvalidOperationException(
                $"{mc} 的 {kind} 安装器需要 Java {needMajor}+, 但只找到 Java {java.Major} ({java.Path})");
        task.LogLine($"使用 Java {java.Major}: {java.Path}");

        // 5) 跑安装器: --installClient <mcDir> (非交互; Forge 会自己下它需要的库)
        task.LogLine($"运行安装器(可能几分钟,需要联网下载)...");
        var psi = new ProcessStartInfo
        {
            FileName = java.Path,
            WorkingDirectory = mcDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installer);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(mcDir);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("安装器进程启动失败");
        // stdout/stderr 必须并发读: 安装器输出多, 管道写满会把进程卡死, 我们等退出就永远等不到
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        try
        {
            await Task.WhenAny(
                proc.WaitForExitAsync(ct),
                Task.Delay(TimeSpan.FromMinutes(10), ct)).ConfigureAwait(false);
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                ct.ThrowIfCancellationRequested(); // 用户取消优先报取消, 不是超时
                throw new TimeoutException("安装器超时 (10 分钟),已终止");
            }
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            throw;
        }
        var output = await stdoutTask.ConfigureAwait(false);
        var err = await stderrTask.ConfigureAwait(false);
        foreach (var line in (output + "\n" + err).Split('\n')
                     .Where(l => l.Trim().Length > 0).TakeLast(20))
            task.LogLine("  " + line.Trim());
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"安装器退出码 {proc.ExitCode},详见任务日志");

        // 6) 确认版本目录生成了 (Forge: {mc}-forge-{ver}; NeoForge: neoforge-{ver})
        var expected = kind == LoaderKind.Forge ? $"{mc}-forge-{loaderVer}" : $"neoforge-{loaderVer}";
        if (!Directory.Exists(Path.Combine(mcDir, "versions", expected)))
        {
            var found = Directory.Exists(Path.Combine(mcDir, "versions"))
                ? Directory.GetDirectories(Path.Combine(mcDir, "versions"))
                    .Select(Path.GetFileName)
                    .FirstOrDefault(d => d is not null && d.Contains(loaderVer))
                : null;
            if (found is null)
                throw new InvalidOperationException($"安装器跑完了但没找到新版本目录 ({expected})");
            expected = found;
        }
        try { Directory.Delete(tempDir, recursive: true); } catch { /* 临时目录删不掉不影响 */ }
        task.LogLine($"完成: {expected}");
        return expected;
    }

    /// <summary>下载到指定路径, 镜像失败换官方源。返回是否成功</summary>
    private static async Task<bool> DownloadWithFallbackAsync(
        string primary, string fallback, string dest, TaskItem task, CancellationToken ct)
    {
        File.Delete(dest);
        foreach (var url in new[] { primary, fallback }.Distinct())
        {
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(dest);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                if (new FileInfo(dest).Length > 10_000)
                    return true;
                task.LogLine($"安装器文件异常小,换源重试: {url}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                task.LogLine($"下载失败: {ex.Message}");
            }
        }
        return false;
    }

    /// <summary>
    /// 安装后把版本目录改成用户起的名字: 目录 + json 文件名 + json 里的 "id" 字段 + 客户端 jar 名。
    /// 加载器 json 是 inheritsFrom 基础原版, 改自己的 id 不影响继承。
    /// </summary>
    public static string RenameVersion(string mcDir, string oldId, string newId, TaskItem task)
    {
        if (string.Equals(oldId, newId, StringComparison.Ordinal))
            return oldId;

        var versionsDir = Path.Combine(mcDir, "versions");
        var oldDir = Path.Combine(versionsDir, oldId);
        if (!Directory.Exists(oldDir))
        {
            task.LogLine($"没找到生成的版本目录 {oldId},名字未生效");
            return oldId;
        }

        var newDir = Path.Combine(versionsDir, newId);
        if (Directory.Exists(newDir))
            throw new InvalidOperationException($"版本 {newId} 已存在,换个名字");

        Directory.Move(oldDir, newDir);

        // json: 改 id 字段 + 改文件名
        var oldJson = Path.Combine(newDir, oldId + ".json");
        if (File.Exists(oldJson))
        {
            var text = File.ReadAllText(oldJson);
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject obj)
                {
                    obj["id"] = newId;
                    text = obj.ToJsonString();
                }
            }
            catch
            {
                /* 解析不了就只改文件名 */
            }
            File.WriteAllText(Path.Combine(newDir, newId + ".json"), text);
            File.Delete(oldJson);
        }

        // 客户端 jar 的文件名也跟着改 ({id}.jar 是按约定找的)
        var oldJar = Path.Combine(newDir, oldId + ".jar");
        if (File.Exists(oldJar))
            File.Move(oldJar, Path.Combine(newDir, newId + ".jar"));

        task.LogLine($"版本已重命名: {oldId} → {newId}");
        return newId;
    }

    /// <summary>该 MC 版本跑 Forge/NeoForge 安装器需要的最低 Java 主版本</summary>
    private static int RequiredJavaMajor(string mc)
    {
        if (CompareVersion(mc, "1.20.5") >= 0) return 21;
        if (CompareVersion(mc, "1.18") >= 0) return 17;
        if (CompareVersion(mc, "1.17") >= 0) return 16;
        return 8;
    }

    /// <summary>版本号比较: a>b 返回正, 相等 0, 小于负 (按数字段)</summary>
    private static int CompareVersion(string a, string b)
    {
        var pa = a.TrimStart('v', 'V').Split('.');
        var pb = b.TrimStart('v', 'V').Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var sa = i < pa.Length ? pa[i] : "0";
            var sb = i < pb.Length ? pb[i] : "0";
            if (int.TryParse(sa, out var na) && int.TryParse(sb, out var nb))
            {
                if (na != nb) return na.CompareTo(nb);
            }
            else
            {
                var c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
        }
        return 0;
    }

    /// <summary>maven 坐标 → libraries 下的相对路径 (和 VersionScanner.MavenPath 同规则)</summary>
    private static string? MavenPath(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
            return null;
        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? parts[3] : null;
        if (classifier is not null && classifier.StartsWith("natives", StringComparison.OrdinalIgnoreCase))
            return null;
        var file = classifier is null
            ? $"{artifact}-{version}.jar"
            : $"{artifact}-{version}-{classifier}.jar";
        return $"{group.Replace('.', '/')}/{artifact}/{version}/{file}";
    }

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
