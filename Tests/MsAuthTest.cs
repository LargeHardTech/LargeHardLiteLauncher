using LargeHardLiteLauncher.Core;

namespace LargeHardLiteLauncher;

/// <summary>
/// 微软登录链路的离线可测部分: client id 为空 / 非法时的错误文案,
/// 以及一个真实 device code 请求 (需要传入 client id)。
/// 完整登录 (浏览器输码) 必须人工参与, 没法自动化。
/// </summary>
internal static class MsAuthTest
{
    public static async Task RunAsync(string clientId)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        Console.Error.WriteLine("[ms] === client id 为空");
        try
        {
            await MicrosoftAuth.RequestDeviceCodeAsync("", CancellationToken.None);
            Console.Error.WriteLine("[ms] FAIL 应该抛异常");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ms] OK   {ex.Message}");
        }

        Console.Error.WriteLine("[ms] === client id 非法 (00000000402b5328)");
        try
        {
            await MicrosoftAuth.RequestDeviceCodeAsync("00000000402b5328", CancellationToken.None);
            Console.Error.WriteLine("[ms] FAIL 应该抛异常");
        }
        catch (Exception ex)
        {
            var first = ex.Message.Split('\n')[0];
            Console.Error.WriteLine($"[ms] OK   {first}");
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            Console.Error.WriteLine("[ms] 没给真实 client id, 跳过 device code 实测");
            Console.Error.WriteLine("[ms] 用法: --mstest <azure-client-id>");
            return;
        }

        Console.Error.WriteLine($"[ms] === 真实 device code 请求 (client_id={clientId})");
        try
        {
            var info = await MicrosoftAuth.RequestDeviceCodeAsync(clientId, CancellationToken.None);
            Console.Error.WriteLine($"[ms] OK   user_code={info.UserCode}");
            Console.Error.WriteLine($"[ms]      verify={info.VerificationUri}");
            Console.Error.WriteLine($"[ms]      interval={info.IntervalSec}s expires={info.ExpiresInSec}s");
            Console.Error.WriteLine($"[ms] 浏览器打开 {info.VerificationUri} 输入 {info.UserCode} 即可完成登录");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ms] FAIL {ex.Message}");
        }
    }
}
