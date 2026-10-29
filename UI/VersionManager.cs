using System.Collections.ObjectModel;
using LargeHardLiteLauncher.Core;
using Terminal.Gui;

namespace LargeHardLiteLauncher.UI;

/// <summary>
/// 版本管理页（学 PCL2 的 PageInstance）：全屏对话框，左侧导航 + 右侧内容。
/// 导航项：概览 / 设置（版本独立设置）/ Mod 管理 / 补全版本。
/// </summary>
public partial class LaunchPage
{
    private static readonly string[] NavItems = { "概览", "设置", "Mod 管理", "资源包", "光影", "补全版本" };

    /// <summary>当前打开的版本管理对话框。仅供 --vmtest 冒烟用，业务逻辑不读它</summary>
    internal static Dialog? OpenManagerDialog { get; private set; }

    /// <summary>打开版本管理页</summary>
    private void ShowVersionManager(MinecraftVersion ver)
    {
        var all = VersionScanner.Scan(Config.MinecraftPath);
        // 传进来的可能是旧对象，重新扫一次拿最新的（用户可能刚装完 mod / 补全过）
        ver = all.FirstOrDefault(v => v.Id == ver.Id) ?? ver;

        var dlg = new Dialog
        {
            Title = $"版本管理 · {ver.Id}",
            Width = Dim.Fill(-1),   // 阴影占一列一行, 不加会透过阴影看到边框
            Height = Dim.Fill(-1),
            BorderStyle = LineStyle.Single,
        };

        // ---- 左侧导航 ----
        var nav = new ListView<string>
        {
            X = 0,
            Y = 0,
            Width = 14,
            Height = Dim.Fill(2),
        };
        var navSource = new ObservableCollection<string>(NavItems);
        nav.SetSource(navSource);
        nav.SelectedItem = navSource[0];

        // ---- 右侧内容容器（每次切换整体重建）----
        var content = new View
        {
            X = Pos.Right(nav) + 1,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };

        var close = new Button
        {
            Text = "关闭",
            X = Pos.Center(),
            Y = Pos.AnchorEnd(2),
            IsDefault = false,
        };
        close.Accepting += (_, _) => dlg.RequestStop();

        void Render()
        {
            content.RemoveAll();
            var idx = nav.SelectedItem is { } sel ? navSource.IndexOf(sel) : 0;
            switch (idx)
            {
                case 1: BuildSetupPage(content, ver, Render); break;
                case 2: BuildModPage(content, ver, Render); break;
                case 3: BuildPackPage(content, ver, shader: false); break;
                case 4: BuildPackPage(content, ver, shader: true); break;
                case 5: BuildRepairPage(content, ver); break;
                default: BuildOverviewPage(content, ver, () => dlg.RequestStop()); break;
            }
            content.SetNeedsLayout();
            content.SetNeedsDraw();
        }

        nav.ValueChanged += (_, _) => Render();
        dlg.Add(nav, content, close);
        nav.SetFocus();
        Render();

        OpenManagerDialog = dlg;
        Application.Run(dlg);
        OpenManagerDialog = null;
        dlg.Dispose();

        // 关掉后启动页的版本信息可能变了（比如改了隔离模式）
        RefreshVersionList();
    }

    // ================= 概览 =================

    private void BuildOverviewPage(View content, MinecraftVersion ver, Action onDeleted)
    {
        var opt = VersionSettings.Get(Config, ver.Id);
        var modCount = VersionSettings.CountMods(Config, ver);
        var eff = VersionSettings.Resolve(Config, ver, modCount);
        var modsDir = VersionSettings.InstalledModsDir(Config, ver);
        var gameDir = GameDirOf(ver);

        var lines = new List<(string Key, string Value)>
        {
            ("版本名", ver.Id),
            ("类型", ver.Type),
            ("加载器", LoaderDisplay(ver)),
            ("主类", ver.MainClass ?? "(缺失)"),
            ("需要 Java", ver.RequiredJava > 0 ? ver.RequiredJava.ToString() : "未知"),
            ("实际使用", DescribeJava(eff, ver)),
            ("内存", $"{eff.MinMemoryMb}~{eff.MaxMemoryMb} MB ({RamSourceName(opt)})"),
            ("窗口", $"{eff.WindowWidth}x{eff.WindowHeight}{(eff.FullScreen ? " 全屏" : "")}"),
            ("版本隔离", IsolationName(eff.Isolation, ver)),
            ("独立设置", VersionSettings.HasOverride(Config, ver.Id) ? "已启用([设置]页可改)" : "全部跟随全局"),
            ("版本目录", ver.Dir),
            ("游戏目录", gameDir),
            ("mods", modsDir),
            ("Mod 数量", $"{modCount} 个已启用"),
        };

        var y = 0;
        foreach (var (key, value) in lines)
        {
            content.Add(new Label
            {
                Text = $"{key,-10}",
                X = 0,
                Y = y,
                Width = 11,
            });
            content.Add(new Label
            {
                Text = value,
                X = 11,
                Y = y,
                Width = Dim.Fill(),
            });
            y++;
        }

        // ---- 底部: 第一行开文件夹，第二行导出/删除 ----
        var openGameBtn = new Button { Text = "根目录", X = 0, Y = Pos.AnchorEnd(5), NoPadding = true, Width = 12 };
        var openModsBtn = new Button { Text = "mods", X = Pos.Right(openGameBtn) + 2, Y = Pos.AnchorEnd(5), NoPadding = true, Width = 12 };
        var openSavesBtn = new Button { Text = "存档", X = Pos.Right(openModsBtn) + 2, Y = Pos.AnchorEnd(5), NoPadding = true, Width = 12 };
        var openPacksBtn = new Button { Text = "资源包", X = Pos.Right(openSavesBtn) + 2, Y = Pos.AnchorEnd(5), NoPadding = true, Width = 12 };
        var openShadersBtn = new Button { Text = "光影", X = Pos.Right(openPacksBtn) + 2, Y = Pos.AnchorEnd(5), NoPadding = true, Width = 12 };
        var openDataBtn = new Button { Text = "数据包", X = Pos.Right(openShadersBtn) + 2, Y = Pos.AnchorEnd(5), NoPadding = true, Width = 12 };

        var exportBtn = new Button { Text = "导出整合包", X = 0, Y = Pos.AnchorEnd(3), NoPadding = true, Width = 16 };
        var deleteBtn = new Button { Text = "删除版本", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(3), NoPadding = true, Width = 14 };

        openGameBtn.Accepting += (_, _) => OpenFolder(gameDir);
        openModsBtn.Accepting += (_, _) => OpenFolder(modsDir);
        openSavesBtn.Accepting += (_, _) => OpenFolder(Path.Combine(gameDir, "saves"));
        openPacksBtn.Accepting += (_, _) => OpenFolder(Path.Combine(gameDir, "resourcepacks"));
        openShadersBtn.Accepting += (_, _) => OpenFolder(Path.Combine(gameDir, "shaderpacks"));
        openDataBtn.Accepting += (_, _) => OpenFolder(Path.Combine(gameDir, "datapacks"));
        exportBtn.Accepting += (_, _) => ShowExportDialog(ver);
        deleteBtn.Accepting += (_, _) =>
        {
            // 删版本真的会丢存档和 mod，确认框必须把路径摆出来
            var r = MessageBox.Query(Application.Instance, $"删除版本 {ver.Id}",
                $"将删除整个版本目录,包括该版本的存档,Mod 与配置:\n\n{ver.Dir}\n\n此操作不可撤销。",
                "删除", "取消");
            if (r != 0)
                return;

            try
            {
                Directory.Delete(ver.Dir, recursive: true);
                if (string.Equals(Config.SelectedVersion, ver.Id, StringComparison.OrdinalIgnoreCase))
                {
                    Config.SelectedVersion = "";
                    Config.Save();
                }
                LauncherLog.Info($"已删除版本: {ver.Id} @ {ver.Dir}");
                MessageBox.Query(Application.Instance, "删除版本", $"已删除版本[{ver.Id}]。", "好");
                DownloadPage.InvalidateVersionCache();
                onDeleted();
            }
            catch (Exception ex)
            {
                LauncherLog.Error($"删除版本 {ver.Id} 失败: {ex}");
                MessageBox.ErrorQuery(Application.Instance, "删除失败",
                    $"{ex.Message}\n\n(文件被占用时同样会失败,请先关闭游戏)", "好");
            }
        };

        content.Add(openGameBtn, openModsBtn, openSavesBtn, openPacksBtn, openShadersBtn, openDataBtn,
            exportBtn, deleteBtn);
    }

    /// <summary>
    /// 导出整合包：先问包名/版本，再选保存位置，然后后台打包成 .mrpack。
    /// 导出的包能被本启动器的整合包安装（以及 PCL/HMCL 的 mrpack 导入）直接装回去。
    /// </summary>
    private void ShowExportDialog(MinecraftVersion ver)
    {
        var modsDir = VersionSettings.InstalledModsDir(Config, ver);
        var gameDir = Path.GetDirectoryName(modsDir) ?? ver.Dir;
        var (kind, loaderVer) = ModpackExporter.LoaderInfoOf(ver);
        var baseMc = VersionHelper.ResolveBaseVersion(ver, VersionScanner.Scan(Config.MinecraftPath));
        var modCount = VersionSettings.CountMods(Config, ver);

        var defaultName = ver.Id;
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var safeDefault = new string(defaultName.Where(c => !"\\/:*?\"<>|".Contains(c)).ToArray());
        var defaultOut = Path.Combine(desktop, safeDefault + ".mrpack");

        var dlg = new Dialog
        {
            Title = "导出整合包",
            Width = 84,
            Height = 20,
            BorderStyle = LineStyle.Single,
        };

        dlg.Add(new Label { Text = $"来源     {ver.Id}", X = 1, Y = 1, Width = Dim.Fill(2) });
        dlg.Add(new Label
        {
            Text = $"依赖     MC {baseMc}{(kind is null ? "（无加载器）" : $" + {kind} {loaderVer}")}",
            X = 1, Y = 2, Width = Dim.Fill(2),
        });
        dlg.Add(new Label
        {
            Text = $"内容     {modCount} 个 Mod,以及配置,资源包等(不含日志,存档,截图)",
            X = 1, Y = 3, Width = Dim.Fill(2),
        });
        dlg.Add(new Label
        {
            Text = $"目录     {gameDir}",
            X = 1, Y = 4, Width = Dim.Fill(2),
        });
        dlg.Add(new Label
        {
            Text = "导出格式为 Modrinth(.mrpack)。联网匹配成功的 Mod 只记录链接,可显著减小体积。",
            X = 1, Y = 6, Width = Dim.Fill(2),
        });

        dlg.Add(new Label { Text = "包名", X = 1, Y = 8 });
        var nameField = new TextField { Text = defaultName, X = 7, Y = 8, Width = Dim.Fill(2) };
        dlg.Add(new Label { Text = "版本", X = 1, Y = 10 });
        var verField = new TextField { Text = "1.0.0", X = 7, Y = 10, Width = 24 };

        dlg.Add(new Label { Text = "保存到", X = 1, Y = 12 });
        var pathField = new TextField { Text = defaultOut, X = 9, Y = 12, Width = Dim.Fill(14) };
        var browseBtn = new Button
        {
            Text = "浏览...",
            X = Pos.Right(pathField) + 1,
            Y = 12,
            NoPadding = true,
            Width = 12,
        };
        browseBtn.Accepting += (_, _) =>
        {
            var sd = new SaveDialog
            {
                Title = "导出到...",
                Path = pathField.Text ?? defaultOut,
            };
            Application.Run(sd);
            if (sd.Canceled != true && !string.IsNullOrWhiteSpace(sd.Path))
                pathField.Text = sd.Path;
            sd.Dispose();
        };

        var onlineCheck = new CheckBox
        {
            Text = "联网匹配在线项目(Modrinth),匹配成功的 Mod 不打包",
            X = 1,
            Y = 14,
            Value = CheckState.Checked,
        };

        var ok = new Button { Text = "导出", X = 1, Y = 16, IsDefault = true };
        var cancel = new Button { Text = "取消", X = Pos.Right(ok) + 2, Y = 16 };

        string? packName = null, packVer = null, outPath = null;
        var matchOnline = true;
        ok.Accepting += (_, _) =>
        {
            var n = (nameField.Text ?? "").Trim();
            if (n.Length == 0 || n.Any(c => "\\/:*?\"<>|".Contains(c)))
                return; // 名字不合法，不关框
            var p = (pathField.Text ?? "").Trim();
            if (p.Length == 0)
                p = defaultOut;
            packName = n;
            packVer = (verField.Text ?? "").Trim();
            if (packVer.Length == 0)
                packVer = "1.0.0";
            outPath = p;
            matchOnline = onlineCheck.Value == CheckState.Checked;
            dlg.RequestStop();
        };
        cancel.Accepting += (_, _) => dlg.RequestStop();

        dlg.Add(nameField, verField, pathField, browseBtn, onlineCheck, ok, cancel);
        nameField.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();

        if (packName is null || outPath is null)
            return;

        if (!outPath.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            outPath += ".mrpack";

        var finalName = packName;
        var finalVer = packVer!;
        var finalPath = outPath;
        var finalOnline = matchOnline;
        TaskManager.Submit($"导出整合包 {finalName}", async (task, ct) =>
        {
            try
            {
                var result = await ModpackExporter.ExportAsync(
                    Config, ver, finalPath, finalName, finalVer, finalOnline, task, ct).ConfigureAwait(false);
                Application.Invoke(() => MessageBox.Query(Application.Instance, "导出整合包",
                    $"导出完成:\n{result.Path}\n\n" +
                    $"在线匹配 {result.Matched} 个 / 打进包里 {result.Packed} 个 mod\n" +
                    $"体积 {result.SizeMb:0.0} MB\n\n" +
                    "恢复方式:下载页 → 整合包 → 选择该文件(或直接放入 modpacks 目录)。", "好"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                task.LogLine("已取消");
                throw;
            }
            catch (Exception ex)
            {
                LauncherLog.Error($"导出整合包 {ver.Id} 失败: {ex}");
                foreach (var line in Net.Describe(ex).Split('\n'))
                    task.LogLine(line.TrimEnd());
                Application.Invoke(() => MessageBox.ErrorQuery(Application.Instance,
                    "导出失败", Net.Brief(ex), "好"));
                throw;
            }
        });
    }

    /// <summary>用资源管理器打开一个目录（不存在就先建出来）</summary>
    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery(Application.Instance, "打不开文件夹",
                $"{path}\n\n{ex.Message}", "好");
        }
    }

    /// <summary>加载器 + 版本号（从 libraries 的 maven 坐标里掰出来）</summary>
    private static string LoaderDisplay(MinecraftVersion ver)
    {
        var (kind, version) = ModpackExporter.LoaderInfoOf(ver);
        if (kind is null)
            return "原版(无加载器)";
        var name = kind switch
        {
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            "fabric" => "Fabric",
            "quilt" => "Quilt",
            _ => kind,
        };
        return version is null ? name : $"{name} {version}";
    }

    private string DescribeJava(EffectiveLaunch eff, MinecraftVersion ver)
    {
        var path = JavaDetector.Resolve(eff.JavaPath, ver.RequiredJava);
        if (path is null)
            return "未找到可用 Java";
        var major = JavaDetector.Detect()
            .FirstOrDefault(j => string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase))?.Major;
        var auto = eff.JavaPath.Length == 0 ? "自动" : "手动指定";
        return major is null or 0 ? $"{path} ({auto})" : $"Java {major} ({auto})";
    }

    private static string RamSourceName(VersionOption opt) => opt.RamMode switch
    {
        0 => "自动",
        1 => "手动",
        _ => "跟随全局",
    };

    private static string IsolationName(IsolationMode mode, MinecraftVersion ver) => mode switch
    {
        IsolationMode.Always => "全部隔离",
        IsolationMode.Modded => GameLauncher.IsModdedVersion(ver) ? "仅 modded(已隔离)" : "仅 modded(本版本不隔离)",
        _ => "不隔离",
    };

    private string GameDirOf(MinecraftVersion ver)
    {
        var eff = VersionSettings.Resolve(Config, ver, 0);
        var isolated = eff.Isolation switch
        {
            IsolationMode.Always => true,
            IsolationMode.Modded => GameLauncher.IsModdedVersion(ver),
            _ => false,
        };
        return isolated ? Path.Combine(Config.MinecraftPath, "versions", ver.Id) : Config.MinecraftPath;
    }

    // ================= 设置（版本独立设置）=================

    private void BuildSetupPage(View content, MinecraftVersion ver, Action rerender)
    {
        var opt = VersionSettings.Get(Config, ver.Id);
        var modCount = VersionSettings.CountMods(Config, ver);

        var items = new ObservableCollection<string>();
        var actions = new List<Action>();

        void Add(string label, string value, Action act)
        {
            items.Add($"{label}:{value}");
            actions.Add(act);
        }

        void Save()
        {
            VersionSettings.Set(Config, ver.Id, opt);
            Config.Save();
        }

        // ---- 内存 ----
        Add("内存模式", opt.RamMode switch
        {
            0 => $"自动(当前按 {modCount} 个 mod 估算 {VersionSettings.AutoMaxMemory(modCount, VersionSettings.AvailableMemoryMb())} MB)",
            1 => "手动",
            _ => "跟随全局",
        }, () => PromptChoice("内存模式", new[] { "自动(按 mod 数)", "手动指定", "跟随全局" }, opt.RamMode, i =>
        {
            opt.RamMode = i;
            Save();
            rerender();
        }));

        Add("最大内存", opt.RamMode == 1
            ? $"{opt.MaxMemoryMb} MB"
            : "(仅手动模式可改)", () =>
        {
            if (opt.RamMode != 1)
            {
                MessageBox.Query(Application.Instance, "内存", "需先将内存模式改为[手动指定]才能填写具体数值。", "好");
                return;
            }
            PromptNumber("最大内存 (MB)", opt.MaxMemoryMb, v => { opt.MaxMemoryMb = v; Save(); rerender(); });
        });

        Add("最小内存", opt.RamMode == 1
            ? $"{opt.MinMemoryMb} MB"
            : "(仅手动模式可改)", () =>
        {
            if (opt.RamMode != 1)
            {
                MessageBox.Query(Application.Instance, "内存", "需先将内存模式改为[手动指定]才能填写具体数值。", "好");
                return;
            }
            PromptNumber("最小内存 (MB, 0=自动取 1/4)", opt.MinMemoryMb, v => { opt.MinMemoryMb = v; Save(); rerender(); });
        });

        // ---- Java ----
        Add("Java 路径", opt.JavaPath.Length == 0 ? "自动(按版本要求挑)" : opt.JavaPath,
            () => PromptText("Java 路径(留空=自动)", opt.JavaPath, s =>
            {
                opt.JavaPath = s.Trim();
                Save();
                rerender();
            }));

        // ---- 参数 ----
        Add("额外 JVM 参数", opt.JvmArgs.Length == 0 ? "(跟随全局)" : opt.JvmArgs,
            () => PromptText("额外 JVM 参数(留空 = 跟随全局)", opt.JvmArgs, s =>
            {
                opt.JvmArgs = s.Trim();
                Save();
                rerender();
            }));

        Add("额外游戏参数", opt.GameArgs.Length == 0 ? "(跟随全局)" : opt.GameArgs,
            () => PromptText("额外游戏参数(留空 = 跟随全局)", opt.GameArgs, s =>
            {
                opt.GameArgs = s.Trim();
                Save();
                rerender();
            }));

        // ---- 窗口 ----
        Add("窗口宽度", opt.WindowWidth > 0 ? $"{opt.WindowWidth}" : "跟随全局",
            () => PromptNumber("窗口宽度(0 = 跟随全局)", opt.WindowWidth, v => { opt.WindowWidth = v; Save(); rerender(); }));

        Add("窗口高度", opt.WindowHeight > 0 ? $"{opt.WindowHeight}" : "跟随全局",
            () => PromptNumber("窗口高度(0 = 跟随全局)", opt.WindowHeight, v => { opt.WindowHeight = v; Save(); rerender(); }));

        Add("全屏", opt.FullScreen switch { 1 => "是", 0 => "否", _ => "跟随全局" },
            () => PromptChoice("全屏", new[] { "跟随全局", "窗口模式", "全屏" }, opt.FullScreen + 1, i =>
            {
                opt.FullScreen = i - 1;
                Save();
                rerender();
            }));

        // ---- 隔离 ----
        Add("版本隔离", opt.Isolation switch
        {
            0 => "不隔离",
            1 => "仅 modded 隔离",
            2 => "全部隔离",
            _ => $"跟随全局({Config.Isolation switch { IsolationMode.Always => "全部", IsolationMode.Modded => "仅 modded", _ => "不隔离" }})",
        }, () => PromptChoice("版本隔离",
            new[] { "跟随全局", "不隔离", "仅 modded 隔离", "全部隔离" }, opt.Isolation + 1, i =>
            {
                opt.Isolation = i - 1;
                Save();
                rerender();
            }));

        // ---- 启动前命令 ----
        Add("启动前命令", opt.PreLaunchCommand.Length == 0 ? "(无)" : opt.PreLaunchCommand,
            () => PromptText("启动前命令(工作目录 = 游戏目录,留空清除)", opt.PreLaunchCommand, s =>
            {
                opt.PreLaunchCommand = s.Trim();
                Save();
                rerender();
            }));

        Add("等待命令结束", opt.PreLaunchWait ? "是" : "否", () =>
        {
            opt.PreLaunchWait = !opt.PreLaunchWait;
            Save();
            rerender();
        });

        // ---- 初始化 ----
        Add("清除所有覆盖", "恢复成全部跟随全局 →", () =>
        {
            var r = MessageBox.Query(Application.Instance, "初始化版本独立设置",
                $"将清除 {ver.Id} 的全部独立设置(内存 / Java / 参数 / 隔离...),回到跟随全局。\n此操作不可撤销,但不会动游戏文件,存档和 Mod。",
                "确认初始化", "取消");
            if (r != 0)
                return;
            VersionSettings.Reset(Config, ver.Id);
            Config.Save();
            rerender();
        });

        var list = new ListView<string>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        list.SetSource(items);
        list.SelectedItem = items[0];
        list.Accepted += (_, _) =>
        {
            var i = list.SelectedItem is { } s ? items.IndexOf(s) : -1;
            if (i >= 0 && i < actions.Count)
                actions[i]();
        };

        content.Add(list);
    }

    // ================= 通用小对话框 =================

    /// <summary>单行文本输入框（Dialog 高 9 = 内容 7 行：0..6）</summary>
    private static void PromptText(string title, string initial, Action<string> onOk)
    {
        var dlg = new Dialog
        {
            Title = title,
            Width = Math.Min(78, Math.Max(40, title.Length + 24)),
            Height = 9,
            BorderStyle = LineStyle.Single,
        };
        var field = new TextField { Text = initial, X = 1, Y = 1, Width = Dim.Fill(2) };
        var ok = new Button { Text = "确定", X = 1, Y = 5, IsDefault = true };
        var cancel = new Button { Text = "取消", X = Pos.Right(ok) + 2, Y = 5 };

        ok.Accepting += (_, _) =>
        {
            var text = field.Text ?? "";
            dlg.RequestStop();
            onOk(text);
        };
        cancel.Accepting += (_, _) => dlg.RequestStop();

        dlg.Add(field, ok, cancel);
        field.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();
    }

    /// <summary>数字输入框（非数字直接拒绝）</summary>
    private static void PromptNumber(string title, int initial, Action<int> onOk)
    {
        PromptText(title, initial.ToString(), s =>
        {
            if (int.TryParse(s.Trim(), out var n) && n >= 0)
                onOk(n);
            else
                MessageBox.Query(Application.Instance, "输入无效", "请填一个非负整数。", "好");
        });
    }

    /// <summary>
    /// 弹一个单项选择对话框（<paramref name="options"/> 列表，<paramref name="selectedIndex"/> 预选）。
    /// 用 DropDownList 而不是 ListView：ListView 上滚鼠标滚轮会直接改选中项，
    /// 用户只想滚一下看，结果把值改了。下拉框收起时滚轮不响应。
    /// </summary>
    private static void PromptChoice(string title, string[] options, int selectedIndex, Action<int> onOk)
    {
        var dlg = new Dialog
        {
            Title = title,
            Width = Math.Max(46, options.Max(o => o.GetColumns()) + 16),
            Height = 9,
            BorderStyle = LineStyle.Single,
        };

        var source = new ObservableCollection<string>(options);
        var drop = new DropDownList
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(2),
            Source = new ListWrapper<string>(source),
            Text = source[Math.Clamp(selectedIndex, 0, source.Count - 1)],
        };

        var ok = new Button { Text = "确定", X = 1, Y = 5, IsDefault = true };
        var cancel = new Button { Text = "取消", X = Pos.Right(ok) + 2, Y = 5 };

        void Confirm()
        {
            var picked = drop.Text?.ToString() ?? "";
            var i = source.IndexOf(picked);
            dlg.RequestStop();
            if (i >= 0)
                onOk(i);
        }

        ok.Accepting += (_, _) => Confirm();
        cancel.Accepting += (_, _) => dlg.RequestStop();

        dlg.Add(new Label { Text = "选择一项:", X = 1, Y = 1 }, drop, ok, cancel);
        drop.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();
    }
}
