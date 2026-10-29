using System.Collections.ObjectModel;
using LargeHardLiteLauncher.Core;
using Terminal.Gui;

namespace LargeHardLiteLauncher.UI;

/// <summary>
/// 启动页：左侧「账户」groupbox（玩家/版本/启动），右侧主页内容区（空白/预设/本地文件/URL）。
/// </summary>
public partial class LaunchPage : PageBase
{
    private readonly Label _playerLabel;
    private readonly Label _folderInfoLabel;
    private readonly Label _versionLabel;
    private readonly ObservableCollection<string> _versions = new();
    private List<MinecraftVersion> _versionObjs = new();
    private MinecraftVersion? _selected;
    private bool _firstEnter = true;
    private readonly TextView _homeView;
    /// <summary>启动按钮（会被"上次的游戏仍在运行"逻辑用到，所以存字段）</summary>
    private Button _launchBtn = null!;
    /// <summary>启动按钮上方那行"游戏运行中:xxx(12 分钟)"</summary>
    private Label _runningLbl = null!;

    public LaunchPage(LauncherConfig config) : base(config, $" {Loc.T("tab.launch")} ")
    {
        // ===== 左侧「账户」groupbox =====
        var leftBox = new FrameView
        {
            Title = Loc.T("acc.title"),
            X = 0,
            Y = 0,
            Width = Dim.Percent(30),
            Height = 7, // 边框2 + 玩家名2 + 账户按钮2 + 余1
            BorderStyle = LineStyle.Single,
        };

        var playerCap = new Label { Text = Loc.T("acc.playerCap"), X = 1, Y = 0 };
        _playerLabel = new Label
        {
            Text = Config.PlayerName,
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
        };
        // 账户管理入口：多账户切换 / 加离线账户
        var accountBtn = new Button
        {
            Text = "账户",
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(-1),
            NoPadding = true,
        };
        accountBtn.Accepting += (_, _) => ShowAccountManager();
        leftBox.Add(playerCap, _playerLabel, accountBtn);

        // ===== 左侧「版本」groupbox (新增: 当前版本 + 目录 + 选择入口) =====
        var verBox = new FrameView
        {
            Title = Loc.T("acc.versionBox"),
            X = 0,
            Y = 7,
            Width = Dim.Percent(30),
            Height = Dim.Fill(2), // 剩余高度全给版本框
            BorderStyle = LineStyle.Single,
        };

        _versionLabel = new Label
        {
            Text = Loc.T("acc.noVersion"),
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
        };
        var folderCapLbl = new Label { Text = Loc.T("acc.folder"), X = 1, Y = 2 };
        _folderInfoLabel = new Label
        {
            Text = Config.MinecraftPath,
            X = 1,
            Y = 3,
            Width = Dim.Fill(1),
        };
        // 注意: 千万别给 Button 显式设 Height=1, TG 2.5 会把边框/文本挤没只剩阴影
        var verSelBtn = new Button
        {
            Text = Loc.T("launch.selectVersion"),
            NoPadding = true,
            X = 0,
            Y = Pos.AnchorEnd(4),   // 15..16 (+2)
            Width = Dim.Fill(-1),   // 右侧多占 1 格
        };
        verSelBtn.Accepting += (_, _) => ShowVersionPicker();

        var verCfgBtn = new Button
        {
            Text = Loc.T("launch.versionSettings"),
            NoPadding = true,
            X = 0,
            Y = Pos.AnchorEnd(2),   // 17..18 贴框底 (+2)
            Width = Dim.Fill(-1),   // 右侧多占 1 格
        };
        verCfgBtn.Accepting += (_, _) =>
        {
            if (_selected is null)
            {
                MessageBox.Query(Application.Instance, Loc.T("launch.versionSettings"),
                    "先在版本列表里选一个版本(点[选择版本]),再进版本设置。", Loc.T("common.confirm"));
                return;
            }
            ShowVersionManager(_selected);
        };

        verBox.Add(_versionLabel, folderCapLbl, _folderInfoLabel, verSelBtn, verCfgBtn);

        // ===== 底部按钮 (贴窗口底边) =====
        _launchBtn = new Button
        {
            Text = Loc.T("launch.start"),
            NoPadding = true,
            X = 0,
            Y = Pos.AnchorEnd(2),   // 高 2 行, 紧贴屏幕底边
            Width = Dim.Percent(31), // 比版本框(30%)再宽 1 格
            Height = 2,
        };
        _launchBtn.Accepting += (_, _) => OnLaunch();

        // "上次启动的游戏仍在运行"提示条（贴在启动按钮上方一行）
        // 关掉终端窗口后游戏会继续跑，再打开启动器时靠它告诉用户
        _runningLbl = new Label
        {
            Text = "",
            X = 0,
            Y = Pos.AnchorEnd(4),
            Width = Dim.Percent(31),
            Visible = false,
        };

        // ===== 右侧主页区 (75%) =====
        _homeView = new TextView
        {
            X = Pos.Percent(30) + 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(),
            ReadOnly = true,
        };

        Add(leftBox, verBox, _launchBtn, _runningLbl, _homeView);

        RefreshPlayerLabel();
        LoadHomePage();
        RefreshVersionList(); // 构造时扫一次, 保证首屏就有数据
    }

    /// <summary>刷新当前目录显示</summary>
    private void RefreshFolderList()
    {
        _folderInfoLabel.Text = Config.MinecraftPath;
    }

    /// <summary>添加一个 .minecraft 文件夹 (用 TG 自带的目录选择框)</summary>
    private void ShowAddFolderDialog(Action? onAdded = null)
    {
        using var fd = new FileDialog
        {
            Title = Loc.T("acc.addFolder"),
            OpenMode = OpenMode.Directory,
            Path = Config.MinecraftPath,
            MustExist = true,
        };
        Application.Run(fd);

        if (fd.Canceled == true)
            return;

        var path = fd.Path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        // 去掉末尾分隔符, 统一格式
        path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (Config.Folders.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Query(Application.Instance, Loc.T("acc.addFolder"),
                Loc.T("acc.folderExists"), Loc.T("common.confirm"));
            return;
        }

        Config.Folders.Add(path);
        Config.CurrentFolder = Config.Folders.Count - 1;
        Config.MinecraftPath = path;
        Config.Save();
        RefreshFolderList();
        RefreshVersionList();
        LauncherLog.Info($"添加游戏目录: {path}");
        onAdded?.Invoke();
    }

    private void ShowVersionPicker()
    {
        var dialog = new Dialog
        {
            Title = Loc.T("launch.versionPickTitle"),
            Width = Dim.Fill(-1),   // 比全屏再多 1 格
            Height = Dim.Fill(-1),
            BorderStyle = LineStyle.Single,
        };

        // ===== 左: 游戏目录 (切换 + 添加) =====
        var folderBox = new FrameView
        {
            Title = Loc.T("acc.folder"),
            X = 0,
            Y = 0,
            Width = Dim.Percent(34),
            Height = Dim.Fill(1),
            BorderStyle = LineStyle.Single,
        };
        var names = Config.Folders.Select(LauncherConfig.FolderDisplay).ToList();
        var folderList = new ListView<string>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(3),
        };
        folderList.SetSource(new ObservableCollection<string>(names));
        if (names.Count > 0)
            folderList.SelectedItem = names[Math.Clamp(Config.CurrentFolder, 0, names.Count - 1)];

        var addBtn = new Button
        {
            Text = Loc.T("acc.addFolder"),
            X = 0,
            Y = Pos.AnchorEnd(2),
            NoPadding = true,
        };
        folderBox.Add(folderList, addBtn);

        // ===== 右: 版本列表 =====
        var verBox = new FrameView
        {
            Title = Loc.T("launch.selectVersion"),
            X = Pos.Percent(34) + 1,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            BorderStyle = LineStyle.Single,
        };
        var verItems = new ObservableCollection<string>(_versions);
        var verList = new ListView<string>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        verBox.Add(verList); // ← 必须 Add, 否则列表是孤儿视图 (Frame 0x0 什么都不画)
        verList.SetSource(verItems);
        verList.SelectedItem = _selected?.Display;
        verList.SetFocus();

        void Rescan()
        {
            var objs = VersionScanner.Scan(Config.MinecraftPath);
            _versionObjs = objs;
            verItems.Clear();
            foreach (var v in objs)
                verItems.Add(v.Display);
            if (verItems.Count > 0)
                verList.SelectedItem = verItems[0];
        }

        // 切换目录 → 存盘 + 重扫
        folderList.ValueChanged += (_, _) =>
        {
            var idx = names.IndexOf(folderList.SelectedItem ?? "");
            if (idx < 0 || idx == Config.CurrentFolder)
                return;
            Config.CurrentFolder = idx;
            Config.MinecraftPath = Config.Folders[idx];
            Config.Save();
            LauncherLog.Info($"切换游戏目录: {Config.MinecraftPath}");
            Rescan();
        };

        // 选中即生效 (不依赖 Accepted, 否则选中后点取消会丢)
        verList.ValueChanged += (_, _) =>
        {
            var v = verList.Value;
            if (string.IsNullOrEmpty(v))
                return;
            _versionLabel.Text = v;
            var hit = _versionObjs.FirstOrDefault(x => x.Display == v);
            if (hit is not null)
            {
                _selected = hit;
                Config.SelectedVersion = hit.Id;
            }
            _versionLabel.SetNeedsDraw();
        };
        verList.Accepted += (_, _) =>
        {
            Config.Save();
            Application.RequestStop(dialog);
        };

        // 添加目录 (弹完刷新左列并重扫)
        addBtn.Accepting += (_, _) => ShowAddFolderDialog(() =>
        {
            names = Config.Folders.Select(LauncherConfig.FolderDisplay).ToList();
            folderList.SetSource(new ObservableCollection<string>(names));
            if (names.Count > 0)
                folderList.SelectedItem = names[^1];
            Rescan();
        });

        var cancel = new Button
        {
            Text = Loc.T("common.cancel"),
            X = Pos.AnchorEnd(12),
            Y = Pos.AnchorEnd(1),
            Width = 10,
        };
        cancel.Accepting += (_, _) => Application.RequestStop(dialog);

        dialog.Add(folderBox, verBox, cancel);
        Application.Run(dialog);
        dialog.Dispose();

        // 关窗后同步页面显示
        _folderInfoLabel.Text = Config.MinecraftPath;
        RefreshVersionList();
    }

    private void RefreshPlayerLabel()
    {
        // 正版模式显示微软档案名 (带标记), 离线显示自定义名
        _playerLabel.Text = AuthState.IsMicrosoft(Config)
            ? AuthState.PlayerName(Config) + "  [正版]"
            : Config.PlayerName + "  [离线]";
    }

    private void LoadHomePage()
    {
        try
        {
            var ver = _selected;
            switch (Config.HomePage)
            {
                case HomePageMode.Blank:
                    _homeView.Text = "";
                    break;

                case HomePageMode.Preset:
                    _homeView.Text = HomePageLoader.PresetText(Config, ver);
                    break;

                case HomePageMode.LocalFile:
                    _homeView.Text = HomePageLoader.Render(
                        HomePageLoader.LoadLocal(Config.HomePageSource), Config, ver);
                    break;

                case HomePageMode.Url:
                {
                    var url = (Config.HomePageSource ?? "").Trim();
                    if (url.Length == 0)
                    {
                        _homeView.Text = "(尚未设置主页地址,请在 设置 → 启动 → 主页 中填写)";
                        break;
                    }

                    // 先显示缓存，再后台刷新：不能因为对方服务器慢把界面卡住
                    var cached = HomePageLoader.LoadCache(url);
                    _homeView.Text = cached is not null
                        ? HomePageLoader.Render(cached, Config, ver)
                        : "(正在加载联网主页...)";
                    _ = RefreshRemoteHomeAsync(url);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _homeView.Text = $"(主页加载失败: {ex.Message})";
        }
    }

    /// <summary>后台拉远程主页，回来再刷界面（不阻塞 UI）</summary>
    private async Task RefreshRemoteHomeAsync(string url)
    {
        try
        {
            var (text, _) = await HomePageLoader.FetchAsync(url, CancellationToken.None)
                .ConfigureAwait(false);

            Application.Invoke(() =>
            {
                // 用户可能已经切到别的模式了，别乱改
                if (Config.HomePage != HomePageMode.Url)
                    return;
                try
                {
                    if (text is null)
                    {
                        // 联网失败：有缓存就继续用缓存（已经在显示了），没有才报错
                        if (HomePageLoader.LoadCache(url) is null)
                            _homeView.Text = $"(联网主页加载失败,也没有缓存)\n{url}";
                    }
                    else
                    {
                        _homeView.Text = HomePageLoader.LooksLikeHtml(text)
                            ? $"(这个地址返回的是网页 HTML,不是纯文本,无法在这显示)\n{url}\n\n请换成 .txt / .md 之类的纯文本地址"
                            : HomePageLoader.Render(text, Config, _selected);
                    }
                    _homeView.SetNeedsDraw();
                }
                catch
                {
                    // 页面可能已经销毁
                }
            });
        }
        catch
        {
            // 拉取失败不影响已显示的缓存
        }
    }

    private void OnLaunch()
    {
        if (_selected is null)
        {
            MessageBox.Query(Application.Instance, Loc.T("launch.start"),
                Loc.T("launch.needVersion"), Loc.T("common.confirm"));
            return;
        }

        // 上次启动的游戏可能还在跑（关掉终端窗口不会杀它），先问一句
        var running = GameSessionStore.LoadAlive();
        if (running is not null)
        {
            var mins = (int)running.Elapsed.TotalMinutes;
            var r = MessageBox.Query(Application.Instance, "游戏还在运行",
                $"{running.VersionName} 还在后台运行(已运行 {mins} 分钟,PID {running.Pid})。\n\n" +
                "要结束它再启动新的吗?",
                "结束并启动", "取消");
            if (r != 0)
                return;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(running.Pid);
                p.Kill(entireProcessTree: true);
            }
            catch
            {
                // 可能刚好自己退了
            }
            GameSessionStore.Clear();
            RefreshRunningState();
        }

        var v = _selected;
        TaskManager.Submit(Loc.T("launch.start"), async (item, ct) =>
        {
            try
            {
                await GameLauncher.LaunchAsync(v, Config, item, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                item.LogLine(Loc.T("task.cancelledByUser"));
                throw;
            }
            catch (GameCrashException crash)
            {
                // 崩溃分析有结论 → 弹详细报告（比"详见日志"有用得多）
                LauncherLog.Error($"启动 {v.Id} 崩溃: {crash.Message}");
                Application.Invoke(() =>
                    MessageBox.ErrorQuery(Application.Instance, "游戏崩溃了", crash.RenderReport(), Loc.T("common.confirm")));
                throw;
            }
            catch (Exception ex)
            {
                // 按异常类型给直白原因
                var msg = ex switch
                {
                    FileNotFoundException f => f.Message,
                    InvalidOperationException o => o.Message,
                    IOException io => $"文件读写失败: {io.Message}",
                    _ => ex.Message,
                };
                LauncherLog.Error($"启动 {v.Id} 失败: {ex}");
                Application.Invoke(() =>
                    MessageBox.ErrorQuery(Application.Instance, Loc.T("launch.failedTitle"), msg, Loc.T("common.confirm")));
                throw;
            }
        });
    }

    /// <summary>重新扫描本地版本并刷新列表/标签</summary>
    private void RefreshVersionList()
    {
        _versionObjs = VersionScanner.Scan(Config.MinecraftPath);
        _versions.Clear();
        foreach (var v in _versionObjs)
            _versions.Add(v.Display);

        // 优先恢复上次选的版本, 其次保持当前选中, 最后取第一个
        _selected = _versionObjs.FirstOrDefault(x => x.Id == Config.SelectedVersion)
                    ?? _versionObjs.FirstOrDefault(x => x.Id == _selected?.Id)
                    ?? _versionObjs.FirstOrDefault();
        if (_selected is not null && _selected.Id != Config.SelectedVersion)
        {
            Config.SelectedVersion = _selected.Id;
        }
        _versionLabel.Text = _selected?.Id ?? Loc.T("acc.noVersion");
        _folderInfoLabel.Text = Config.MinecraftPath;
        LauncherLog.Info($"扫描版本: {_versionObjs.Count} 个 @ {Config.MinecraftPath}");
    }

    public override void OnPageEnter()
    {
        RefreshPlayerLabel();
        RefreshRunningState();
        LoadHomePage();
        // 构造时已扫过一次, 首次进入不重复扫 (否则日志里会看到两次扫描)
        if (_firstEnter)
        {
            _firstEnter = false;
            return;
        }
        RefreshVersionList();
    }

    /// <summary>
    /// 检查"上次启动的游戏还在不在"，更新启动按钮上方的提示条。
    ///
    /// 关掉终端窗口不会连带杀游戏（游戏是 DETACHED_PROCESS 启动的），
    /// 所以重新打开启动器时它可能还在跑, 靠 running-game.json + PID 三重校验认出来。
    /// </summary>
    private void RefreshRunningState()
    {
        var s = GameSessionStore.LoadAlive();
        if (s is null)
        {
            _runningLbl.Text = "";
            _runningLbl.Visible = false;
            _runningLbl.SetNeedsDraw();
            return;
        }

        _runningLbl.Text = $"游戏运行中:{s.VersionName}({(int)s.Elapsed.TotalMinutes} 分钟)";
        _runningLbl.Visible = true;
        _runningLbl.SetNeedsDraw();
    }
}
