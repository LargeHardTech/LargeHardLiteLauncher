using System.Net;
using System.Net.Security;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LargeHardLiteLauncher.Core;

/// <summary>
/// 网络相关的公共设施: HttpClient 工厂 + 失败诊断。
///
/// 为什么要专门做诊断: <c>HttpRequestException</c> 的 Message 只有
/// "The SSL connection could not be established, see inner exception.", 真正原因
/// (证书链错误 / 域名不匹配 / 协议版本) 全在 InnerException 里, 不展开的话
/// 用户截图过来根本没法判断。
/// </summary>
public static class Net
{
    /// <summary>最近一次证书校验失败的具体错误 (诊断用; 多线程下最后一次的值)</summary>
    public static string? LastCertError { get; private set; }

    /// <summary>
    /// 建 HttpClient。
    /// </summary>
    /// <param name="ignoreSslErrors">
    /// 忽略证书校验错误（设置里的应急开关）。只在系统根证书缺失、
    /// 杀软中间人等无法修复的场景用，会牺牲安全性。
    /// </param>
    public static HttpClient CreateClient(TimeSpan timeout, bool ignoreSslErrors = false)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            SslOptions = new SslClientAuthenticationOptions
            {
                // 不依赖系统注册表里的"默认协议"（可能被优化工具改坏），
                // 明确允许 TLS 1.2/1.3。系统不支持的协议会被自动忽略。
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback =
                    (sender, cert, chain, errors) => RecordCertError(cert, errors, ignoreSslErrors),
            },
        };

        var http = new HttpClient(handler) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LargeHardLiteLauncher/0.1");
        return http;
    }

    /// <summary>记录证书校验结果；默认仍然拒绝，只有开了"忽略证书错误"才放行</summary>
    private static bool RecordCertError(X509Certificate? cert, SslPolicyErrors errors, bool ignore)
    {
        if (errors == SslPolicyErrors.None)
            return true;

        var detail = errors.ToString();
        if (cert is not null)
            detail += $" (证书主体 {cert.Subject})";
        LastCertError = ignore ? detail + " [已忽略]" : detail;

        return ignore;
    }

    /// <summary>HTTP 状态码 → 人话（502/503/504 都是服务端/中间设备的事，不是本地网络坏）</summary>
    public static string? HttpStatusHint(int status) => status switch
    {
        401 or 403 => "服务器拒绝访问(鉴权/区域限制)",
        404 => "文件在服务器上不存在(版本可能已被移除)",
        429 => "请求太频繁被限流,稍等再试",
        500 => "镜像/服务器内部错误,稍后重试或换源",
        502 or 503 or 504 => "网关错误:服务器或中间代理暂时不可用,稍后重试或换源",
        >= 500 => "服务器故障,稍后重试或换源",
        _ => null,
    };

    /// <summary>判断这次失败是不是 SSL/TLS 握手问题</summary>
    public static bool IsSslFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return true;
            if (e.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 把异常链展开成多行文本（真正原因在 InnerException 里）。
    /// </summary>
    public static string Describe(Exception ex, bool includeHint = true)
    {
        var sb = new StringBuilder();
        var e = ex;
        for (int depth = 0; e is not null && depth < 4; depth++)
        {
            if (depth > 0)
                sb.Append("\n  └─ ");
            sb.Append(Shorten(e.Message, 200));
            e = e.InnerException;
        }

        if (IsSslFailure(ex) && LastCertError is not null)
            sb.Append("\n  [证书] ").Append(LastCertError);

        if (includeHint && Hint(ex) is { } hint)
            sb.Append("\n\n").Append(hint);

        return sb.ToString();
    }

    /// <summary>弹窗用的精简版: 单行、总宽可控, 不会把 MessageBox 撑出屏幕</summary>
    public static string Brief(Exception ex)
    {
        // SSL 失败: 英文原文 ("The SSL connection could not be established...") 对用户没用,
        // 直接给中文归因, 详细步骤在任务日志里
        if (IsSslFailure(ex))
            return "SSL 握手失败 - " + CertReason() + " (排查建议见任务日志)";

        var sb = new StringBuilder(Shorten(ex.Message, 60));
        if (ex.InnerException is { } inner)
            sb.Append(" | ").Append(Shorten(inner.Message, 50));
        return sb.ToString();
    }

    /// <summary>把 SslPolicyErrors 翻译成一句人话</summary>
    public static string CertReason()
    {
        var e = LastCertError;
        if (e is null)
            return "连接被中断(可能是代理/杀软/网络问题)";
        if (e.Contains("ChainErrors", StringComparison.OrdinalIgnoreCase))
            return "证书链验证失败(根证书缺失,系统时间不准或被中间人拦截)";
        if (e.Contains("NameMismatch", StringComparison.OrdinalIgnoreCase))
            return "证书域名不匹配(可能被 DNS 劫持)";
        if (e.Contains("NotAvailable", StringComparison.OrdinalIgnoreCase))
            return "服务器没有返回证书(可能被劫持)";
        return Shorten(e, 60);
    }

    /// <summary>SSL 失败的排查建议（写成任务日志, 不放弹窗）</summary>
    public static string? Hint(Exception ex)
    {
        if (!IsSslFailure(ex))
            return null;

        return """
            SSL 握手失败, 常见原因 (按出现概率):
              1. 系统时间不准 —— 校准右下角日期/时间后重试
              2. 系统根证书缺失或过期 (Win7/8 常见) —— 走 Windows Update / 装根证书更新
              3. 杀毒软件、游戏加速器、公司代理拦截 HTTPS —— 临时关掉再试
              4. 运营商劫持 / DNS 污染镜像域名 —— 设置 → 下载源 换成「官方」再试
              自检: 用浏览器打开 https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json
                   能打开说明网络通, 打不开就是这个域名的证书/连接问题
            """;
    }

    /// <summary>从 URL 里取域名 (失败诊断用: 一眼看出是哪个域名挂了)</summary>
    public static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    /// <summary>
    /// 单行描述一次请求失败: 带域名 + HTTP 状态码归因 / SSL 归因。
    /// 之前只报 "Response status code does not indicate success: 502"，
    /// 既不知道哪个域名、也不知道该找谁, 完全没法排查。
    /// </summary>
    public static string DescribeHttp(Exception ex, string? url = null)
    {
        var at = url is null ? "" : $" @ {HostOf(url)}";

        var status = (ex as HttpRequestException)?.StatusCode is { } sc ? (int)sc : 0;
        if (status > 0)
        {
            var hint = HttpStatusHint(status);
            return $"HTTP {status}{at}" + (hint is null ? "" : $" - {hint}");
        }

        if (IsSslFailure(ex))
            return $"SSL 握手失败{at} - {CertReason()}";

        return Shorten(ex.Message, 100) + at;
    }

    private static string Shorten(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
