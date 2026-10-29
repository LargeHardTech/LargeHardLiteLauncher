using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 启动游戏: 解析 Java → 解压 natives → 拼 classpath / JVM 参数 / 游戏参数 → 起进程并把输出接到任务日志。
/// </summary>
public static class GameLauncher
{
    /// <summary>
    /// 判断版本是否带 mod 加载器 (Forge/NeoForge/Fabric/Quilt/LiteLoader)。
    /// HMCL 的 MODDED 隔离档靠这个决定要不要隔离。
    /// </summary>
    public static bool IsModdedVersion(MinecraftVersion v)
    {
        var main = v.MainClass ?? "";
        if (main.Contains("forge", StringComparison.OrdinalIgnoreCase)
            || main.Contains("fabric", StringComparison.OrdinalIgnoreCase)
            || main.Contains("quilt", StringComparison.OrdinalIgnoreCase)
            || main.Contains("liteloader", StringComparison.OrdinalIgnoreCase)
            || main.Contains("bootstraplauncher", StringComparison.OrdinalIgnoreCase)
            || main.Contains("knot", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var lib in v.Libraries)
        {
            var n = lib.Name ?? "";
            if (n.Contains("forge", StringComparison.OrdinalIgnoreCase)
                || n.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase)
                || n.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }


    /// <summary>
    /// 从版本 json 的库推断 mod 加载器 (给社区资源按加载器过滤用)。
    /// 返回 fabric / forge / neoforge / quilt / null。
    /// </summary>
    public static string? DetectLoader(MinecraftVersion v)
    {
        foreach (var lib in v.Libraries)
        {
            var n = lib.Name ?? "";
            if (n.StartsWith("net.fabricmc:fabric-loader", StringComparison.OrdinalIgnoreCase))
                return "fabric";
            if (n.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase)
                || n.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
                return "neoforge";
            if (n.StartsWith("net.minecraftforge", StringComparison.OrdinalIgnoreCase)
                || n.Contains(":forge:", StringComparison.OrdinalIgnoreCase))
                return "forge";
            if (n.StartsWith("org.quiltmc", StringComparison.OrdinalIgnoreCase))
                return "quilt";
        }
        return null;
    }

    /// <summary>MC 启动日志里的通用里程碑 (按顺序命中, 只升不降)</summary>
    private static readonly (string Pattern, double Progress)[] Milestones =
    {
        ("Loading Minecraft", 0.25),          // Fabric/Forge 加载器
        ("Environment: Environment", 0.40),   // 原版: 服务端环境初始化
        ("Setting user", 0.48),               // 账户就绪
        ("Backend library", 0.56),            // 渲染后端 (LWJGL version ...)
        ("Reloading ResourceManager", 0.64),  // 资源包加载
        ("OpenAL initialized", 0.72),         // 音频设备
        ("Sound engine started", 0.80),       // 声音引擎
        ("textures/atlas", 0.90),             // 贴图图集 (实际是 textures/atlas/xxx.png-atlas)
        ("Narrator library", 0.95),           // 叙述器
        ("Time elapsed", 1.00),               // 进主菜单
    };

    /// <summary>
    /// 启动指定版本。返回进程退出码。异常向上抛给调用方按类型显示。
    /// </summary>
    public static async Task<int> LaunchAsync(MinecraftVersion v, LauncherConfig cfg, TaskItem task, CancellationToken ct)
    {
        var mcDir = cfg.MinecraftPath;
        task.LogLine($"版本 {v.Id} (需要 Java {v.RequiredJava})");

        // 正版模式: 先把会话刷新好 (access token 只有 24 小时)
        if (AuthState.IsMicrosoft(cfg))
        {
            task.LogLine($"正版账户: {cfg.MsName}");
            await AuthState.EnsureFreshAsync(cfg, task.LogLine, ct).ConfigureAwait(false);
        }
        else
        {
            task.LogLine($"离线账户: {cfg.PlayerName}");
        }

        task.Report(0.05); // 1) 开始

        // ---- 合成生效配置 (全局 + 该版本的独立设置) ----
        var modCount = VersionSettings.CountMods(cfg, v);
        var eff = VersionSettings.Resolve(cfg, v, modCount);
        if (VersionSettings.HasOverride(cfg, v.Id))
            task.LogLine($"版本独立设置:已启用{(eff.MemoryFromAuto ? $" (内存按 {modCount} 个 mod 自动分配)" : "")}");

        // ---- Java (自动模式按版本要求挑"满足要求的最小主版本", 别拿最新的硬上) ----
        var javaPath = JavaDetector.Resolve(eff.JavaPath, v.RequiredJava);
        if (javaPath is null)
            throw new FileNotFoundException("未找到可用的 Java, 请在 设置 → Java 中指定");

        var javaMajor = JavaDetector.Detect()
            .FirstOrDefault(j => string.Equals(j.Path, javaPath, StringComparison.OrdinalIgnoreCase))?.Major ?? 0;
        task.LogLine($"Java: {javaPath} (主版本 {javaMajor})");
        task.Report(0.08); // 2) Java 就绪

        // ---- 32 位 Java 兜底 ----
        // 32 位 JVM 的堆上限约 1.5 GB，-Xmx 超过就直接
        // "Could not reserve enough space for NNNNKB object heap" 起不来（用户踩过）
        if (!JavaDetector.Is64Bit(javaPath))
        {
            const int cap32 = 1024;
            if (eff.MaxMemoryMb > cap32)
            {
                task.LogLine($"检测到 32 位 Java,堆上限约 1.5 GB,最大内存已从 {eff.MaxMemoryMb} MB 降到 {cap32} MB");
                task.LogLine("   如需更大内存,请安装 64 位 Java(设置 → Java)");
                eff = eff with
                {
                    MaxMemoryMb = cap32,
                    MinMemoryMb = Math.Min(eff.MinMemoryMb, cap32 / 2),
                };
            }
            else
            {
                task.LogLine("检测到 32 位 Java,堆上限约 1.5 GB(当前设置未超出,可正常使用)");
            }
            // 32 位 JVM 压缩指针上限也低，别让它把堆外内存算爆
            task.LogLine("   建议改用 64 位 Java,尤其是带 Mod 的版本");
        }

        if (v.RequiredJava > 0 && javaMajor > 0 && javaMajor < v.RequiredJava)
            throw new InvalidOperationException(
                $"Java 版本过低:需要 {v.RequiredJava},当前 {javaMajor}");

        // 过高也要说一句: 1.20.1 的 Forge / LWJGL 3.3.1 在 Java 22+ 上会报
        // "Unsupported JNI version" 或模块解析失败, 用户看着像启动器坏了
        if (v.RequiredJava > 0 && javaMajor > v.RequiredJava + 2)
            task.LogLine($"[警告] 该版本只需 Java {v.RequiredJava},当前 {javaMajor} 偏新; " +
                         "若启动失败,请在 设置 → Java 中指定对应版本(自动模式会选择最合适的版本)");

        if (!File.Exists(v.ClientJar))
            throw new FileNotFoundException($"缺少客户端 jar: {v.ClientJar}");

        // ---- natives 解压 ----
        var nativesDir = Path.Combine(v.Dir, "natives");
        ExtractNatives(v, mcDir, nativesDir, task);
        task.Report(0.12); // 3) natives 解压完

        // ---- classpath ----
        var cp = BuildClassPath(v, mcDir, out var missing);
        if (missing.Count > 0)
        {
            // 用 PCL / HMCL 装的版本常常缺「占位库」, 典型是 1.9.x 的
            // org.lwjgl.lwjgl:lwjgl-platform，json 里 artifact 是个 22 字节的空 jar，
            // 只有 natives classifier 才真有内容，别的启动器会把空 jar 跳过。
            // 所以先自己补一次，补不上的跳过而不是拒绝启动。
            var missingCount = missing.Count;
            task.LogLine($"检测到 {missingCount} 个库文件缺失，正在自动下载：");
            foreach (var m in missing.Take(8))
                task.LogLine("        " + m);
            if (missingCount > 8)
                task.LogLine($"        …以及其他 {missingCount - 8} 个");

            var stillMissing = await FillMissingLibrariesAsync(v, mcDir, cfg, missing, task, ct)
                .ConfigureAwait(false);
            cp = BuildClassPath(v, mcDir, out missing);
            var filled = missingCount - stillMissing;

            if (missing.Count == 0)
            {
                task.LogLine($"已自动补齐 {filled} 个库文件");
            }
            else
            {
                task.LogLine($"[警告] {missing.Count} 个库文件无法自动补齐，已从 classpath 跳过：");
                foreach (var m in missing.Take(8))
                    task.LogLine("        " + m);
                if (missing.Count > 8)
                    task.LogLine($"        …以及其他 {missing.Count - 8} 个");
                task.LogLine("        若游戏内出现 NoClassDefFoundError，请用「版本设置 → 补全版本」修复");
            }
        }
        task.Report(0.15); // 4) classpath 就绪

        // ---- 游戏目录 (版本隔离, 对齐 HMCL DefaultIsolationType) ----
        // 注意顺序: 必须先算 gameDir 再拼参数, --gameDir 要用隔离后的目录,
        // 否则隔离形同虚设 (进程工作目录隔离了, 但游戏自己按 --gameDir 找 mods/saves)
        var modded = IsModdedVersion(v);
        var isolate = eff.Isolation switch
        {
            IsolationMode.Always => true,
            IsolationMode.Modded => modded,
            _ => false,
        };

        var gameDir = isolate ? Path.Combine(mcDir, "versions", v.Id) : mcDir;
        try
        {
            Directory.CreateDirectory(gameDir);
        }
        catch (Exception ex)
        {
            task.LogLine($"[警告] 无法创建目录 {gameDir}: {ex.Message}");
            gameDir = mcDir;
        }

        var modeName = eff.Isolation switch
        {
            IsolationMode.Always => "全部隔离",
            IsolationMode.Modded => modded ? "仅 modded 版本隔离(当前已隔离)" : "仅 modded 版本隔离(原版不隔离)",
            _ => "不隔离",
        };
        task.LogLine($"版本隔离: {modeName} → 工作目录 {gameDir}");

        // ---- 参数 (模板化: 版本 json 的 arguments + ${} 占位符替换) ----
        if (string.IsNullOrEmpty(v.MainClass))
            throw new InvalidOperationException("版本 json 缺少 mainClass");

        var warnings = new List<string>();
        var args = new List<string>();
        args.AddRange(BuildJvmArgs(v, cfg, eff, nativesDir, cp, javaMajor, warnings));
        args.Add(v.MainClass);
        args.AddRange(BuildGameArgs(v, cfg, eff, mcDir, gameDir, warnings));

        foreach (var w in warnings.Distinct())
            LauncherLog.Warn($"参数: {w}");

        task.LogLine($"主类: {v.MainClass}");
        var argSource = v.GameTemplate.Count > 0
            ? $"json 模板 (jvm {v.JvmTemplate.Count} / game {v.GameTemplate.Count})"
            : v.LegacyArguments is not null ? "老格式 minecraftArguments" : "内置拼装";
        task.LogLine($"启动参数: {args.Count} 个,来源 {argSource}");
        task.LogLine($"工作目录: {gameDir}");
        task.LogLine($"内存: {eff.MinMemoryMb}~{eff.MaxMemoryMb} MB | 窗口: {eff.WindowWidth}x{eff.WindowHeight}{(eff.FullScreen ? " (全屏)" : "")}");

        // ---- 启动前命令 (学 PCL 的"启动前执行命令") ----
        if (!string.IsNullOrWhiteSpace(eff.PreLaunchCommand))
            await RunPreLaunchAsync(eff, gameDir, task, ct).ConfigureAwait(false);

        // ---- 起进程 ----
        // 不用 Process.Start：那样启动的 Java 会**继承本启动器的控制台**，
        // 用户点终端窗口的 × 时 Windows 给控制台里所有进程发 CTRL_CLOSE_EVENT，
        // Java 收到就退, 连带整个游戏一起没了。
        // DetachedProcess 用 DETACHED_PROCESS 启动，Java 根本没有控制台；管道不依赖控制台，
        // 所以 stdout 照读、崩溃分析照做。
        DetachedProcess proc;
        try
        {
            proc = DetachedProcess.Start(javaPath, args, gameDir);
        }
        catch (Exception ex)
        {
            task.LogLine($"启动失败: {ex.Message}");
            throw;
        }

        using var _procOwner = proc;
        var tcs = new TaskCompletionSource();

        // MC 日志里的通用里程碑 (原版/Forge/Fabric 都会打), 命中就推进进度, 只升不降
        int milestone = 0; // 已通过的里程碑索引
        proc.OutputLine += line =>
        {
            task.LogLine(line);

            // 从当前位置往后找: 某个里程碑日志缺失 (比如没有音频设备) 也不会卡住
            for (int i = milestone; i < Milestones.Length; i++)
            {
                if (!line.Contains(Milestones[i].Pattern, StringComparison.OrdinalIgnoreCase))
                    continue;
                task.Report(Milestones[i].Progress);
                LauncherLog.Verbose($"进度 {Milestones[i].Progress * 100:0}% (命中日志: {Milestones[i].Pattern})");
                milestone = i + 1;
                break;
            }
        };

        proc.Exited += _ =>
        {
            GameSessionStore.Clear(); // 游戏结束了，会话记录作废
            tcs.TrySetResult();
        };

        LauncherLog.Info($"启动游戏: {v.Id}, Java={javaPath}, PID={proc.Pid}");
        task.Report(0.18); // 5) 进程起来只是开始, 之后由 MC 日志推进
        task.LogLine($"进程已启动,PID={proc.Pid}(已脱离控制台,关闭终端不会终止游戏)");

        // 记录会话：启动器被关掉（哪怕是强杀）之后，下次打开还能认出这个游戏
        GameSessionStore.Save(new GameSession
        {
            Pid = proc.Pid,
            StartTicksUtc = proc.StartedAt.ToUniversalTime().Ticks,
            ProcessName = Path.GetFileNameWithoutExtension(javaPath),
            VersionId = v.Id,
            VersionName = v.Id,
            LaunchedAt = DateTime.Now,
            LogPath = Path.Combine(gameDir, "logs", "latest.log"),
            GameDir = gameDir,
        });

        // ---- 窗口检测 (学 PCL)：真的看到游戏窗口才算"进主界面" ----
        // 日志关键词可能被 mod 改掉，窗口类名（GLFW30/LWJGL）是 LWJGL 定死的，更硬
        var gameWindow = IntPtr.Zero;
        using var windowPoll = new System.Threading.Timer(_ =>
        {
            try
            {
                if (gameWindow != IntPtr.Zero || proc.HasExited)
                    return;
                var found = GameWindowFinder.Find(proc.StartedAt);
                if (!found.Found)
                    return;
                gameWindow = found.Handle;
                task.LogLine($"游戏窗口已出现:{found.Title}");
                task.Report(0.95);
            }
            catch
            {
                // 轮询失败不影响游戏
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        // 兜底: 30 秒还没到 100% (有些版本不打 Time elapsed) 就直接满格
        using var fallback = new System.Threading.Timer(_ =>
        {
            try { task.Report(1.0); } catch { }
        }, null, TimeSpan.FromSeconds(30), System.Threading.Timeout.InfiniteTimeSpan);

        using var reg = ct.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill();
            }
            catch { }
        });

        await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        int exit = proc.ExitCode;
        task.LogLine($"游戏进程退出,代码 {exit}");
        if (exit != 0)
        {
            // 崩溃分析: 把 Java 异常翻译成「哪出问题 + 怎么办」
            var diags = CrashAnalyzer.Analyze(task.LogText, exit);
            if (diags.Count > 0)
            {
                task.LogLine("");
                task.LogLine("===== 崩溃分析 =====");
                for (var i = 0; i < diags.Count; i++)
                {
                    foreach (var line in diags[i].Render(i + 1).Split('\n'))
                        task.LogLine(line);
                }
            }
            else
            {
                task.LogLine("未能匹配已知崩溃特征,完整日志见上。");
            }
            var brief = diags.Count > 0
                ? $"游戏崩溃(退出码 {exit}):{diags[0].Title}"
                : $"游戏异常退出(退出码 {exit}),未能自动识别原因,详见日志";
            throw new GameCrashException(brief, diags);
        }
        return exit;
    }

    /// <summary>
    /// 解压 natives-windows 到 natives 目录（公开入口，自检和重新解压也走这里）。
    /// 26.x 的 jvm 模板把 java.library.path 拆成了子目录（java、jna、lwjgl、netty），
    /// dll 只平铺根目录会让这些子目录为空，所以每个被引用的子目录各解一份。
    /// </summary>
    public static void ExtractNativesFor(MinecraftVersion v, string mcDir, string nativesDir, TaskItem? task = null)
        => ExtractNatives(v, mcDir, nativesDir, task);

    private static void ExtractNatives(MinecraftVersion v, string mcDir, string nativesDir, TaskItem? task)
    {
        Directory.CreateDirectory(nativesDir);

        var subDirs = ExtractNativesSubDirs(v.JvmTemplate);
        var targets = new List<string> { nativesDir };
        foreach (var sd in subDirs)
        {
            var dir = Path.Combine(nativesDir, sd);
            Directory.CreateDirectory(dir);
            targets.Add(dir);
        }
        if (subDirs.Count > 0)
            task?.LogLine($"natives 子目录: {string.Join(", ", subDirs)}");

        int count = 0;
        foreach (var lib in v.Libraries)
        {
            if (!lib.AppliesToWindows || lib.NativesWindowsPath is null)
                continue;
            var zip = Path.Combine(mcDir, "libraries", lib.NativesWindowsPath);
            if (!File.Exists(zip))
            {
                task?.LogLine($"缺少 natives: {lib.NativesWindowsPath}");
                continue;
            }
            try
            {
                using var archive = ZipFile.OpenRead(zip);
                foreach (var entry in archive.Entries)
                {
                    // 跳过 META-INF 和目录项
                    if (string.IsNullOrEmpty(entry.Name) ||
                        entry.FullName.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var dir in targets)
                    {
                        try
                        {
                            entry.ExtractToFile(Path.Combine(dir, entry.Name), overwrite: true);
                            count++;
                        }
                        catch (IOException)
                        {
                            // 同名 dll 被占用 (上一个游戏进程还没退) → 跳过
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                task?.LogLine($"解压 natives 失败 ({lib.NativesWindowsPath}): {ex.Message}");
            }
        }
        if (count > 0)
            task?.LogLine($"natives 已就绪 ({count} 个文件,{targets.Count} 个目录)");
    }

    /// <summary>从 JVM 模板里抽出 ${natives_directory}/xxx 的 xxx 子目录名</summary>
    public static List<string> ExtractNativesSubDirs(IReadOnlyList<string> jvmTemplate)
    {
        const string Marker = "${natives_directory}";
        var result = new List<string>();

        foreach (var a in jvmTemplate)
        {
            int i = a.IndexOf(Marker, StringComparison.Ordinal);
            if (i < 0)
                continue;

            var rest = a[(i + Marker.Length)..].TrimStart('/', '\\');
            if (rest.Length == 0)
                continue;

            int end = rest.IndexOfAny(new[] { '/', '\\', '"' });
            var seg = end >= 0 ? rest[..end] : rest;
            if (seg.Length > 0 && !result.Contains(seg, StringComparer.OrdinalIgnoreCase))
                result.Add(seg);
        }
        return result;
    }

    /// <summary>
    /// 补下启动前发现缺失的库文件。返回仍然缺的个数。
    /// 只补 json 里给了 url 的；没有 url 的（Fabric 之类靠 maven 推导）直接算失败。
    /// 走和游戏下载同一套镜像规则：先按设置的源，失败再换另一个。
    /// </summary>
    private static async Task<int> FillMissingLibrariesAsync(
        MinecraftVersion v, string mcDir, LauncherConfig cfg,
        List<string> missing, TaskItem? task, CancellationToken ct)
    {
        var byPath = new Dictionary<string, LibEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in v.Libraries)
        {
            if (!string.IsNullOrEmpty(lib.ArtifactPath))
                byPath[lib.ArtifactPath] = lib;
        }

        using var http = Net.CreateClient(TimeSpan.FromMinutes(2));
        var stillMissing = 0;
        var done = 0;
        foreach (var rel in missing)
        {
            ct.ThrowIfCancellationRequested();
            done++;

            if (!byPath.TryGetValue(rel, out var lib) || string.IsNullOrEmpty(lib.Url))
            {
                stillMissing++;
                continue;
            }

            var dest = Path.Combine(mcDir, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));

            // 先按设置的源走，不通再换另一个（国内官方源经常卡住）
            var mirrored = GameDownloader.Mirror(lib.Url, cfg.DownloadSource);
            var candidates = new List<string> { mirrored };
            if (!string.Equals(mirrored, lib.Url, StringComparison.OrdinalIgnoreCase))
                candidates.Add(lib.Url);

            Exception? lastError = null;
            var ok = false;
            foreach (var url in candidates)
            {
                try
                {
                    var dir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    var bytes = await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                    if (lib.Size > 0 && bytes.Length != lib.Size)
                        throw new InvalidDataException($"大小不符（期望 {lib.Size}，实际 {bytes.Length}）");
                    await File.WriteAllBytesAsync(dest, bytes, ct).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(lib.Sha1))
                    {
                        var got = await ModUpdateChecker.Sha1OfAsync(dest, ct).ConfigureAwait(false);
                        if (!string.Equals(got, lib.Sha1, StringComparison.OrdinalIgnoreCase))
                        {
                            TryDeleteFile(dest);
                            throw new InvalidDataException("SHA1 不匹配");
                        }
                    }

                    task?.LogLine($"补齐库文件 {Path.GetFileName(rel)}（{bytes.Length} 字节）");
                    ok = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    TryDeleteFile(dest);
                }
            }

            if (!ok)
            {
                stillMissing++;
                task?.LogLine($"[跳过] {Path.GetFileName(rel)}: {Net.Brief(lastError!)}");
            }

            task?.Report(0.13 + 0.02 * done / Math.Max(1, missing.Count));
        }
        return stillMissing;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 删不掉不影响
        }
    }

    /// <summary>拼 classpath；<paramref name="missing"/> 收集磁盘上不存在的库。</summary>
    public static string BuildClassPath(MinecraftVersion v, string mcDir, out List<string> missing)
    {
        missing = new List<string>();
        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lib in v.Libraries)
        {
            if (!lib.AppliesToWindows || lib.ArtifactPath is null)
                continue;
            var full = Path.Combine(mcDir, "libraries", lib.ArtifactPath);
            if (!File.Exists(full))
            {
                missing.Add(lib.ArtifactPath);
                continue;
            }
            if (!seen.Add(full))
                continue;
            sb.Append(full).Append(';');
        }

        sb.Append(v.ClientJar);
        return sb.ToString();
    }

    /// <summary>
    /// 修补 Forge/NeoForge 1.13+ 的 <c>-DignoreList</c>。
    /// Forge 模板里写的是 <c>${version_name}.jar</c>，但 1.13+ 的加载器版本目录没有自己的 jar
    /// （installer 只生成 json，客户端 jar 沿用父版本）。名字对不上时原版 jar 会被当成
    /// 自动模块 <c>_1._20._1</c> 加载，和 minecraft 模块抢 <c>net.minecraft.client.main</c> 包，
    /// 报 <c>ResolutionException</c>。补上真实文件名即可，没有 ignoreList 的版本不受影响。
    /// </summary>
    private static void PatchIgnoreList(List<string> args, MinecraftVersion v)
    {
        if (string.IsNullOrEmpty(v.ClientJar))
            return;
        var jarName = Path.GetFileName(v.ClientJar);
        if (string.IsNullOrEmpty(jarName))
            return;

        for (var i = 0; i < args.Count; i++)
        {
            if (!args[i].StartsWith("-DignoreList=", StringComparison.Ordinal))
                continue;
            var list = args[i]["-DignoreList=".Length..];
            if (list.Contains(jarName, StringComparison.OrdinalIgnoreCase))
                return; // 模板里本来就对得上
            args[i] = $"-DignoreList={list},{jarName}";
            return;
        }
    }

    /// <summary>离线模式 UUID: Java 的 UUID.nameUUIDFromBytes("OfflinePlayer:"+name)</summary>
    public static string OfflineUuidFor(string name) => OfflineUuid(name);

    private static string OfflineUuid(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // variant
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// <summary>
    /// 构建 JVM 部分参数。和实际启动共用同一份逻辑 (以前预览和启动各写一份, 会漂移)。
    /// <paramref name="eff"/> 是「全局 + 版本独立设置」合成后的生效值，内存和自定义参数都从它取。
    /// </summary>
    public static List<string> BuildJvmArgs(
        MinecraftVersion v, LauncherConfig cfg, EffectiveLaunch eff,
        string nativesDir, string classpath, int javaMajor, List<string>? warnings = null)
    {
        var args = new List<string>();

        // 内存统一由配置生成; 自定义参数里的 -Xmx/-Xms 一律过滤, 避免打架
        var maxMem = Math.Max(256, eff.MaxMemoryMb);
        var minMem = Math.Clamp(eff.MinMemoryMb, 128, maxMem);
        args.Add($"-Xms{minMem}M");
        args.Add($"-Xmx{maxMem}M");

        var userArgs = (eff.JvmArgs ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var userSetGc = userArgs.Any(a =>
            a.StartsWith("-XX:+Use", StringComparison.OrdinalIgnoreCase)
            && a.EndsWith("GC", StringComparison.OrdinalIgnoreCase));

        foreach (var seg in userArgs)
        {
            if (seg.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)
                || seg.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase))
                continue;
            // -Xincgc 在 Java 9+ 已移除, 留着直接起不来
            if (javaMajor >= 9 && seg.Equals("-Xincgc", StringComparison.OrdinalIgnoreCase))
                continue;
            args.Add(seg);
        }

        // ---- 编码: Windows 默认 GBK, 不指定的话中文日志会乱码 ----
        args.Add("-Dfile.encoding=UTF-8");
        if (javaMajor >= 19)
        {
            args.Add("-Dstdout.encoding=UTF-8");
            args.Add("-Dstderr.encoding=UTF-8");
        }
        else
        {
            args.Add("-Dsun.stdout.encoding=UTF-8");
            args.Add("-Dsun.stderr.encoding=UTF-8");
        }

        // ---- log4j2 RCE 缓解 (CVE-2021-44228): 老版本 MC 没有这层防护 ----
        args.Add("-Dlog4j2.formatMsgNoLookups=true");
        args.Add("-Djava.rmi.server.useCodebaseOnly=true");
        args.Add("-Dcom.sun.jndi.rmi.object.trustURLCodebase=false");
        args.Add("-Dcom.sun.jndi.cosnaming.object.trustURLCodebase=false");

        // ---- 客户端 jar 位置 (老 Forge / OptiFine 靠它定位) ----
        if (!string.IsNullOrEmpty(v.ClientJar))
            args.Add($"-Dminecraft.client.jar={v.ClientJar}");

        // ---- Forge: 不忽略这两项校验会直接拒绝启动 ----
        args.Add("-Dfml.ignoreInvalidMinecraftCertificates=true");
        args.Add("-Dfml.ignorePatchDiscrepancies=true");

        // ---- 跟随系统代理 ----
        args.Add("-Djava.net.useSystemProxies=true");

        // ---- GC / JIT (Java 8+, 用户自己指定了 GC 就不覆盖) ----
        if (javaMajor >= 8 && !userSetGc)
        {
            args.Add("-XX:+UnlockExperimentalVMOptions");
            args.Add("-XX:+UnlockDiagnosticVMOptions");
            args.Add("-XX:+UseG1GC");
            args.Add("-XX:G1NewSizePercent=20");
            args.Add("-XX:G1ReservePercent=20");
            args.Add("-XX:MaxGCPauseMillis=50");
            args.Add("-XX:G1HeapRegionSize=32M");
            args.Add("-XX:-OmitStackTraceInFastThrow"); // 布尔选项必须 +/- 形式, 写 =false 会让 JVM 直接拒绝启动
        }

        // ---- Java 版本适配 ----
        if (javaMajor == 16)
            args.Add("--illegal-access=permit");
        if (javaMajor is 24 or 25)
            args.Add("--sun-misc-unsafe-memory-access=allow");

        args.Add("-Dminecraft.launcher.brand=LargeHardLiteLauncher");
        args.Add("-Dminecraft.launcher.version=0.1.0");

        // ---- 版本 json 的 JVM 模板 (含 -Djava.library.path / -cp / Forge 专用 -D) ----
        if (v.JvmTemplate.Count > 0)
        {
            var vars = BuildPlaceholders(v, cfg, eff, cfg.MinecraftPath, cfg.MinecraftPath, nativesDir, classpath);
            args.AddRange(ArgumentEngine.Expand(v.JvmTemplate, vars, warnings));
            PatchIgnoreList(args, v);
        }

        // ---- 兜底: 模板里没有的必备项自己补 (老版本 json 常常整段缺失) ----
        if (!v.JvmTemplate.Any(a => a.Contains("java.library.path", StringComparison.OrdinalIgnoreCase)))
            args.Add($"-Djava.library.path={nativesDir}");
        if (!v.JvmTemplate.Any(a => a is "-cp" or "-classpath"))
        {
            args.Add("-cp");
            args.Add(classpath);
        }

        // -D 属性去重: 模板里已经有的 (如 minecraft.launcher.brand / java.library.path)
        // 保留模板那份, 我们的默认值让位, 免得命令行里出现两遍
        return DedupJvmProperties(args);
    }

    /// <summary>
    /// 去掉重复的 -D 属性 (按 key)，保留靠后的那份, 模板里的取值比我们的默认值更贴合版本。
    /// </summary>
    private static List<string> DedupJvmProperties(List<string> args)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keep = new bool[args.Count];

        for (int i = args.Count - 1; i >= 0; i--)
        {
            var a = args[i];
            if (!a.StartsWith("-D", StringComparison.Ordinal))
            {
                keep[i] = true;
                continue;
            }
            int eq = a.IndexOf('=');
            var key = eq > 0 ? a[..eq] : a;
            if (seen.Add(key))
                keep[i] = true;
        }

        var result = new List<string>(args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            if (keep[i])
                result.Add(args[i]);
        }
        return result;
    }

    /// <summary>构建主类之后的游戏参数 (同样走 json 模板)</summary>
    public static List<string> BuildGameArgs(
        MinecraftVersion v, LauncherConfig cfg, EffectiveLaunch eff, string mcDir, string gameDir,
        List<string>? warnings = null)
    {
        var vars = BuildPlaceholders(v, cfg, eff, mcDir, gameDir, Path.Combine(v.Dir, "natives"),
            BuildClassPath(v, mcDir, out _));

        List<string> args;
        if (v.GameTemplate.Count > 0)
        {
            args = ArgumentEngine.Expand(v.GameTemplate, vars, warnings);
        }
        else if (!string.IsNullOrEmpty(v.LegacyArguments))
        {
            // 1.12 及以前: 一整串空格分隔的参数
            args = ArgumentEngine.Expand(ArgumentEngine.SplitLegacy(v.LegacyArguments), vars, warnings);
        }
        else
        {
            args = new List<string>();
        }

        // ---- 兜底: json 里缺什么补什么, 否则游戏进不去 ----
        if (!args.Contains("--username"))
        {
            args.Add("--username");
            args.Add(AuthState.PlayerName(cfg));
        }
        if (!args.Contains("--version"))
        {
            args.Add("--version");
            args.Add(v.Id);
        }
        if (!args.Contains("--gameDir"))
        {
            args.Add("--gameDir");
            args.Add(gameDir);
        }
        if (!args.Contains("--assetsDir"))
        {
            args.Add("--assetsDir");
            args.Add(Path.Combine(mcDir, "assets"));
        }
        if (!args.Contains("--assetIndex") && !string.IsNullOrEmpty(v.AssetIndexId))
        {
            args.Add("--assetIndex");
            args.Add(v.AssetIndexId!);
        }
        if (!args.Contains("--uuid"))
        {
            args.Add("--uuid");
            args.Add(AuthState.Uuid(cfg));
        }
        if (!args.Contains("--accessToken"))
        {
            args.Add("--accessToken");
            args.Add(AuthState.AccessToken(cfg));
        }
        if (!args.Contains("--versionType"))
        {
            args.Add("--versionType");
            args.Add(v.Type);
        }
        if (!args.Contains("--width"))
        {
            args.Add("--width");
            args.Add(Math.Max(320, eff.WindowWidth).ToString());
        }
        if (!args.Contains("--height"))
        {
            args.Add("--height");
            args.Add(Math.Max(240, eff.WindowHeight).ToString());
        }
        if (eff.FullScreen && !args.Contains("--fullscreen"))
            args.Add("--fullscreen");

        return args;
    }

    /// <summary>
    /// 占位符表。值为 null 表示"本机没有这个值", 让 ArgumentEngine 把整个参数丢掉
    /// (离线登录没有 clientid/xuid, 也不支持 quickPlay)。
    /// </summary>
    public static Dictionary<string, string?> BuildPlaceholders(
        MinecraftVersion v, LauncherConfig cfg, EffectiveLaunch eff, string mcDir, string gameDir,
        string nativesDir, string classpath)
    {
        // 账户信息统一走 AuthState (离线/正版两种)
        var playerName = AuthState.PlayerName(cfg);
        var uuid = AuthState.Uuid(cfg);
        var assetsRoot = Path.Combine(mcDir, "assets");

        // 老版本 (1.6/1.7) 的 assets 是虚拟目录: assets/virtual/<index>/
        // 不能看 json 的 assets 字段（1.18+ 那个值就是 index id），要看目录本身在不在
        var gameAssets = assetsRoot;
        if (!string.IsNullOrEmpty(v.AssetIndexId))
        {
            var virtualDir = Path.Combine(assetsRoot, "virtual", v.AssetIndexId);
            if (Directory.Exists(virtualDir))
                gameAssets = virtualDir;
            else
            {
                var mappedDir = Path.Combine(assetsRoot, "resources");
                if (Directory.Exists(mappedDir) && File.Exists(Path.Combine(mappedDir, "minecraft", "lang", "en_us.lang")))
                    gameAssets = mappedDir;
            }
        }

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = playerName,
            ["version_name"] = v.Id,
            ["game_directory"] = gameDir,
            ["assets_root"] = assetsRoot,
            ["game_assets"] = gameAssets,
            ["assets_index_name"] = v.AssetIndexId ?? "legacy",
            ["auth_uuid"] = uuid,
            ["auth_access_token"] = AuthState.AccessToken(cfg),
            ["auth_session"] = $"token:{AuthState.AccessToken(cfg)}:{uuid}",
            ["user_type"] = AuthState.UserType(cfg),
            ["version_type"] = v.Type,
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = "LargeHardLiteLauncher",
            ["launcher_version"] = "0.1.0",
            ["classpath"] = classpath,
            ["classpath_separator"] = ";",
            ["library_directory"] = Path.Combine(mcDir, "libraries"),
            ["primary_jar"] = v.ClientJar ?? "",
            ["resolution_width"] = Math.Max(320, eff.WindowWidth).ToString(),
            ["resolution_height"] = Math.Max(240, eff.WindowHeight).ToString(),
            ["user_properties"] = "{}",

            // 离线模式没有的东西 → 对应参数整条丢弃
            ["clientid"] = null,
            ["auth_xuid"] = null,
            ["quickPlayPath"] = null,
            ["quickPlaySingleplayer"] = null,
            ["quickPlayMultiplayer"] = null,
            ["quickPlayRealms"] = null,
        };
    }

    /// <summary>
    /// 执行「启动前命令」(学 PCL 的 VersionAdvanceRun)。工作目录 = 游戏目录。
    /// 失败只记日志、不阻止启动, 用户写的脚本挂了不该让游戏也开不了。
    /// </summary>
    private static async Task RunPreLaunchAsync(
        EffectiveLaunch eff, string gameDir, TaskItem task, CancellationToken ct)
    {
        task.LogLine($"启动前命令: {eff.PreLaunchCommand}");
        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = gameDir,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(eff.PreLaunchCommand);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                task.LogLine("[警告] 启动前命令没能执行");
                return;
            }

            // 必须并发读两个管道, 否则输出多了会把进程卡住
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            if (!eff.PreLaunchWait)
            {
                task.LogLine("不等命令结束,直接启动游戏");
                _ = Task.Run(async () =>
                {
                    try { await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch { /* 进程没了就算了 */ }
                }, CancellationToken.None);
                return;
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = await stdoutTask.ConfigureAwait(false) + await stderrTask.ConfigureAwait(false);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(20))
                task.LogLine("  " + line.TrimEnd());
            task.LogLine($"启动前命令结束(退出码 {proc.ExitCode})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            task.LogLine($"[警告] 启动前命令失败: {ex.Message}(不影响游戏启动)");
        }
    }

    /// <summary>构建完整的命令行预览 (调试用), 和实际启动走同一套构建逻辑</summary>
    public static string DescribeCommand(MinecraftVersion v, LauncherConfig cfg)
    {
        var modCount = VersionSettings.CountMods(cfg, v);
        var eff = VersionSettings.Resolve(cfg, v, modCount);
        var javaPath = JavaDetector.Resolve(eff.JavaPath, v.RequiredJava) ?? "java";
        var nativesDir = Path.Combine(v.Dir, "natives");
        var cp = BuildClassPath(v, cfg.MinecraftPath, out _);
        var major = JavaDetector.Detect()
            .FirstOrDefault(j => string.Equals(j.Path, javaPath, StringComparison.OrdinalIgnoreCase))?.Major
            ?? JavaDetector.AutoPick(v.RequiredJava)?.Major ?? 17;

        var sb = new StringBuilder();
        sb.Append(javaPath).Append(' ');
        foreach (var a in BuildJvmArgs(v, cfg, eff, nativesDir, cp, major))
            sb.Append(a).Append(' ');
        sb.Append(v.MainClass).Append(' ');
        foreach (var a in BuildGameArgs(v, cfg, eff, cfg.MinecraftPath, cfg.MinecraftPath))
            sb.Append(a).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
