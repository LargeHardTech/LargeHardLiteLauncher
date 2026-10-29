using System.Text;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 手拼 JSON 用的小工具。
/// <para>
/// 为什么不用 <c>JsonSerializer</c>：NativeAOT 下匿名类型走 <c>Serialize(object)</c> 会抛
/// <c>NotSupportedException</c>（匿名类型没有 source-gen 元数据），所以全项目凡是要发出去的
/// JSON 都是手拼的。手拼就必须自己保证转义正确, 所有动态字符串一律过 <see cref="Str"/>。
/// </para>
/// </summary>
public static class JsonText
{
    /// <summary>把字符串包成 JSON 字面量（带引号 + 转义）。null 输出 <c>null</c>。</summary>
    public static string Str(string? s)
    {
        if (s is null)
            return "null";

        var sb = new StringBuilder(s.Length + 8);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    // 其它控制字符一律转成 \u00XX
                    if (c < 0x20)
                        sb.Append("\\u00").Append(((int)c).ToString("x2"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>把一组字符串拼成 JSON 数组字面量。</summary>
    public static string StrArray(IEnumerable<string?> items) =>
        "[" + string.Join(",", items.Select(Str)) + "]";
}
