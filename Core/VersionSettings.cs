namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 单个版本的独立设置（学 PCL2 的「版本独立设置」）。
/// 每个字段都是"可覆盖"，未设置时回落到启动器全局设置。
/// 存在 <see cref="LauncherConfig.VersionOptions"/> 里，key = 版本目录名。
/// </summary>
public sealed class VersionOption
{
    /// <summary>内存模式: 0=自动(按 mod 数量估算) 1=手动 2=跟随全局（默认，等于没覆盖）</summary>
    public int RamMode { get; set; } = 2;
    /// <summary>手动模式下的最大堆内存 (MB)</summary>
    public int MaxMemoryMb { get; set; }
    /// <summary>手动模式下的最小堆内存 (MB)，0 = 自动取 1/4</summary>
    public int MinMemoryMb { get; set; }

    /// <summary>专用 Java 路径。空 = 按版本要求的版本自动挑（不跟全局）</summary>
    public string JavaPath { get; set; } = "";
    /// <summary>额外 JVM 参数。空 = 跟随全局</summary>
    public string JvmArgs { get; set; } = "";
    /// <summary>额外游戏参数。空 = 跟随全局</summary>
    public string GameArgs { get; set; } = "";

    /// <summary>窗口宽，0 = 跟随全局</summary>
    public int WindowWidth { get; set; }
    /// <summary>窗口高，0 = 跟随全局</summary>
    public int WindowHeight { get; set; }
    /// <summary>全屏。-1 = 跟随全局，0 = 窗口，1 = 全屏</summary>
    public int FullScreen { get; set; } = -1;

    /// <summary>版本隔离: -1 = 跟随全局，其余对应 <see cref="IsolationMode"/></summary>
    public int Isolation { get; set; } = -1;

    /// <summary>启动游戏前要执行的命令（工作目录 = 游戏目录）</summary>
    public string PreLaunchCommand { get; set; } = "";
    /// <summary>等启动前命令执行完再启动游戏</summary>
    public bool PreLaunchWait { get; set; } = true;

    /// <summary>是否完全是空的（没有任何覆盖）, 空了就从字典里删掉，别让配置越攒越大</summary>
    public bool IsDefault =>
        RamMode == 2 && MaxMemoryMb == 0 && MinMemoryMb == 0
        && JavaPath.Length == 0 && JvmArgs.Length == 0 && GameArgs.Length == 0
        && WindowWidth == 0 && WindowHeight == 0 && FullScreen < 0
        && Isolation < 0 && PreLaunchCommand.Length == 0;
}

/// <summary>合成之后真正拿去启动的一套参数（全局值 + 版本覆盖值）</summary>
/// <remarks>record 而不是 class：启动链路里需要 <c>eff with { MaxMemoryMb = ... }</c>
/// 来应用"32 位 Java 降内存"这类运行时修正</remarks>
public sealed record EffectiveLaunch
{
    public int MaxMemoryMb { get; init; }
    public int MinMemoryMb { get; init; }
    public bool MemoryFromAuto { get; init; }
    public string JavaPath { get; init; } = "";
    public string JvmArgs { get; init; } = "";
    public string GameArgs { get; init; } = "";
    public int WindowWidth { get; init; }
    public int WindowHeight { get; init; }
    public bool FullScreen { get; init; }
    public IsolationMode Isolation { get; init; }
    public string PreLaunchCommand { get; init; } = "";
    public bool PreLaunchWait { get; init; } = true;
}

/// <summary>版本独立设置的读写 + 合并。启动和设置页共用同一份逻辑，避免两边算得不一样。</summary>
public static class VersionSettings
{
    /// <summary>取某版本的覆盖项（没有就返回默认对象，别写回字典）</summary>
    public static VersionOption Get(LauncherConfig cfg, string versionId)
    {
        if (string.IsNullOrEmpty(versionId))
            return new VersionOption();
        return cfg.VersionOptions.TryGetValue(versionId, out var o) ? o : new VersionOption();
    }

    /// <summary>写回覆盖项；全默认就删掉（保持 config.json 干净）</summary>
    public static void Set(LauncherConfig cfg, string versionId, VersionOption opt)
    {
        if (string.IsNullOrEmpty(versionId))
            return;
        if (opt.IsDefault)
            cfg.VersionOptions.Remove(versionId);
        else
            cfg.VersionOptions[versionId] = opt;
    }

    /// <summary>初始化该版本的独立设置（学 PCL 的"初始化版本独立设置"）</summary>
    public static void Reset(LauncherConfig cfg, string versionId) => cfg.VersionOptions.Remove(versionId);

    /// <summary>该版本有没有任何覆盖项</summary>
    public static bool HasOverride(LauncherConfig cfg, string versionId) =>
        !string.IsNullOrEmpty(versionId) && cfg.VersionOptions.ContainsKey(versionId);

    /// <summary>
    /// 按 mod 数量估算合适的内存上限（借鉴 PCL2 的分段思路）。
    /// mod 越多给越多，但不超过系统可用内存的 70%，最后向上对齐到 256MB。
    /// </summary>
    public static int AutoMaxMemory(int modCount, long availableMb)
    {
        // 基准 2 GB，每 70 个 mod 加 1 GB。
        // （PCL 是 1.5 GB + 每 90 个 1 GB；实测 60 多个 mod 的大型整合包只分到 2.2 GB 不够用，
        //  所以起步和斜率都调高了一点，仍然受系统可用内存的 70% 封顶）
        var targetGb = 2.0 + modCount / 70.0;
        var capMb = Math.Max(1024, (long)(availableMb * 0.7));
        var mb = (int)Math.Min(targetGb * 1024, capMb);
        mb = Math.Clamp(mb, 1024, 16384);
        return (mb + 255) / 256 * 256; // 向上取到 256 的倍数
    }

    /// <summary>系统可用内存 (MB)，用于自动分配</summary>
    public static long AvailableMemoryMb()
    {
        try
        {
            var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (bytes > 0)
                return bytes / 1024 / 1024;
        }
        catch
        {
            // 拿不到就按 8GB 估
        }
        return 8192;
    }

    /// <summary>把全局配置 + 版本覆盖合成一套生效参数</summary>
    public static EffectiveLaunch Resolve(LauncherConfig cfg, MinecraftVersion v, int modCount)
    {
        var o = Get(cfg, v.Id);
        var avail = AvailableMemoryMb();

        int maxMb, minMb;
        var auto = false;
        switch (o.RamMode)
        {
            case 0: // 自动
                maxMb = AutoMaxMemory(modCount, avail);
                minMb = Math.Max(512, maxMb / 4);
                auto = true;
                break;
            case 1: // 手动
                maxMb = o.MaxMemoryMb > 0 ? o.MaxMemoryMb : cfg.MaxMemoryMb;
                minMb = o.MinMemoryMb > 0 ? o.MinMemoryMb : Math.Max(512, maxMb / 4);
                break;
            default: // 跟随全局：全局自己也有自动/手动两种模式
                if (cfg.RamMode == 1)
                {
                    maxMb = cfg.MaxMemoryMb;
                    minMb = cfg.MinMemoryMb;
                }
                else
                {
                    maxMb = AutoMaxMemory(modCount, avail);
                    minMb = Math.Max(512, maxMb / 4);
                    auto = true;
                }
                break;
        }

        return new EffectiveLaunch
        {
            MaxMemoryMb = maxMb,
            MinMemoryMb = Math.Min(minMb, maxMb),
            MemoryFromAuto = auto,
            JavaPath = o.JavaPath.Length > 0 ? o.JavaPath : cfg.JavaPath,
            JvmArgs = o.JvmArgs.Length > 0 ? o.JvmArgs : cfg.JvmArgs,
            GameArgs = o.GameArgs.Length > 0 ? o.GameArgs : cfg.GameArgs,
            WindowWidth = o.WindowWidth > 0 ? o.WindowWidth : cfg.WindowWidth,
            WindowHeight = o.WindowHeight > 0 ? o.WindowHeight : cfg.WindowHeight,
            FullScreen = o.FullScreen >= 0 ? o.FullScreen == 1 : cfg.FullScreen,
            Isolation = o.Isolation >= 0 ? (IsolationMode)o.Isolation : cfg.Isolation,
            PreLaunchCommand = o.PreLaunchCommand,
            PreLaunchWait = o.PreLaunchWait,
        };
    }

    /// <summary>该版本的 mods 目录里有多少个启用的 mod（自动内存要按它估算）</summary>
    public static int CountMods(LauncherConfig cfg, MinecraftVersion v)
    {
        try
        {
            var dir = InstalledModsDir(cfg, v);
            if (!Directory.Exists(dir))
                return 0;
            return Directory.EnumerateFiles(dir)
                .Count(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".litemod", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 该版本实际的 mods 目录（和 GameLauncher 的隔离规则保持一致）：
    /// 隔离 → <c>versions/&lt;id&gt;/mods</c>；不隔离 → <c>.minecraft/mods</c>。
    /// </summary>
    public static string InstalledModsDir(LauncherConfig cfg, MinecraftVersion v)
    {
        var iso = Get(cfg, v.Id).Isolation;
        var mode = iso >= 0 ? (IsolationMode)iso : cfg.Isolation;
        var isolated = mode switch
        {
            IsolationMode.Always => true,
            IsolationMode.Modded => GameLauncher.IsModdedVersion(v),
            _ => false,
        };
        var gameDir = isolated ? Path.Combine(cfg.MinecraftPath, "versions", v.Id) : cfg.MinecraftPath;
        return Path.Combine(gameDir, "mods");
    }
}
