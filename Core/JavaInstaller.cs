using System.IO.Compression;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>一个可下载的 Java 包（Adoptium 提供的 JDK）</summary>
public sealed class JavaPackage
{
    public int Major { get; init; }
    /// <summary>完整版本号，比如 17.0.13+11</summary>
    public string Version { get; init; } = "";
    public string Url { get; init; } = "";
    public string FileName { get; init; } = "";
    public long Size { get; init; }
    /// <summary>Adoptium 给的 sha256（校验用）</summary>
    public string? Sha256 { get; init; }

    public string Display => $"Java {Major}({Version}, {Size / 1048576.0:0} MB)";
}

/// <summary>
/// 自动下载 Java（学 PCL 的「缺 Java 就自己下」）。
/// 源用 Adoptium 官方 API（Eclipse Temurin 构建，商用免费，和 Mojang 推荐的一致），
/// 下到 <c>&lt;数据目录&gt;/java/</c> 下，<see cref="JavaDetector"/> 会自动扫到。
/// </summary>
public static class JavaInstaller
{
    private const string ApiBase = "https://api.adoptium.net/v3";

    /// <summary>给用户选的常用主版本（1.20.5+ 要 21，1.18+ 要 17，老版本要 8）</summary>
    public static readonly int[] OfferMajors = { 21, 17, 8 };

    /// <summary>下载安装根目录</summary>
    public static string InstallRoot => Path.Combine(LauncherConfig.RootDir, "java");

    /// <summary>查询某主版本的最新 JDK 包（Windows x64 zip）</summary>
    public static async Task<JavaPackage?> FetchLatestAsync(int major, CancellationToken ct)
    {
        var url = $"{ApiBase}/assets/latest/{major}/hotspot"
                  + "?architecture=x64&image_type=jdk&os=windows&vendor=eclipse";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LargeHardLiteLauncher/0.1");
        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        var first = doc.RootElement[0];
        if (!first.TryGetProperty("binary", out var binary)
            || !binary.TryGetProperty("package", out var pkg))
            return null;

        var link = Str(pkg, "link");
        if (string.IsNullOrEmpty(link))
            return null;

        // 版本号从 asset 的 version 里取（形如 {"major":17,"minor":0,"security":13,"build":11}）
        var version = "";
        if (first.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Object)
        {
            var majorV = Num(ver, "major");
            var minorV = Num(ver, "minor");
            var sec = Num(ver, "security");
            var build = Num(ver, "build");
            version = $"{majorV}.{minorV}.{sec}" + (build > 0 ? $"+{build}" : "");
        }

        return new JavaPackage
        {
            Major = major,
            Version = version.Length > 0 ? version : major.ToString(),
            Url = link,
            FileName = Str(pkg, "name") ?? $"jdk-{major}.zip",
            Size = Num(pkg, "size"),
            Sha256 = Str(pkg, "checksum"),
        };
    }

    /// <summary>
    /// 下载并解压。<paramref name="progress"/> 回调 (百分比 0~1, 说明)。
    /// 返回 java.exe 的完整路径。
    /// </summary>
    public static async Task<string> InstallAsync(
        JavaPackage pkg, Action<double, string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(InstallRoot);
        var zipPath = Path.Combine(InstallRoot, pkg.FileName + ".tmp");

        // ---- 1) 下载 ----
        progress?.Invoke(0.02, $"下载 {pkg.FileName}({pkg.Size / 1048576.0:0} MB)");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LargeHardLiteLauncher/0.1");
            using var resp = await http.GetAsync(pkg.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? pkg.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(zipPath);

            var buffer = new byte[81920];
            long done = 0;
            int n;
            var lastReport = 0.0;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0)
                {
                    var pct = 0.02 + done / (double)total * 0.73; // 下载占 2%~75%
                    if (pct - lastReport >= 0.02)
                    {
                        lastReport = pct;
                        progress?.Invoke(pct, $"下载中 {done / 1048576.0:0}/{total / 1048576.0:0} MB");
                    }
                }
            }
        }

        // ---- 2) SHA256 校验（Adoptium 给了就必须对得上）----
        if (!string.IsNullOrEmpty(pkg.Sha256))
        {
            progress?.Invoke(0.76, "校验文件完整性...");
            string actual;
            await using (var fs = File.OpenRead(zipPath))
            {
                var hash = await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
                actual = Convert.ToHexString(hash).ToLowerInvariant();
            }
            if (!actual.Equals(pkg.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(zipPath);
                throw new InvalidOperationException(
                    "下载的 Java 包校验失败(文件被截断或被中间人改过),已删除。请重试或换个网络。");
            }
        }

        // ---- 3) 解压 ----
        progress?.Invoke(0.80, "解压中...");
        var staging = Path.Combine(InstallRoot, "_unzip_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging);
            TryDelete(zipPath);

            // zip 顶层一般就是一个目录（jdk-17.0.13+11），把它挪到最终位置
            var dirs = Directory.GetDirectories(staging);
            var files = Directory.GetFiles(staging);
            string finalDir;
            if (dirs.Length == 1 && files.Length == 0)
            {
                var inner = dirs[0];
                var targetName = Path.GetFileName(inner);
                finalDir = Path.Combine(InstallRoot, targetName);
                if (Directory.Exists(finalDir))
                    Directory.Delete(finalDir, recursive: true); // 重装同一个版本
                Directory.Move(inner, finalDir);
            }
            else
            {
                // 结构不符合预期（顶层直接是文件）→ 整个 staging 当最终目录
                finalDir = Path.Combine(InstallRoot, $"jdk-{pkg.Major}-{Sanitize(pkg.Version)}");
                if (Directory.Exists(finalDir))
                    Directory.Delete(finalDir, recursive: true);
                Directory.Move(staging, finalDir);
                staging = ""; // 已经移走了，别在 finally 里再删
            }

            var javaExe = Path.Combine(finalDir, "bin", "java.exe");
            if (!File.Exists(javaExe))
                throw new InvalidOperationException($"解压后找不到 bin\\java.exe({finalDir}),这个包结构不对");

            progress?.Invoke(1.0, $"装好了:{finalDir}");
            LauncherLog.Info($"已下载 Java {pkg.Display} → {finalDir}");
            return javaExe;
        }
        finally
        {
            if (staging.Length > 0)
                TryDeleteDir(staging);
            TryDelete(zipPath);
        }
    }

    private static string Sanitize(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+' ? c : '_').ToArray());

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* 删不掉无所谓 */ }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { /* 删不掉无所谓 */ }
    }

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
