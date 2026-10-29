using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using LargeHardLiteLauncher.Core;
using Terminal.Gui;

namespace LargeHardLiteLauncher.UI;

/// <summary>
/// 安装页, 在「原版游戏」列表里双击任一版本打开。
/// 左 groupbox 选 .minecraft; 右上版本名 (可改, 学 PCL) + 加载器下拉; 右下加载器版本列表。
/// 选"原版"= 只装原版 (列表留空); 选加载器 = 先补原版再装加载器。
/// 对话框必须 Dim.Fill(-1): TG 2.5 把阴影也算一列一行, 不 +1 会透过阴影看到边框。
/// </summary>
public partial class DownloadPage
{
    private static string[] LoaderKindNames => new[]
    {
        Loc.T("dl.loaderKindVanilla"), "Forge", "NeoForge", "Fabric", "Quilt",
    };

    /// <summary>双击原版版本 → 安装页。entry 决定基础 MC 版本</summary>
    private void PromptInstallWithLoader(VersionEntry entry)
    {
        var names = Config.Folders.Select(LauncherConfig.FolderDisplay).ToList();
        if (names.Count == 0)
            return;

        var baseMc = entry.Id;

        var dialog = new Dialog
        {
            Title = $"{Loc.T("dl.installVersionTitle")} - {Loc.T("dl.baseMc", baseMc)}",
            Width = Dim.Fill(-1),
            Height = Dim.Fill(-1),
            BorderStyle = LineStyle.Single,
        };

        // ---- 左: .minecraft 文件夹 ----
        var leftBox = new FrameView
        {
            Title = Loc.T("dl.installDir"),
            X = 0,
            Y = 0,
            Width = Dim.Percent(32),
            Height = Dim.Fill(4),
            BorderStyle = LineStyle.Single,
        };
        var folderItems = new ObservableCollection<string>(names);
        var folderList = new ListView<string> { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        folderList.SetSource(folderItems);
        folderList.SelectedItem = names[Math.Clamp(Config.CurrentFolder, 0, names.Count - 1)];
        leftBox.Add(folderList);

        // ---- 右上: 版本名 (可改) ----
        var nameLbl = new Label { Text = Loc.T("dl.instName"), X = Pos.Right(leftBox) + 1, Y = 0 };
        var nameBox = new TextField { Text = baseMc, X = Pos.Right(nameLbl) + 1, Y = 0, Width = 36 };
        var lastAuto = baseMc;

        // ---- 版本名下面: 加载器下拉 ----
        var kindLbl = new Label { Text = Loc.T("dl.loader"), X = Pos.Right(leftBox) + 1, Y = 2 };
        var kindNames = LoaderKindNames;
        var kindDrop = new DropDownList
        {
            X = Pos.Right(kindLbl) + 1,
            Y = 2,
            Width = 12,
            Source = new ListWrapper<string>(new ObservableCollection<string>(kindNames)),
        };
        kindDrop.Text = kindNames[0];

        // ---- 右下: 加载器版本列表 ----
        // Dim.Fill(4): 和左边 groupbox 底边对齐 (原来是 6, 比左边矮 2 行, 用户要求加高)
        var verBox = new FrameView
        {
            Title = Loc.T("dl.versions"),
            X = Pos.Right(leftBox) + 1,
            Y = 4,
            Width = Dim.Fill(2),
            Height = Dim.Fill(4),
            BorderStyle = LineStyle.Single,
        };
        var verItems = new ObservableCollection<string> { Loc.T("dl.pickLoaderHint") };
        var verList = new ListView<string> { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        verList.SetSource(verItems);
        verBox.Add(verList);

        var versions = new List<InstallableVersion>();
        var loadEpoch = 0;

        // 版本名自动跟随选择 (用户手动改过就不覆盖)
        void AutoName(string n)
        {
            if ((nameBox.Text ?? "") == lastAuto)
                nameBox.Text = n;
            lastAuto = n;
        }

        void ShowVersions(IReadOnlyList<InstallableVersion> list)
        {
            versions.Clear();
            versions.AddRange(list);
            verItems = new ObservableCollection<string>(versions.Select(v =>
                v.Note.Length > 0 ? $"{v.Version}   [{v.Note}]" : v.Version));
            verList.SetSource(verItems);
            verBox.Title = $"{kindDrop.Text} · {Loc.T("dl.versions")} ({versions.Count})";
            if (verItems.Count > 0)
            {
                verList.SelectedItem = verItems[0];
                var kind = KindFromIndex(Array.IndexOf(kindNames, kindDrop.Text ?? ""));
                AutoName(DefaultNameFor(kind, versions[0].Version, baseMc));
            }
        }

        void ShowEmpty()
        {
            versions.Clear();
            verItems = new ObservableCollection<string> { Loc.T("dl.pickLoaderHint") };
            verList.SetSource(verItems);
            verBox.Title = Loc.T("dl.versions");
            AutoName(baseMc);
        }

        void ReloadVersions()
        {
            var epoch = ++loadEpoch;
            var kind = KindFromIndex(Array.IndexOf(kindNames, kindDrop.Text ?? ""));
            ShowEmpty();
            if (kind == LoaderKind.Vanilla)
                return; // 原版: 列表留空 (装的就是双击的那个版本)

            TaskManager.Submit($"读取 {kindDrop.Text} 版本", async (task, ct) =>
            {
                try
                {
                    var list = await LoaderApi.ListAsync(kind, baseMc, ct).ConfigureAwait(false);
                    if (epoch != loadEpoch)
                        return;
                    task.LogLine($"{kindDrop.Text} 共 {list.Count} 个版本");
                    Application.Invoke(() =>
                    {
                        if (epoch == loadEpoch)
                            ShowVersions(list);
                    });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    task.LogLine($"失败: {ex.Message}");
                    if (epoch != loadEpoch)
                        return;
                    Application.Invoke(() =>
                    {
                        if (epoch != loadEpoch)
                            return;
                        ShowEmpty();
                        MessageBox.ErrorQuery(Application.Instance, Loc.T("common.error"),
                            $"读取 {kindDrop.Text} 版本失败: {ex.Message}", Loc.T("common.confirm"));
                    });
                }
            });
        }

        kindDrop.ValueChanged += (_, _) => ReloadVersions();
        verList.ValueChanged += (_, _) =>
        {
            // 选中加载器版本 → 版本名跟着给个默认名
            if (verList.SelectedItem is not { } sel)
                return;
            var i = verItems.IndexOf(sel);
            if (i < 0 || i >= versions.Count)
                return;
            var kind = KindFromIndex(Array.IndexOf(kindNames, kindDrop.Text ?? ""));
            if (kind == LoaderKind.Vanilla)
                return;
            AutoName(DefaultNameFor(kind, versions[i].Version, baseMc));
        };

        // ---- 底部: 目标 + 按钮 ----
        var target = new Label
        {
            Text = $"{Loc.T("dl.saveTarget")}: {Path.Combine(Config.Folders[0], "versions")}",
            X = 1,
            Y = Pos.AnchorEnd(4),
            Width = Dim.Fill(2),
        };
        void UpdateTarget()
        {
            if (folderList.SelectedItem is not { } fname)
                return;
            var fi = folderItems.IndexOf(fname);
            if (fi >= 0 && fi < Config.Folders.Count)
                target.Text = $"{Loc.T("dl.saveTarget")}: {Path.Combine(Config.Folders[fi], "versions")}";
        }
        folderList.ValueChanged += (_, _) => UpdateTarget();
        UpdateTarget();

        var ok = new Button { Text = Loc.T("dl.install"), X = Pos.Center() - 10, Y = Pos.AnchorEnd(2), IsDefault = true };
        var cancel = new Button { Text = Loc.T("common.cancel"), X = Pos.Center() + 6, Y = Pos.AnchorEnd(2) };

        var go = false;
        var chosenKind = LoaderKind.Vanilla;
        var chosenLoaderVer = "";
        var chosenFolder = "";
        var chosenName = "";
        ok.Accepting += (_, _) =>
        {
            var fi = folderList.SelectedItem is { } f ? folderItems.IndexOf(f) : 0;
            if (fi < 0 || fi >= Config.Folders.Count)
            {
                MessageBox.ErrorQuery(Application.Instance, Loc.T("common.error"),
                    Loc.T("dl.noSelect"), Loc.T("common.confirm"));
                return;
            }
            var kind = KindFromIndex(Array.IndexOf(kindNames, kindDrop.Text ?? ""));
            var vi = verList.SelectedItem is { } v ? verItems.IndexOf(v) : -1;
            if (kind != LoaderKind.Vanilla && (vi < 0 || vi >= versions.Count))
            {
                MessageBox.ErrorQuery(Application.Instance, Loc.T("common.error"),
                    Loc.T("dl.pickLoaderHint"), Loc.T("common.confirm"));
                return;
            }
            var nm = (nameBox.Text ?? "").Trim();
            if (nm.Length == 0 || nm.Any(c => "\\/:*?\"<>|".Contains(c)))
            {
                MessageBox.ErrorQuery(Application.Instance, Loc.T("common.error"),
                    Loc.T("dl.saveBadName"), Loc.T("common.confirm"));
                return;
            }
            chosenKind = kind;
            chosenLoaderVer = kind == LoaderKind.Vanilla ? baseMc : versions[vi].Version;
            chosenFolder = Config.Folders[fi];
            chosenName = nm;
            go = true;
            dialog.RequestStop();
        };
        cancel.Accepting += (_, _) => dialog.RequestStop();

        dialog.Add(leftBox, nameLbl, nameBox, kindLbl, kindDrop, verBox, target, ok, cancel);
        folderList.SetFocus();
        Application.Run(dialog);
        dialog.Dispose();

        if (!go)
            return;
        StartInstallWithLoader(chosenKind, chosenLoaderVer, chosenFolder, chosenName, baseMc, entry);
    }

    /// <summary>两个路径是否指向同一目录 (忽略大小写与末尾分隔符)</summary>
    private static bool SameDir(string a, string b)
    {
        try
        {
            static string Norm(string p) => Path.GetFullPath(p)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static LoaderKind KindFromIndex(int idx) => idx switch
    {
        1 => LoaderKind.Forge,
        2 => LoaderKind.NeoForge,
        3 => LoaderKind.Fabric,
        4 => LoaderKind.Quilt,
        _ => LoaderKind.Vanilla,
    };

    /// <summary>加载器版本 → 安装器会用/我们期望的默认版本名, 叫法对齐各 loader 的官方命名</summary>
    private static string DefaultNameFor(LoaderKind kind, string loaderVer, string baseMc) => kind switch
    {
        LoaderKind.Forge => $"{baseMc}-forge-{loaderVer}",
        LoaderKind.NeoForge => $"neoforge-{loaderVer}",
        LoaderKind.Fabric => $"fabric-loader-{loaderVer}-{baseMc}",
        LoaderKind.Quilt => $"quilt-loader-{loaderVer}-{baseMc}",
        _ => baseMc,
    };

    /// <summary>安装任务: 原版 → (加载器) → 按用户起的名字重命名</summary>
    private void StartInstallWithLoader(LoaderKind kind, string loaderVer, string mcDir,
        string instanceName, string baseMc, VersionEntry entry)
    {
        var kindName = kind == LoaderKind.Vanilla ? Loc.T("dl.loaderKindVanilla") : kind.ToString();
        TaskManager.Submit(Loc.T("dl.installing", instanceName), async (task, ct) =>
        {
            try
            {
                GameDownloader.Configure(Config.IgnoreSslErrors);
                task.LogLine($"目标: {mcDir}");
                task.LogLine($"版本名: {instanceName}");

                // 1) 原版部分 (选加载器时也要, 因为加载器 inheritsFrom 它)
                var baseJson = Path.Combine(mcDir, "versions", baseMc, baseMc + ".json");
                if (!File.Exists(baseJson))
                {
                    task.LogLine(kind == LoaderKind.Vanilla ? "安装原版..." : $"目标目录缺原版 {baseMc},先补装...");
                    var files = await GameDownloader.BuildPlanAsync(entry, mcDir, Config.DownloadSource, task, ct)
                        .ConfigureAwait(false);
                    task.LogLine($"原版文件清单: {files.Count} 个");
                    await GameDownloader.DownloadAsync(files, task, ct).ConfigureAwait(false);
                }

                // 2) 加载器
                string generated;
                if (kind == LoaderKind.Vanilla)
                {
                    generated = baseMc;
                }
                else if (kind is LoaderKind.Fabric or LoaderKind.Quilt)
                {
                    generated = await LoaderInstaller.InstallJsonLoaderAsync(kind, baseMc, loaderVer, mcDir, task, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    generated = await LoaderInstaller.InstallByInstallerAsync(kind, baseMc, loaderVer, mcDir, task, ct)
                        .ConfigureAwait(false);
                }

                // 3) 用户改过名字就重命名目录 (PCL 的版本名支持)
                var final = LoaderInstaller.RenameVersion(mcDir, generated, instanceName, task);

                task.LogLine($"安装完成: {final}");
                LauncherLog.Info($"安装 {kindName} {loaderVer} 完成: {final} @ {mcDir}");
                Application.Invoke(() =>
                {
                    // 版本表变了, 立刻作废扫描缓存 (否则 3 秒内读到的还是老列表)
                    InvalidateVersionCache();

                    var sameDir = SameDir(mcDir, Config.MinecraftPath);
                    if (sameDir)
                    {
                        // 学 PCL: 装完自动选中新版本, 回启动页直接能启动
                        Config.SelectedVersion = final;
                        Config.Save();
                        MessageBox.Query(Application.Instance, Loc.T("dl.install"),
                            Loc.T("dl.installedCurrent", final), Loc.T("common.confirm"));
                        return;
                    }

                    // 装到别的 .minecraft 了: 问要不要切过去 (不切就等于白装, 启动页看不到)
                    var r = MessageBox.Query(Application.Instance, Loc.T("dl.install"),
                        Loc.T("dl.installedOtherDir", final, mcDir),
                        Loc.T("dl.switchTo"), Loc.T("dl.stayHere"));
                    if (r != 0)
                        return;

                    var fi = Config.Folders.FindIndex(f => SameDir(f, mcDir));
                    if (fi < 0)
                    {
                        Config.Folders.Add(mcDir);
                        fi = Config.Folders.Count - 1;
                    }
                    Config.CurrentFolder = fi;
                    Config.MinecraftPath = mcDir;
                    Config.SelectedVersion = final;
                    Config.Save();
                    InvalidateVersionCache();
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                task.LogLine("已取消");
                throw;
            }
            catch (Exception ex)
            {
                LauncherLog.Error($"安装 {kindName} {loaderVer} 失败: {ex}");
                foreach (var line in Net.Describe(ex).Split('\n'))
                    task.LogLine(line.TrimEnd());
                var brief = Net.Brief(ex);
                if (Net.IsSslFailure(ex) && !Config.IgnoreSslErrors)
                    brief += " | 可到 设置→下载 勾选[忽略 HTTPS 证书错误]再试";
                Application.Invoke(() => MessageBox.ErrorQuery(Application.Instance,
                    Loc.T("common.error"), Loc.T("dl.installFailed", brief), Loc.T("common.confirm")));
                throw;
            }
        });
    }

}
