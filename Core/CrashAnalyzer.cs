namespace LargeHardLiteLauncher.Core;

/// <summary>一条崩溃诊断：说人话的结论 + 日志证据 + 该怎么办</summary>
public sealed class CrashDiagnosis
{
    public string Title { get; init; } = "";
    /// <summary>触发这条结论的日志行（截断）</summary>
    public string Evidence { get; init; } = "";
    /// <summary>命中行后面几行里有用的内容（比如缺哪个前置 mod）</summary>
    public string Context { get; init; } = "";
    public string Advice { get; init; } = "";

    public string Render(int index)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{index}. {Title}");
        if (Evidence.Length > 0)
            sb.AppendLine($"   证据:{Evidence}");
        if (Context.Length > 0)
            sb.AppendLine($"   细节:{Context}");
        sb.Append($"   处理:{Advice}");
        return sb.ToString();
    }
}

/// <summary>游戏启动失败（带诊断结果）。UI 层认出这个类型就弹详细分析框。</summary>
public sealed class GameCrashException : Exception
{
    public IReadOnlyList<CrashDiagnosis> Diagnosis { get; }

    public GameCrashException(string message, IReadOnlyList<CrashDiagnosis> diagnosis) : base(message)
        => Diagnosis = diagnosis;

    /// <summary>给弹窗用的完整文本</summary>
    public string RenderReport()
    {
        if (Diagnosis.Count == 0)
            return Message;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < Diagnosis.Count; i++)
        {
            if (i > 0)
                sb.AppendLine();
            sb.AppendLine(Diagnosis[i].Render(i + 1));
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// 崩溃分析（学 PCL2 的招牌功能）：拿日志里的 Java 异常去匹配一组已知特征，
/// 把 `java.lang.module.ResolutionException` 这种天书翻译成「哪出问题 + 怎么办」。
///
/// 设计原则：
/// 1. 只做**高置信度**的判断，拿不准就不报（宁可少报也别误导）
/// 2. 每条规则只报一次，按 Rules 数组的顺序输出（越靠前越常见）
/// 3. 纯字符串匹配，不联网、不猜
/// </summary>
public static class CrashAnalyzer
{
    private sealed record Rule(string[] AnyOf, string Title, string Advice);

    private static readonly Rule[] Rules =
    {
        new(new[] { "java.lang.module.ResolutionException" },
            "加载器模块冲突(两个 jar 抢同一个包)",
            "版本文件不完整或加载器安装异常。请先到[版本设置 → 补全版本]检查;" +
            "还不行就删掉这个版本重装加载器。"),

        new(new[] { "UnsupportedClassVersionError", "has been compiled by a more recent version of the Java Runtime" },
            "Java 版本太低(游戏/加载器是用更新版本的 Java 编译的)",
            "改用更高版本的 Java:1.20.5 及以上需 21,1.18 及以上需 17,1.17 需 16,更早版本需 8。" +
            "可在[设置 → Java]中手动指定,或保持自动选择。"),

        new(new[] { "Unsupported JNI version" },
            "Java 版本太新(加载器的原生库跟不上)",
            "例如 Forge 1.20.1 搭配 Java 25 即会出现此问题,请改用其要求的版本(1.20.1 对应 Java 17)。"),

        new(new[] { "OutOfMemoryError", "Java heap space" },
            "内存不足",
            "请在[版本设置 → 设置]中增大内存。若已超过物理内存的一半仍不足,通常是 Mod 数量过多或存在内存泄漏。"),

        new(new[] { "Missing or unsupported mandatory dependencies", "Missing dependency" },
            "缺前置 Mod",
            "日志[细节]中会列出缺失项及其依赖来源。请在[下载 → Mod]中搜索并安装,注意版本与加载器需匹配。"),

        new(new[] { "Duplicate mods found", "Found a duplicate mod" },
            "mods 目录里有重复的 mod",
            "同一 Mod 存在多个版本。请在[版本设置 → Mod 管理]中删除旧版本或重复项。"),

        new(new[] { "Mixin apply failed", "InvalidMixinException", "MixinApplyError" },
            "某个 mod 的 Mixin 注入失败(mod 与游戏版本不兼容)",
            "根据[细节]中的 Mod 名称删除该 Mod,或更换为适配当前版本的文件。常见于将高版本 Mod 用于低版本。"),

        new(new[] { "NoClassDefFoundError", "ClassNotFoundException" },
            "缺类(库文件缺失或版本不完整)",
            "请在[版本设置 → 补全版本]中检查并补全。若刚安装 Mod,也可能是该 Mod 缺少前置。"),

        new(new[] { "GLFW error", "Failed to create window", "Pixel format not accelerated", "WGL: Failed" },
            "显卡/OpenGL 问题(创建窗口失败)",
            "请先更新显卡驱动。使用核显的笔记本还需在显卡控制面板中确认游戏使用独立显卡。"),

        new(new[] { "Could not find or load main class" },
            "找不到主类(版本 json 或加载器不完整)",
            "版本 json 指向的主类不存在。请在[版本设置 → 补全版本]中检查,或重新安装加载器。"),

        new(new[] { "There is no Minecraft launcher profile" },
            "Forge 安装器找不到启动器档案",
            "Forge 安装器要求 .minecraft 中存在 launcher_profiles.json。本启动器安装加载器时会自动创建;" +
            "出现此错误通常是手动运行了 installer。"),

        new(new[] { "EXCEPTION_ACCESS_VIOLATION", "SIGSEGV" },
            "JVM / 原生库崩溃(访问冲突)",
            "通常由 Java 与系统或驱动不匹配导致。建议先更换 Java 版本,其次更新显卡驱动。"),

        new(new[] { "Invalid maximum heap size", "Invalid initial heap size" },
            "内存参数写错了",
            "请在[版本设置 → 设置]中将内存模式改为[跟随全局]或[自动],并在自定义 JVM 参数中移除 -Xmx/-Xms。"),

        new(new[] { "AuthenticationException", "Failed to verify username", "Invalid credentials" },
            "登录凭据失效",
            "请重新登录账户(微软账户可能已过期;离线账户请检查名称是否合法)。"),

        new(new[] { "Crash report saved to" },
            "游戏生成了崩溃报告",
            "完整堆栈位于 crash-reports 目录(路径见[细节]),反馈问题时请附上该文件。"),
    };

    /// <summary>分析日志。没命中任何规则就返回空列表（这时只说明"没识别出原因"）</summary>
    public static List<CrashDiagnosis> Analyze(string logText, int exitCode)
    {
        var result = new List<CrashDiagnosis>();
        if (string.IsNullOrWhiteSpace(logText))
            return result;

        var lines = logText.Split('\n');
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in Rules)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0)
                    continue;
                if (!rule.AnyOf.Any(p => line.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (!seen.Add(rule.Title))
                    break;

                result.Add(new CrashDiagnosis
                {
                    Title = rule.Title,
                    Evidence = Clip(line),
                    Context = CollectContext(lines, i),
                    Advice = rule.Advice,
                });
                break;
            }
        }

        return result;
    }

    /// <summary>命中行之后的几条有用上下文（缺前置时 mod 名在下面几行）</summary>
    private static string CollectContext(string[] lines, int hitIndex)
    {
        var picked = new List<string>();
        for (var i = hitIndex + 1; i < lines.Length && i <= hitIndex + 12; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0)
                continue;
            // 去掉我们自己的时间戳前缀，保留 Java 自己的内容
            picked.Add(t.Length > 160 ? t[..160] + "..." : t);
            if (picked.Count >= 3)
                break;
        }
        return string.Join(" / ", picked);
    }

    private static string Clip(string line)
    {
        var t = line.Trim();
        return t.Length > 200 ? t[..200] + "..." : t;
    }
}
