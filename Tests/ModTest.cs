using LargeHardLiteLauncher.Core;

namespace LargeHardLiteLauncher;

/// <summary>Modrinth / CurseForge 接口实测</summary>
internal static class ModTest
{
    public static async Task RunAsync(string query, string gameVersion, string loader)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        Console.Error.WriteLine($"[mod] === Modrinth 搜索 '{query}' (mod, {gameVersion}, {loader})");
        List<RemoteProject> hits;
        try
        {
            hits = await ModrinthApi.SearchAsync(query, AddonType.Mod, gameVersion, loader);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[mod] FAIL " + ex.Message);
            return;
        }
        Console.Error.WriteLine($"[mod] 命中 {hits.Count} 个");
        foreach (var h in hits.Take(5))
            Console.Error.WriteLine($"[mod]   {h.Title} ({h.Id}) {h.Downloads/10000}万 {h.Author}");

        if (hits.Count == 0)
            return;

        var first = hits[0];
        Console.Error.WriteLine($"[mod] === 取 {first.Title} 适用文件");
        var files = await ModrinthApi.GetFilesAsync(first.Id, gameVersion, loader);
        Console.Error.WriteLine($"[mod] 文件 {files.Count} 个");
        foreach (var f in files.Take(3))
        {
            Console.Error.WriteLine($"[mod]   {f.DisplayName}");
            Console.Error.WriteLine($"[mod]     {f.FileName}  {f.Size/1024/1024}MB  deps={string.Join(",", f.RequiredDependencies)}");
            Console.Error.WriteLine($"[mod]     {f.Url}");
        }
        if (files.Count > 0 && !string.IsNullOrEmpty(files[0].Sha1))
            Console.Error.WriteLine($"[mod]   sha1={files[0].Sha1}");

        // 详情页的做法: 不按版本过滤 (只看加载器), 应该能看到所有版本
        var allFiles = await ModrinthApi.GetFilesAsync(first.Id, null, loader);
        Console.Error.WriteLine($"[mod] 详情页(不限版本): {allFiles.Count} 个文件 (应远多于 {files.Count})");
        foreach (var f in allFiles.Take(3))
            Console.Error.WriteLine($"[mod]   {f.Display}");

        Console.Error.WriteLine("[mod] === 整合包搜索");
        var packs = await ModrinthApi.SearchAsync(query, AddonType.Modpack, gameVersion, null);
        Console.Error.WriteLine($"[mod] 整合包 {packs.Count} 个");
        foreach (var p2 in packs.Take(3))
            Console.Error.WriteLine($"[mod]   {p2.Title} ({p2.Id}) {p2.Downloads/10000}万");

        Console.Error.WriteLine("[mod] === CurseForge (需要 key)");
        try
        {
            var cf = await CurseForgeApi.SearchAsync(query, AddonType.Mod, gameVersion, loader, null, null, "", 0, 3);
            Console.Error.WriteLine($"[mod] CF 命中 {cf.Count}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mod] CF: {ex.Message}");
        }
    }
}
