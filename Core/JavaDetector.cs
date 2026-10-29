using System.Text.RegularExpressions;

namespace LargeHardLiteLauncher.Core;

/// <summary>一个已安装的 Java</summary>
public sealed class JavaInfo
{
    /// <summary>java.exe 完整路径</summary>
    public string Path { get; init; } = "";
    /// <summary>版本号 (如 17.0.8 / 1.8.0_301)</summary>
    public string Version { get; init; } = "?";
    /// <summary>主版本号 (8 / 17 / 21)</summary>
    public int Major { get; init; }
    /// <summary>发行商 (目录名推测)</summary>
    public string Vendor { get; init; } = "";

    public string Display => $"{Version}  ({Path})";
}

/// <summary>
/// 系统已安装 Java 探测: JAVA_HOME / PATH / 常见安装目录 (Adoptium/Corretto/Zulu/Microsoft/Java…)。
/// 版本从 JDK 目录下的 release 文件读取, 不启动进程 (快)。
/// </summary>
public static class JavaDetector
{
    private static readonly string[] CommonRoots =
    {
        @"ProgramFiles\Java",
        @"ProgramFiles\Eclipse Adoptium",
        @"ProgramFiles\Eclipse Foundation",
        @"ProgramFiles\Microsoft",
        @"ProgramFiles\Amazon Corretto",
        @"ProgramFiles\Zulu",
        @"ProgramFiles\BellSoft",
        @"ProgramFiles\Semeru",
        @"ProgramFiles (x86)\Java",
        @"ProgramFiles (x86)\Eclipse Adoptium",
    };

    /// <summary>探测所有 Java, 按主版本降序</summary>
    public static List<JavaInfo> Detect()
    {
        var found = new Dictionary<string, JavaInfo>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? javaExe)
        {
            if (string.IsNullOrWhiteSpace(javaExe))
                return;
            try
            {
                var full = Path.GetFullPath(javaExe);
                if (!File.Exists(full) || found.ContainsKey(full))
                    return;
                found[full] = ReadInfo(full);
            }
            catch { /* 路径非法忽略 */ }
        }

        // 1) JAVA_HOME
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            AddCandidate(Path.Combine(javaHome, "bin", "java.exe"));

        // 2) PATH
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (dir.Contains("java", StringComparison.OrdinalIgnoreCase))
                AddCandidate(Path.Combine(dir.Trim(), "java.exe"));
        }

        // 3) 常见安装目录下的子目录 (vendor\jdk-17\bin\java.exe 或 java\17\bin\java.exe)
        foreach (var root in CommonRoots)
        {
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                       Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                var dir = Path.Combine(pf, root.Replace(@"ProgramFiles (x86)\", "").Replace(@"ProgramFiles\", ""));
                if (!Directory.Exists(dir))
                    continue;
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        AddCandidate(Path.Combine(sub, "bin", "java.exe"));
                }
                catch { /* 权限不足忽略 */ }
            }
        }

        // 4) PER-USER 安装 (Adoptium/MS 会装到 LocalAppData)
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var rel in new[]
                 {
                     @"Programs\Eclipse Adoptium", @"Programs\Eclipse Foundation",
                     @"Programs\Microsoft", @"Programs\Amazon Corretto",
                 })
        {
            var dir = Path.Combine(localApp, rel);
            if (!Directory.Exists(dir))
                continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    AddCandidate(Path.Combine(sub, "bin", "java.exe"));
            }
            catch { }
        }

        // 5) 启动器自己下载的 Java (<数据目录>/java/jdk-17.0.13+11/bin/java.exe)
        // 这是"缺 Java 自动下"的落地位置，要优先能被扫到
        var ownJavaRoot = Path.Combine(LauncherConfig.RootDir, "java");
        if (Directory.Exists(ownJavaRoot))
        {
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(ownJavaRoot))
                    AddCandidate(Path.Combine(sub, "bin", "java.exe"));
            }
            catch { /* 权限问题忽略 */ }
        }

        return found.Values.OrderByDescending(j => j.Major).ThenByDescending(j => j.Version).ToList();
    }

    /// <summary>
    /// 自动挑一个 Java。
    /// <para><paramref name="requiredMajor"/> &gt; 0 时按「满足要求的最小主版本」挑 (对齐 PCL/HMCL):
    /// 1.20.1 要 17 就给它 17, 不能拿机器上最新的 25 硬上,
    /// Forge 1.20.1 的 ModLauncher 10 / LWJGL 3.3.1 在 Java 25 下会报
    /// 「Unsupported JNI version」甚至模块解析失败。</para>
    /// 没给要求时退回「17 及以上最高的」。
    /// </summary>
    public static JavaInfo? AutoPick(int requiredMajor = 0)
    {
        var all = Detect();
        if (all.Count == 0)
            return null;

        if (requiredMajor > 0)
        {
            // ① 主版本正好对上 (17 → 17)
            var exact = all.Where(j => j.Major == requiredMajor)
                           .OrderByDescending(j => j.Version, StringComparer.OrdinalIgnoreCase)
                           .FirstOrDefault();
            if (exact is not null)
                return exact;

            // ② 比要求高的里面最小的 (要 17 只有 21 就用 21, 别直接跳 25)
            var above = all.Where(j => j.Major > requiredMajor)
                           .OrderBy(j => j.Major)
                           .FirstOrDefault();
            if (above is not null)
                return above;

            // ③ 全都比要求低: 只能挑最高的, 后面启动时会明确报「版本过低」
            return all.OrderByDescending(j => j.Major).First();
        }

        var modern = all.Where(j => j.Major >= 17).OrderByDescending(j => j.Major).FirstOrDefault();
        return modern ?? all[0];
    }

    /// <summary>从 JDK 的 release 文件读版本, 读不到返回 "?"</summary>
    private static JavaInfo ReadInfo(string javaExe)
    {
        var binDir = Path.GetDirectoryName(javaExe) ?? "";
        var jdkDir = Path.GetDirectoryName(binDir) ?? "";
        string version = "?";
        int major = 0;

        try
        {
            var releaseFile = Path.Combine(jdkDir, "release");
            if (File.Exists(releaseFile))
            {
                foreach (var line in File.ReadLines(releaseFile))
                {
                    if (!line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
                        continue;
                    version = line["JAVA_VERSION=".Length..].Trim().Trim('"');
                    major = ParseMajor(version);
                    break;
                }
            }
        }
        catch { }

        return new JavaInfo
        {
            Path = javaExe,
            Version = version,
            Major = major,
            Vendor = Path.GetFileName(jdkDir),
        };
    }

    /// <summary>"1.8.0_301" → 8, "17.0.8" → 17, "21" → 21</summary>
    public static int ParseMajor(string version)
    {
        if (string.IsNullOrEmpty(version))
            return 0;
        if (version.StartsWith("1."))
        {
            var m = Regex.Match(version, @"^1\.(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
        var m2 = Regex.Match(version, @"^(\d+)");
        return m2.Success ? int.Parse(m2.Groups[1].Value) : 0;
    }

    /// <summary>解析配置里的 JavaPath: 空 = 自动 (按版本要求挑最合适的那份)</summary>
    public static string? Resolve(string configJavaPath, int requiredMajor = 0)
    {
        if (!string.IsNullOrWhiteSpace(configJavaPath) && File.Exists(configJavaPath))
            return configJavaPath; // 用户显式指定的照用, 版本不符只在日志里提醒
        return AutoPick(requiredMajor)?.Path;
    }

    /// <summary>
    /// 和 <see cref="Resolve"/> 挑同一份 Java，但返回完整信息（主页要显示版本号）。
    /// </summary>
    public static JavaInfo? ResolveInfo(string configJavaPath, int requiredMajor = 0)
    {
        if (!string.IsNullOrWhiteSpace(configJavaPath) && File.Exists(configJavaPath))
        {
            foreach (var j in Detect())
            {
                if (string.Equals(j.Path, configJavaPath, StringComparison.OrdinalIgnoreCase))
                    return j;
            }
            return ReadInfo(configJavaPath); // 不在探测结果里(比如自己解压的)，单独读一次
        }
        return AutoPick(requiredMajor);
    }

    /// <summary>
    /// 判断 java.exe 是不是 64 位（读 PE 头，最可靠）。
    ///
    /// 为什么要判：**32 位 JVM 的堆上限约 1.5 GB**，-Xmx2G 会直接
    /// "Could not reserve enough space for NNNNKB object heap" 起不来。
    /// 光看路径含 "Program Files (x86)" 不可靠（有人把 64 位 JDK 解压到那里）。
    /// </summary>
    public static bool Is64Bit(string exePath)
    {
        try
        {
            using var fs = File.OpenRead(exePath);
            using var br = new BinaryReader(fs);

            if (br.ReadUInt16() != 0x5A4D) // "MZ"
                return true; // 不是 PE，没法判断，按 64 位放行

            fs.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 > fs.Length)
                return true;

            fs.Seek(peOffset, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x00004550) // "PE\0\0"
                return true;

            var machine = br.ReadUInt16();
            return machine is 0x8664 or 0xAA64 or 0x0200; // x64 / arm64 / ia64
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"读 {exePath} 的 PE 头失败(按 64 位处理): {ex.Message}");
            return true;
        }
    }
}
