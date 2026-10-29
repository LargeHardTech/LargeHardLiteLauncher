using System.Text.Json;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 版本 json 里 rules 数组的求值 (os.name / os.arch / os.version / features)。
/// libraries 和 arguments 共用同一套语义, 集中在这里避免两处实现漂移。
/// </summary>
public static class JsonRules
{
    /// <summary>当前进程架构对应的 os.arch 标识</summary>
    private static readonly string Arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => "x86_64",
    };

    /// <summary>
    /// 我们声明自己支持的 features。原版 json 用 features 决定要不要带
    /// --demo / --width / --height / --quickPlay* 这些参数。
    /// </summary>
    private static bool FeatureEnabled(string name) => name switch
    {
        // 我们允许用户设置窗口尺寸 → 让 json 里的 --width/--height 生效
        "has_custom_resolution" => true,
        // 其余 (demo / quickPlay / 服务器独占) 一律不支持
        _ => false,
    };

    /// <summary>
    /// 判断带 rules 的对象是否适用于本机。无 rules → 适用。
    /// 语义 (官方规范): 初始 false, 逐条匹配, 匹配则覆盖为 action 的结果。
    /// </summary>
    public static bool Allow(JsonElement owner)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty("rules", out var rules) ||
            rules.ValueKind != JsonValueKind.Array)
            return true;

        bool allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (!RuleMatches(rule))
                continue;
            var action = GetString(rule, "action");
            allowed = action == "allow";
        }
        return allowed;
    }

    /// <summary>单条 rule 的 os / features 是否都匹配 (没有对应字段视为匹配)</summary>
    private static bool RuleMatches(JsonElement rule)
    {
        if (rule.ValueKind != JsonValueKind.Object)
            return false;

        if (rule.TryGetProperty("os", out var os) && os.ValueKind == JsonValueKind.Object)
        {
            var name = GetString(os, "name");
            if (name is not null && name != "windows")
                return false;

            var arch = GetString(os, "arch");
            if (arch is not null && arch != Arch)
                return false;

            if (os.TryGetProperty("versionRange", out var range) && range.ValueKind == JsonValueKind.Object)
            {
                var cur = Environment.OSVersion.Version;
                var min = GetString(range, "min");
                var max = GetString(range, "max");
                if (min is not null && Version.TryParse(min, out var vmin) && cur < vmin)
                    return false;
                if (max is not null && Version.TryParse(max, out var vmax) && cur > vmax)
                    return false;
            }
        }

        if (rule.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Object)
        {
            foreach (var f in features.EnumerateObject())
            {
                if (f.Value.ValueKind != JsonValueKind.True)
                    continue;
                if (!FeatureEnabled(f.Name))
                    return false;
            }
        }

        return true;
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
