using System.Text;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 启动参数模板引擎，把版本 json 里的 <c>${placeholder}</c> 替换成实际值。
/// 本机没有值的占位符（离线登录的 <c>${clientid}</c>、<c>${auth_xuid}</c>）不能替换成空串，
/// 否则 <c>--clientId --xuid 123</c> 会吃掉后面的参数；正确做法是丢掉整条参数，
/// 连同紧邻前面的 <c>--flag</c>。
/// </summary>
public static class ArgumentEngine
{
    /// <summary>
    /// 展开参数模板。
    /// </summary>
    /// <param name="template">模板 (含 ${...})</param>
    /// <param name="vars">占位符表; 值为 null 表示"本机不支持", 触发整参数丢弃</param>
    /// <param name="warnings">未知占位符告警 (原样保留, 方便排查新版本 json)</param>
    public static List<string> Expand(
        IReadOnlyList<string> template,
        IReadOnlyDictionary<string, string?> vars,
        List<string>? warnings = null)
    {
        var result = new List<string>(template.Count);

        foreach (var raw in template)
        {
            if (string.IsNullOrEmpty(raw))
                continue;

            var expanded = Substitute(raw, vars, warnings, out var dropIfMissing);
            if (dropIfMissing)
            {
                // 值缺失: 当前参数作废, 把前面的 --flag 也撤掉
                if (result.Count > 0 && IsFlag(result[^1]))
                    result.RemoveAt(result.Count - 1);
                continue;
            }
            if (expanded.Length > 0)
                result.Add(expanded);
        }

        return result;
    }

    /// <summary>老格式 (1.12 及以前) 的整串 minecraftArguments → 按空格拆</summary>
    public static List<string> SplitLegacy(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? new List<string>()
            : raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>看起来像命令行开关 (--xxx / -Dxxx)</summary>
    private static bool IsFlag(string s) =>
        s.Length > 1 && s[0] == '-';

    /// <summary>替换一个字符串里的所有 ${...}</summary>
    private static string Substitute(
        string input,
        IReadOnlyDictionary<string, string?> vars,
        List<string>? warnings,
        out bool dropIfMissing)
    {
        dropIfMissing = false;
        if (!input.Contains("${", StringComparison.Ordinal))
            return input;

        var sb = new StringBuilder(input.Length + 32);
        int i = 0;
        while (i < input.Length)
        {
            int start = input.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(input, i, input.Length - i);
                break;
            }

            sb.Append(input, i, start - i);
            int end = input.IndexOf('}', start + 2);
            if (end < 0)
            {
                // 没有闭合的 ${ → 原样保留剩下部分
                sb.Append(input, start, input.Length - start);
                break;
            }

            var key = input.Substring(start + 2, end - start - 2);
            if (vars.TryGetValue(key, out var value))
            {
                if (value is null)
                {
                    // 可选占位符但本机无值 → 整个参数丢弃
                    dropIfMissing = true;
                    return "";
                }
                sb.Append(value);
            }
            else
            {
                // 未知占位符: 原样留下, 便于发现新版本引入的变量
                warnings?.Add($"未知占位符 ${{{key}}}");
                sb.Append("${").Append(key).Append('}');
            }

            i = end + 1;
        }

        return sb.ToString();
    }
}
