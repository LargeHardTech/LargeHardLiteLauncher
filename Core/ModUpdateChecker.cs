using System.Security.Cryptography;

namespace LargeHardLiteLauncher.Core;

/// <summary>一个待更新的 mod：本地文件 → 线上最新文件</summary>
public sealed class ModUpdateInfo
{
    public ModInfo Mod { get; init; } = null!;
    public RemoteFile NewFile { get; init; } = null!;

    /// <summary>当前已装文件的 SHA1（小写）</summary>
    public string OldSha1 { get; init; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(Mod.Name) ? Mod.EnabledName : Mod.Name;

    /// <summary>列表显示：名字 + 老文件名 → 新文件名</summary>
    public string Display =>
        $"{DisplayName}   {Mod.FileName}  →  {NewFile.FileName}   [{NewFile.Size / 1024.0 / 1024.0:0.00} MB]";

    /// <summary>新文件名和旧的完全一样（只是内容变了），提示一下让用户知道是原地替换</summary>
    public bool SameFileName =>
        string.Equals(Mod.FileName, NewFile.FileName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Mod 更新检查（学 PCL 的「更新选中 Mod」）。
/// <para>
/// 原理：算每个已装 jar 的 SHA1 → 打 Modrinth <c>POST /v2/version_files/update</c>
/// （带当前加载器 + MC 版本做过滤）→ 返回的版本 hash 和自己不一样就说明有新版。
/// CurseForge 不参与：它的指纹库接口要 key 且限流狠，先只支持 Modrinth。
/// </para>
/// </summary>
public static class ModUpdateChecker
{
    private static readonly HttpClient Http = Net.CreateClient(TimeSpan.FromMinutes(5), ignoreSslErrors: false);

    /// <summary>
    /// 检查更新。<paramref name="loader"/> 传当前版本的加载器（forge/fabric/…），
    /// 传 null 表示不按加载器过滤。
    /// </summary>
    public static async Task<List<ModUpdateInfo>> CheckAsync(
        IReadOnlyList<ModInfo> mods, string? baseMc, string? loader,
        TaskItem? task, CancellationToken ct)
    {
        var result = new List<ModUpdateInfo>();

        // 只查 jar / litemod；.old 是老备份，跳过
        var targets = mods
            .Where(m => m.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                     || m.FileName.EndsWith(".litemod", StringComparison.OrdinalIgnoreCase)
                     || m.FileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
                     || m.FileName.EndsWith(".litemod.disabled", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (targets.Count == 0)
        {
            task?.LogLine("没有可检查的 jar");
            return result;
        }

        task?.LogLine($"计算 {targets.Count} 个文件的 SHA1...");
        var hashToMod = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        foreach (var m in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var sha1 = await Sha1OfAsync(m.FilePath, ct).ConfigureAwait(false);
                if (sha1 is not null)
                    hashToMod[sha1] = m; // 同一份文件装了两次就只认一个, 无所谓
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                task?.LogLine($"[跳过] {m.FileName}: {ex.Message}");
            }

            done++;
            if (done % 10 == 0 || done == targets.Count)
                task?.Report(0.45 * done / targets.Count);
        }

        if (hashToMod.Count == 0)
        {
            task?.LogLine("一个文件都没算出来,跳过");
            return result;
        }

        task?.LogLine($"向 Modrinth 查询 {hashToMod.Count} 个哈希" +
                      (loader is null ? "(不限加载器)" : $"(加载器 {loader})") +
                      (string.IsNullOrEmpty(baseMc) ? "" : $", MC {baseMc}"));
        var latest = await ModrinthApi
            .GetLatestVersionsAsync(hashToMod.Keys.ToList(), baseMc, loader, ct)
            .ConfigureAwait(false);
        task?.Report(0.9);

        var checkedCount = 0;
        foreach (var (hash, mod) in hashToMod)
        {
            if (!latest.TryGetValue(hash, out var file))
                continue; // 不在 Modrinth 上 / 这个版本被作者删了
            checkedCount++;

            // hash 相同 = 已经是最新版
            if (string.Equals(file.Sha1, hash, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(file.Url))
                continue;

            result.Add(new ModUpdateInfo { Mod = mod, NewFile = file, OldSha1 = hash });
        }

        task?.Report(1);
        task?.LogLine($"Modrinth 上认得 {checkedCount} 个,其中 {result.Count} 个有新版本");
        return result;
    }

    /// <summary>
    /// 执行更新：下载新文件 → 校验 SHA1 → 删旧文件 → 换成新名字（保持原来的启用/禁用状态）。
    /// 返回成功/失败数量，失败原因写进 <paramref name="firstError"/>。
    /// </summary>
    public static async Task<(int Ok, int Fail)> ApplyAsync(
        IReadOnlyList<ModUpdateInfo> updates, TaskItem? task, CancellationToken ct)
    {
        var ok = 0;
        var fail = 0;
        string? firstError = null;

        for (var i = 0; i < updates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var u = updates[i];
            var name = u.DisplayName;
            try
            {
                var dir = Path.GetDirectoryName(u.Mod.FilePath) ?? "";
                if (string.IsNullOrEmpty(dir))
                    throw new InvalidOperationException("算不出 mods 目录");

                // 目标名：沿用新文件名，但要带上原来的启用/禁用状态
                var targetName = u.NewFile.FileName;
                if (!u.Mod.Enabled && !targetName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    targetName += ".disabled";
                var targetPath = Path.Combine(dir, targetName);

                // 目标已存在且不是当前这个文件 → 说明目录里已经有同名新版了, 跳过防覆盖
                if (File.Exists(targetPath)
                    && !string.Equals(targetPath, u.Mod.FilePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"目标文件已存在: {targetName}");

                var tmp = targetPath + ".tmp";
                task?.LogLine($"下载 {u.NewFile.FileName}...");
                await DownloadToAsync(u.NewFile.Url, tmp, ct).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(u.NewFile.Sha1))
                {
                    var got = await Sha1OfAsync(tmp, ct).ConfigureAwait(false);
                    if (!string.Equals(got, u.NewFile.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(tmp);
                        throw new InvalidDataException("下载到的文件 SHA1 不匹配");
                    }
                }

                // 先替换/写入新文件, 再删旧文件, 顺序反过来万一崩了就两头空
                File.Move(tmp, targetPath, overwrite: false);
                if (!string.Equals(u.Mod.FilePath, targetPath, StringComparison.OrdinalIgnoreCase))
                    TryDelete(u.Mod.FilePath);

                ok++;
                task?.LogLine($"[ok] {name} → {targetName}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                fail++;
                firstError ??= $"{name}: {ex.Message}";
                task?.LogLine($"[失败] {name}: {ex.Message}");
            }

            task?.Report((double)(i + 1) / updates.Count);
        }

        if (firstError is not null)
            task?.LogLine("首个失败原因: " + firstError);

        return (ok, fail);
    }

    private static async Task DownloadToAsync(string url, string path, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(path);
        await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
    }

    /// <summary>算文件 SHA1（小写十六进制）；文件不存在返回 null</summary>
    public static async Task<string?> Sha1OfAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        var hash = await SHA1.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 删不掉就算了, 反正新文件已经就位
        }
    }
}
