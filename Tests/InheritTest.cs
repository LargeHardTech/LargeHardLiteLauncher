using LargeHardLiteLauncher.Core;

namespace LargeHardLiteLauncher;

/// <summary>
/// inheritsFrom 合并的独立实测: 临时造一个「父版本 + Forge 子版本 + 三层继承」的假 .minecraft,
/// 扫一遍验证小版本能不能正确继承 mainClass / libraries / arguments / client jar。
/// 真机上 Forge 都被 PCL2 合成单 json 了, 没样本可测, 所以自己造。
/// </summary>
internal static class InheritTest
{
    public static void Run()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        var root = Path.Combine(Path.GetTempPath(), "lht-inherittest");
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        var mc = Path.Combine(root, ".minecraft");
        foreach (var id in new[] { "1.20.1", "ForgeTest", "ForgeTest2" })
            Directory.CreateDirectory(Path.Combine(mc, "versions", id));

        // ---------------- 父版本 (仿原版 1.20.1) ----------------
        var parent = """
        {
          "id": "1.20.1",
          "type": "release",
          "mainClass": "net.minecraft.client.main.Main",
          "javaVersion": { "majorVersion": 17 },
          "assets": "5",
          "assetIndex": { "id": "5" },
          "arguments": {
            "jvm": [
              "-Djava.library.path=${natives_directory}",
              "-cp",
              "${classpath}"
            ],
            "game": [
              "--username", "${auth_player_name}",
              "--version", "${version_name}",
              "--gameDir", "${game_directory}",
              "--assetsDir", "${assets_root}",
              "--assetIndex", "${assets_index_name}",
              "--uuid", "${auth_uuid}",
              "--accessToken", "${auth_access_token}",
              "--versionType", "${version_type}",
              { "rules": [ { "action": "allow", "features": { "has_custom_resolution": true } } ],
                "value": [ "--width", "${resolution_width}", "--height", "${resolution_height}" ] }
            ]
          },
          "libraries": [
            { "name": "org.example:parent-lib:1.0",
              "downloads": { "artifact": { "path": "org/example/parent-lib/1.0/parent-lib-1.0.jar" } } }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(mc, "versions", "1.20.1", "1.20.1.json"), parent);
        File.WriteAllBytes(Path.Combine(mc, "versions", "1.20.1", "1.20.1.jar"), Array.Empty<byte>());

        // ---------------- 中层 (Forge 假版本, inheritsFrom 父) ----------------
        var child = """
        {
          "id": "ForgeTest",
          "inheritsFrom": "1.20.1",
          "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
          "arguments": {
            "game": [ "--launchTarget", "forgeclient", "--fml.forgeVersion", "47.2.0" ],
            "jvm": [ "-Dfml.ignoreInvalidMinecraftCertificates=true" ]
          },
          "libraries": [
            { "name": "net.minecraftforge:forge:47.2.0",
              "downloads": { "artifact": { "path": "net/minecraftforge/forge/47.2.0/forge-47.2.0.jar" } } }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(mc, "versions", "ForgeTest", "ForgeTest.json"), child);

        // ---------------- 三层 (ForgeTest2 → ForgeTest → 1.20.1) ----------------
        var grand = """
        {
          "id": "ForgeTest2",
          "inheritsFrom": "ForgeTest",
          "arguments": { "game": [ "--extraFlag", "deep" ] },
          "libraries": [
            { "name": "org.example:deep-lib:2.0",
              "downloads": { "artifact": { "path": "org/example/deep-lib/2.0/deep-lib-2.0.jar" } } }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(mc, "versions", "ForgeTest2", "ForgeTest2.json"), grand);

        // ---------------- 扫描 + 验证 ----------------
        var vs = VersionScanner.Scan(mc);
        Console.Error.WriteLine($"[inherit] 扫到 {vs.Count} 个版本: {string.Join(", ", vs.Select(x => x.Id))}");

        int fail = 0;
        void Check(string label, bool ok, string detail)
        {
            Console.Error.WriteLine($"[inherit] {(ok ? "OK  " : "FAIL")} {label}: {detail}");
            if (!ok) fail++;
        }

        var c1 = vs.FirstOrDefault(x => x.Id == "ForgeTest");
        if (c1 is null)
        {
            Console.Error.WriteLine("[inherit] FAIL 没扫到 ForgeTest");
            return;
        }

        Check("mainClass 用子的", c1.MainClass == "cpw.mods.bootstraplauncher.BootstrapLauncher", c1.MainClass ?? "null");
        Check("javaVersion 继承", c1.RequiredJava == 17, c1.RequiredJava.ToString());
        Check("assetIndex 继承", c1.AssetIndexId == "5", c1.AssetIndexId ?? "null");
        Check("client jar 用父的", c1.ClientJar?.EndsWith("1.20.1.jar") == true, c1.ClientJar ?? "null");
        Check("libraries = 父+子", c1.Libraries.Count == 2,
            $"{c1.Libraries.Count}: {string.Join(" | ", c1.Libraries.Select(l => l.Name))}");
        Check("库顺序 父在前", c1.Libraries.Count > 0 && c1.Libraries[0].Name.Contains("parent-lib"),
            c1.Libraries.FirstOrDefault()?.Name ?? "null");
        Check("game 参数 父+子", c1.GameTemplate.Count == 24, // 父 21 + 子 3
            $"{c1.GameTemplate.Count}: {string.Join(" ", c1.GameTemplate)}");
        Check("game 参数 子的在最后", c1.GameTemplate.Count >= 3 && c1.GameTemplate[^1] == "47.2.0",
            c1.GameTemplate.LastOrDefault() ?? "null");
        Check("jvm 参数 父+子", c1.JvmTemplate.Count == 4,
            $"{c1.JvmTemplate.Count}: {string.Join(" ", c1.JvmTemplate)}");

        // 展开成真实命令行, 看两层参数是否都在
        var cfg = new LauncherConfig { PlayerName = "PeterTest", MinecraftPath = mc, WindowWidth = 1920, WindowHeight = 1080 };
        var eff = VersionSettings.Resolve(cfg, c1, VersionSettings.CountMods(cfg, c1));
        var game = GameLauncher.BuildGameArgs(c1, cfg, eff, mc, mc);
        var jvm = GameLauncher.BuildJvmArgs(c1, cfg, eff, Path.Combine(c1.Dir, "natives"), "dummy.jar", 17);
        Check("展开后含 forgeclient", game.Contains("forgeclient"), string.Join(" ", game));
        Check("展开后含 username", game.Contains("PeterTest"), "ok=" + game.Contains("PeterTest"));
        Check("展开后含 width 1920", game.Contains("1920"), "ok=" + game.Contains("1920"));
        Check("展开后含 fml.ignore", jvm.Any(a => a.Contains("fml.ignoreInvalidMinecraftCertificates")), "ok");
        Check("展开后无重复 -cp", jvm.Count(a => a is "-cp") == 1, jvm.Count(a => a is "-cp").ToString());

        // 三层继承
        var c2 = vs.FirstOrDefault(x => x.Id == "ForgeTest2");
        if (c2 is not null)
        {
            Check("三层: mainClass 传到最底", c2.MainClass == "cpw.mods.bootstraplauncher.BootstrapLauncher", c2.MainClass ?? "null");
            Check("三层: libraries = 3", c2.Libraries.Count == 3,
                string.Join(" | ", c2.Libraries.Select(l => l.Name)));
            Check("三层: game 含 deep", c2.GameTemplate.Contains("deep"), string.Join(" ", c2.GameTemplate));
        }
        else
        {
            Check("三层版本被扫到", false, "ForgeTest2 缺失");
        }

        Console.Error.WriteLine($"[inherit] ===== {(fail == 0 ? "全部通过" : $"{fail} 项失败")} =====");
        try { Directory.Delete(root, true); } catch { }
    }
}
