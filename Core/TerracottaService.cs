using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 陶瓦联机（Terracotta）本地服务管理。Terracotta 是独立的 Rust 程序，内嵌 EasyTier 做 P2P 组网。
/// 启动方式 <c>terracotta.exe --hmcl &lt;端口文件&gt;</c>，它把监听的端口以 <c>{"port":N}</c>
/// 写进该文件，之后所有操作走 <c>http://127.0.0.1:N</c> 的 REST 接口。
/// 注意 <c>--hmcl</c> 模式会 fork 子进程，父进程写完端口就退出，判断存活要看 HTTP 而不是进程句柄。
/// 另外访问 127.0.0.1 必须关代理，否则请求会被发给用户开的代理软件。
/// </summary>
public sealed class TerracottaService : IDisposable
{
    /// <summary>内嵌的版本号（跟 Resources/terracotta.zip 对应）</summary>
    public const string Version = "0.4.2";

    /// <summary>中继节点列表（HMCL 用的同一个地址）</summary>
    private const string NodeListUrl = "https://terracotta.glavo.site/nodes";

    private Process? _proc;
    private HttpClient? _local;
    private int _port;
    private static List<string>? _nodeCache;

    /// <summary>terracotta 子进程的输出（页面拿它显示到日志区）</summary>
    public event Action<string>? LogLine;

    private bool _stopping;

    public static string InstallDir => Path.Combine(LauncherConfig.RootDir, "terracotta");

    /// <summary>
    /// 不能用 `_proc.HasExited` 判断服务死活。
    /// `--hmcl` 模式下 terracotta 会 **fork 一个子进程**（真正干活的那个，写端口、跑 Rocket），
    /// 我们启动的父进程写完端口文件就退出了。
    /// 所以判断标准只有一个：HTTP 还能不能连上。
    /// </summary>
    public bool IsRunning => IsReady;

    public int Port => _port;
    public bool IsReady => _port > 0 && _local is not null;

    // ---------- 安装 ----------

    /// <summary>找到解压出来的 exe（版本号在文件名里，所以用通配匹配）</summary>
    private static string? FindExe()
    {
        if (!Directory.Exists(InstallDir))
            return null;
        try
        {
            return Directory.GetFiles(InstallDir, "terracotta-*.exe").FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 首次使用：把内嵌的 zip 解压到数据目录。
    /// 已经解压过就跳过（省几秒）。
    /// </summary>
    public static async Task<string> EnsureInstalledAsync(TaskItem? task, CancellationToken ct)
    {
        if (FindExe() is { } existing)
            return existing;

        task?.LogLine($"首次使用,解压内嵌的 Terracotta v{Version}...");
        Directory.CreateDirectory(InstallDir);

        await using var res = typeof(TerracottaService).Assembly.GetManifestResourceStream("terracotta.zip")
            ?? throw new InvalidOperationException("程序里没有找到内嵌的 Terracotta 包(发布时漏了资源?)");

        var tmp = Path.Combine(Path.GetTempPath(), $"lll-tc-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var fs = File.Create(tmp))
                await res.CopyToAsync(fs, ct).ConfigureAwait(false);

            ZipFile.ExtractToDirectory(tmp, InstallDir, overwriteFiles: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 删不掉无所谓 */ }
        }

        var exe = FindExe() ?? throw new InvalidOperationException(
            $"解压后没找到 terracotta-*.exe(目录:{InstallDir})");
        task?.LogLine($"已解压到 {exe}");
        return exe;
    }

    // ---------- 启动 / 停止 ----------

    /// <summary>
    /// 起进程并等它写出端口。返回端口号。
    /// </summary>
    public async Task<int> StartAsync(TaskItem? task, CancellationToken ct)
    {
        if (IsReady)
            return _port;

        Stop(); // 清掉残留

        var exe = await EnsureInstalledAsync(task, ct).ConfigureAwait(false);

        // 端口文件路径：临时目录里随便建一个，交给 terracotta 去写
        var portFile = Path.Combine(Path.GetTempPath(), $"lll-tc-{Guid.NewGuid():N}", "http");
        Directory.CreateDirectory(Path.GetDirectoryName(portFile)!);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = InstallDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--hmcl");
        psi.ArgumentList.Add(portFile);

        task?.LogLine($"启动: {exe} --hmcl {portFile}");
        _stopping = false;
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("terracotta 进程起不来");
        _proc.EnableRaisingEvents = true;
        _proc.Exited += (_, _) =>
        {
            if (_stopping)
                return;
            // 正常！--hmcl 模式下父进程写完端口就退，真正干活的是它 fork 的子进程。
            // 只在日志里留一句，不动 UI 状态（状态以 HTTP 为准）。
            var code = -1;
            try { code = _proc?.ExitCode ?? -1; } catch { /* 拿不到就算了 */ }
            LauncherLog.Info($"terracotta 启动器进程退出(代码 {code}),服务由它拉起的子进程继续提供");
            LogLine?.Invoke($"[提示] 启动器进程已退出(代码 {code}),服务仍在后台运行");
        };

        _proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            task?.LogLine("[tc] " + e.Data);
            LogLine?.Invoke(e.Data);
        };
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            task?.LogLine("[tc!] " + e.Data);
            LogLine?.Invoke("[stderr] " + e.Data);
        };
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        // 等它把端口写进文件（一般 1 秒内）
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (File.Exists(portFile))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(portFile, ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number)
                    {
                        _port = p.GetInt32();
                        break;
                    }
                }
                catch (IOException)
                {
                    // 它可能正在写，过一会儿再读
                }
                catch (JsonException)
                {
                    // 同上
                }
            }

            if (_proc.HasExited)
                throw new InvalidOperationException(
                    $"terracotta 启动后就退出了(代码 {_proc.ExitCode})。\n" +
                    "常见原因:上一次的进程没关干净(它有单实例锁),重启启动器尝试。");

            await Task.Delay(120, ct).ConfigureAwait(false);
        }

        if (_port <= 0)
        {
            Stop();
            throw new TimeoutException("等了 25 秒还没拿到 terracotta 的端口,启动失败。");
        }

        // UseProxy = false：不关掉的话，开着代理软件时连不上 127.0.0.1
        _local = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_port}/"),
            Timeout = TimeSpan.FromSeconds(10),
        };

        task?.LogLine($"terracotta 就绪,端口 {_port}");
        return _port;
    }

    /// <summary>杀掉进程（Terracotta 会自己清理临时目录和 EasyTier 子进程）</summary>
    public void Stop()
    {
        _stopping = true;
        _local?.Dispose();
        _local = null;
        _port = 0;

        var p = _proc;
        _proc = null;
        if (p is not null)
        {
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已经退了
            }
            finally
            {
                p.Dispose();
            }
        }

        KillAllTerracotta();
    }

    /// <summary>
    /// 按进程名清场。**必须做**：`--hmcl` 模式的父进程早就退了，
    /// 真正干活的是它 fork 的子进程，光 Kill(_proc) 杀不掉。
    /// （Terracotta 有全局单实例锁，所以按名字杀不会误伤别的正常实例）
    /// </summary>
    private static void KillAllTerracotta()
    {
        var exe = FindExe();
        if (exe is null)
            return;
        var name = Path.GetFileNameWithoutExtension(exe);
        if (string.IsNullOrEmpty(name))
            return;

        try
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 单个杀不掉不影响其它
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"清理 terracotta 进程失败: {ex.Message}");
        }
    }

    public void Dispose() => Stop();

    // ---------- REST ----------

    public async Task<TerracottaSnapshot?> GetStateAsync(CancellationToken ct)
    {
        if (_local is null)
            return null;
        var json = await _local.GetStringAsync("state", ct).ConfigureAwait(false);
        return TerracottaSnapshot.Parse(json);
    }

    /// <summary>回到待机（同时退出房间）</summary>
    public Task SetWaitingAsync(CancellationToken ct) => GetAsync("state/ide", ct);

    /// <summary>开房：先扫本机的 MC 局域网世界，扫到就建隧道</summary>
    public Task HostAsync(string playerName, IReadOnlyList<string> nodes, CancellationToken ct)
        => GetAsync(BuildUrl("state/scanning", null, playerName, nodes), ct);

    /// <summary>加房</summary>
    public Task GuestAsync(string room, string playerName, IReadOnlyList<string> nodes, CancellationToken ct)
        => GetAsync(BuildUrl("state/guesting", room, playerName, nodes), ct);

    /// <summary>导出 Terracotta 自己的日志（它内部记的 EasyTier 输出）</summary>
    public async Task<string> ExportLogAsync(CancellationToken ct)
    {
        if (_local is null)
            throw new InvalidOperationException("Terracotta 还没启动。");
        return await _local.GetStringAsync("log?fetch=true", ct).ConfigureAwait(false);
    }

    private async Task GetAsync(string path, CancellationToken ct)
    {
        if (_local is null)
            throw new InvalidOperationException("Terracotta 还没启动。");
        using var resp = await _local.GetAsync(path, ct).ConfigureAwait(false);
        // 400 = 房间码非法 / 状态不对，让调用方看到
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Terracotta 返回 {(int)resp.StatusCode}({resp.ReasonPhrase})");
    }

    private static string BuildUrl(string path, string? room, string player, IReadOnlyList<string> nodes)
    {
        var q = new List<string>();
        if (room is not null)
            q.Add("room=" + Uri.EscapeDataString(room));
        q.Add("player=" + Uri.EscapeDataString(player));
        foreach (var n in nodes)
            q.Add("public_nodes=" + Uri.EscapeDataString(n));
        return q.Count == 0 ? path : $"{path}?{string.Join("&", q)}";
    }

    // ---------- 中继节点 ----------

    /// <summary>
    /// 拉公共中继节点列表。失败返回空列表（Terracotta 会用自带的默认节点）。
    /// 结果进程内缓存。
    /// </summary>
    public static async Task<List<string>> FetchNodesAsync(CancellationToken ct)
    {
        if (_nodeCache is not null)
            return _nodeCache;

        var list = new List<string>();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LargeHardLiteLauncher/0.1");
            var json = await http.GetStringAsync(NodeListUrl, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in doc.RootElement.EnumerateArray())
                {
                    if (node.ValueKind != JsonValueKind.Object
                        || !node.TryGetProperty("url", out var u)
                        || u.ValueKind != JsonValueKind.String)
                        continue;
                    var url = u.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                        list.Add(url!);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"拉取陶瓦节点列表失败(不影响使用): {ex.Message}");
        }

        _nodeCache = list;
        return list;
    }

    /// <summary>读出内嵌的 AGPL-3.0 许可证全文（关于页 / 首次使用时展示）</summary>
    public static string ReadLicense()
    {
        try
        {
            using var s = typeof(TerracottaService).Assembly
                .GetManifestResourceStream("TERRACOTTA-LICENSE.txt");
            if (s is null)
                return "(程序里没有找到许可证文件)";
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch (Exception ex)
        {
            return $"(读取许可证失败: {ex.Message})";
        }
    }
}
