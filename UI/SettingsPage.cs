using System.Collections.ObjectModel;
using LargeHardLiteLauncher.Core;
using Terminal.Gui;
using Terminal.Gui.Configuration;

namespace LargeHardLiteLauncher.UI;

/// <summary>
/// 设置页：左侧分类导航 groupbox + 右侧设置区（性能等分组用直角 groupbox）。
/// </summary>
public class SettingsPage : PageBase
{
    private static string[] Sections => new[] { Loc.T("set.section.launch"), Loc.T("set.section.personalize"), Loc.T("set.section.performance"), Loc.T("set.section.misc") };

    private readonly Action _backToLaunch;
    private readonly View _panel;

    // 启动
    private readonly TextField _playerName;
    private readonly TextField _msClientId;
    private readonly Button _msLoginBtn;
    private readonly Label _msStateLbl;
    private readonly TextField _mcPath;
    private readonly DropDownList _javaDrop;
    private List<JavaInfo> _javas = new();
    /// <summary>刷新 Java 下拉时抑制 ValueChanged（不然会把用户选的路径冲掉）</summary>
    private bool _suppressJavaChange;
    private readonly TextField _jvmArgs;

    // 个性化
    private readonly DropDownList _homeModeList;
    private readonly TextField _homeSource;

    // 性能
    private readonly TextField _fpsLimit;
    private readonly CheckBox _disableAnim;
    private readonly CheckBox _startupAnim;
    private readonly DropDownList _isolationDrop;
    private readonly string[] _isolationNames;
    private readonly TextField _maxMem;
    private readonly TextField _minMem;
    /// <summary>内存模式：自动（按 mod 数）/ 手动指定</summary>
    private readonly DropDownList _ramModeDrop;
    private readonly TextField _winWidth;
    private readonly TextField _winHeight;
    private readonly CheckBox _fullScreen;
    private readonly CheckBox _lowMem;
    private readonly TextField _inputDebounce;
    private readonly TextField _poolSize;
    private readonly CheckBox _dnsPreheat;
    private readonly TextField _listCache;
    private readonly TextField _cacheMax;
    private readonly TextField _taskCapacity;

    // 其他
    private readonly TextField _maxThreads;
    private readonly TextField _timeout;
    private readonly ListView<string> _dlSourceList;
    /// <summary>右侧竖滚动条 (显示当前浏览位置的百分比)</summary>
    private readonly ScrollBar _scrollBar;
    /// <summary>防止"面板滚动 → 更新滑块 → 又滚面板"的循环</summary>
    private bool _syncingScroll;
    private readonly ListView<string> _logLevelList;
    private readonly CheckBox _showDebug;
    private readonly CheckBox _ignoreSsl;
    private readonly TextField _cfKey;
    /// <summary>Mod 文件名中文前缀样式 (学 PCL2)</summary>
    private readonly ListView<string> _modNameList;
    private static readonly string[] ModNameStyles =
    {
        Loc.T("dl.nameStyleNone"), Loc.T("dl.nameStyleCn"), Loc.T("dl.nameStyleSquare"),
        Loc.T("dl.nameStyleDash"), Loc.T("dl.nameStyleLead"),
    };

    // 个性化
    private readonly ListView<string> _themeList;
    private readonly ListView<string> _langList;
    private List<ThemeCatalog.Entry> _themeCatalog = new();
    private bool _themeSyncing; // 布局回填选中项时抑制 ValueChanged 副作用

    private static readonly string[] HomeModes = { "空白", "预设", "本地文件", "联网 URL" };
    private static readonly string[] DlSources = { "自动(官方优先,慢时换镜像)", "仅官方源", "仅 BMCLAPI 镜像" };
    private static readonly string[] LogLevels = { "安静", "正常", "详细", "调试" };

    public SettingsPage(LauncherConfig config, Action backToLaunch) : base(config, $" {Loc.T("tab.settings")} ")
    {
        _backToLaunch = backToLaunch;

        // ===== 左侧分类导航 groupbox (18%, 直角) =====
        var navBox = new FrameView
        {
            Title = Loc.T("set.categories"),
            X = 0,
            Y = 0,
            Width = Dim.Percent(18),
            Height = Dim.Fill(), // 贴齐内容区底边
            BorderStyle = LineStyle.Single,
        };
        var sectionList = new ListView<string>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        sectionList.SetSource(new ObservableCollection<string>(Sections));
        sectionList.ValueChanged += (_, _) =>
        {
            var idx = Array.IndexOf(Sections, sectionList.Value);
            if (idx >= 0)
                ShowSection(idx);
        };
        navBox.Add(sectionList);

        // ===== 右侧设置区 (View 自带 ContentSize, 内容超高时整体滚动) =====
        _panel = new View
        {
            X = Pos.Percent(18) + 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Fill(),
            CanFocus = true, // 父不可聚焦则子 TextField 永远拿不到焦点 (TG2 规则)
        };

        // ---- 构建控件（一次性创建, 切换分类时动态挂载） ----
        _playerName = new TextField { Text = Config.PlayerName, Width = Dim.Percent(60) };
        _msClientId = new TextField { Text = Config.MicrosoftClientId, Width = Dim.Percent(60) };
        _msLoginBtn = new Button { Text = Loc.T("set.msLogin"), NoPadding = true };
        _msLoginBtn.Accepting += (_, _) => OnMicrosoftAuthButton();
        _msStateLbl = new Label { Text = MsStateText() };
        _mcPath = new TextField { Text = Config.MinecraftPath, Width = Dim.Percent(60) };
        _javaDrop = new DropDownList { Width = Dim.Percent(46) };
        _javas = JavaDetector.Detect();
        var javaItems = new List<string> { Loc.T("set.javaAuto") };
        javaItems.AddRange(_javas.Select(j => j.Display));
        _javaDrop.Source = new ListWrapper<string>(new System.Collections.ObjectModel.ObservableCollection<string>(javaItems));
        _javaDrop.Text = string.IsNullOrWhiteSpace(Config.JavaPath)
            ? Loc.T("set.javaAuto")
            : (_javas.FirstOrDefault(j => string.Equals(j.Path, Config.JavaPath, StringComparison.OrdinalIgnoreCase))?.Display
               ?? Config.JavaPath);
        _javaDrop.ValueChanged += (_, _) =>
        {
            if (_suppressJavaChange)
                return; // 刷新列表时设 Text 不算用户操作
            var text = _javaDrop.Text;
            if (string.IsNullOrEmpty(text) || text == Loc.T("set.javaAuto"))
                Config.JavaPath = "";
            else
            {
                var hit = _javas.FirstOrDefault(j => j.Display == text);
                Config.JavaPath = hit?.Path ?? text;
            }
        };
        _jvmArgs = new TextField { Text = Config.JvmArgs, Width = Dim.Percent(60) };
        _maxMem = new TextField { Text = Config.MaxMemoryMb.ToString(), Width = Dim.Percent(20) };
        _minMem = new TextField { Text = Config.MinMemoryMb.ToString(), Width = Dim.Percent(20) };

        // 内存模式：手动才让填两个框，自动时禁用（免得用户以为填了有用）
        _ramModeDrop = new DropDownList
        {
            Width = Dim.Percent(40),
            Source = new ListWrapper<string>(
                new ObservableCollection<string>(new[] { "自动(按 mod 数)", "手动指定" })),
            Text = Config.RamMode == 1 ? "手动指定" : "自动(按 mod 数)",
        };
        _ramModeDrop.ValueChanged += (_, _) => SyncMemEnabled();
        _winWidth = new TextField { Text = Config.WindowWidth.ToString(), Width = Dim.Percent(20) };
        _winHeight = new TextField { Text = Config.WindowHeight.ToString(), Width = Dim.Percent(20) };
        _fullScreen = new CheckBox { Text = Loc.T("set.fullscreen"), Value = Config.FullScreen ? CheckState.Checked : CheckState.UnChecked };

        _homeModeList = new DropDownList
        {
            Width = 28,
            Source = new ListWrapper<string>(new ObservableCollection<string>(HomeModes)),
            Text = HomeModes[(int)Config.HomePage],
        };
        _homeModeList.ValueChanged += (_, _) =>
        {
            var idx = Array.IndexOf(HomeModes, _homeModeList.Text?.ToString() ?? "");
            if (idx >= 0) Config.HomePage = (HomePageMode)idx;
        };
        _homeSource = new TextField { Text = Config.HomePageSource, Width = Dim.Percent(60) };

        // 主题 + 配色统一列表 (内置 TG 主题 + 自定义柔和/流行配色)
        _themeCatalog = ThemeCatalog.Build();
        _themeList = new ListView<string> { Width = 28, Height = 10 };
        ScrollOnly(_themeList); // 滚轮只滚动浏览, 不改变选中 (否则每滚一格就换主题, 界面狂闪)
        _themeList.SetSource(new ObservableCollection<string>(_themeCatalog.Select(e => e.Display)));
        _themeList.ValueChanged += (_, _) =>
        {
            if (_themeSyncing)
                return;
            var name = _themeList.Value;
            if (string.IsNullOrEmpty(name))
                return;
            var entry = _themeCatalog.FirstOrDefault(e => e.Display == name);
            if (entry is null)
                return;

            if (entry.Builtin is not null)
            {
                // 内置主题: 恢复快照 (清掉配色覆盖) 再套主题
                Config.ColorSchemeId = "";
                ColorSchemes.Clear();
                Config.Theme = entry.Builtin;
                try { ThemeManager.Theme = entry.Builtin; } catch { }
            }
            else if (entry.PaletteId is not null)
            {
                // 配色即主题: 整界面套用同一套 scheme
                Config.ColorSchemeId = entry.PaletteId;
                ColorSchemes.Apply(ColorSchemes.Find(entry.PaletteId));
            }
        };

        // 语言列表
        _langList = new ListView<string> { Width = 20, Height = 2 };
        ScrollOnly(_langList); // 同上: 滚轮只滚动视图
        _langList.SetSource(new ObservableCollection<string>(Loc.LanguageNames));
        _langList.ValueChanged += (_, _) =>
        {
            var name = _langList.Value;
            Config.Language = name == Loc.LanguageNames[1] ? "en-US" : "zh-CN";
        };

        _fpsLimit = new TextField { Text = Config.RenderFpsLimit.ToString(), Width = 10 };
        _isolationNames = new[]
        {
            Loc.T("set.isolationNone"), Loc.T("set.isolationMods"), Loc.T("set.isolationFull"),
        };
        _isolationDrop = new DropDownList { Width = Dim.Percent(60) };
        _isolationDrop.Source = new ListWrapper<string>(
            new System.Collections.ObjectModel.ObservableCollection<string>(_isolationNames));
        _isolationDrop.Text = _isolationNames[Math.Clamp((int)Config.Isolation, 0, 2)];
        _startupAnim = new CheckBox { Text = Loc.T("set.startupAnimationOn"), Value = Config.StartupAnimation ? CheckState.Checked : CheckState.UnChecked };
        _disableAnim = new CheckBox { Text = Loc.T("set.disableAnim"), Value = Config.DisableAnimations ? CheckState.Checked : CheckState.UnChecked };
        _lowMem = new CheckBox { Text = Loc.T("set.lowMem"), Value = Config.LowMemoryMode ? CheckState.Checked : CheckState.UnChecked };
        _inputDebounce = new TextField { Text = Config.InputDebounceMs.ToString(), Width = 10 };
        _poolSize = new TextField { Text = Config.HttpConnectionPoolSize.ToString(), Width = 10 };
        _dnsPreheat = new CheckBox { Text = Loc.T("set.dnsPreheat"), Value = Config.DnsPreheat ? CheckState.Checked : CheckState.UnChecked };
        _listCache = new TextField { Text = Config.ListViewCacheLines.ToString(), Width = 10 };
        _cacheMax = new TextField { Text = Config.CacheMaxSizeMb.ToString(), Width = 10 };
        _taskCapacity = new TextField { Text = Config.TaskCapacity.ToString(), Width = 10 };

        _maxThreads = new TextField { Text = Config.MaxDownloadThreads.ToString(), Width = 10 };
        _timeout = new TextField { Text = Config.DownloadTimeoutSec.ToString(), Width = 10 };
        _dlSourceList = new ListView<string> { Width = 40, Height = 3 };
        _dlSourceList.SetSource(new ObservableCollection<string>(DlSources));
        _dlSourceList.ValueChanged += (_, _) =>
        {
            var idx = Array.IndexOf(DlSources, _dlSourceList.Value);
            Config.DownloadSource = idx switch { 1 => "official", 2 => "bmclapi", _ => "auto" };
        };
        _logLevelList = new ListView<string> { Width = 20, Height = 4 };
        _logLevelList.SetSource(new ObservableCollection<string>(LogLevels));
        _logLevelList.ValueChanged += (_, _) =>
        {
            var idx = Array.IndexOf(LogLevels, _logLevelList.Value);
            if (idx >= 0) Config.LogLevel = (LogLevelMode)idx;
        };
        _showDebug = new CheckBox { Text = Loc.T("set.showDebug"), Value = Config.ShowDebugInfo ? CheckState.Checked : CheckState.UnChecked };
        _cfKey = new TextField { Text = Config.CurseForgeApiKey, Width = Dim.Percent(60) };
        _modNameList = new ListView<string> { Width = 24, Height = 5 };
        _modNameList.SetSource(new ObservableCollection<string>(ModNameStyles));
        _modNameList.ValueChanged += (_, _) =>
        {
            var idx = Array.IndexOf(ModNameStyles, _modNameList.Value);
            if (idx >= 0) Config.ModNameStyle = idx;
        };
        _ignoreSsl = new CheckBox { Text = Loc.T("set.ignoreSsl"), Value = Config.IgnoreSslErrors ? CheckState.Checked : CheckState.UnChecked };
        // 切换后立即重建连接, 不用等下次启动 (下载前也能生效)
        _ignoreSsl.ValueChanged += (_, _) => GameDownloader.Configure(_ignoreSsl.Value == CheckState.Checked);

        // 滚轮滚动整页 (鼠标落在 panel 空白处时)
        _panel.MouseEvent += OnWheelScroll;

        // 右侧竖滚动条: 显示浏览位置, 鼠标停在上面滚轮也能带动左侧页面
        _scrollBar = new ScrollBar
        {
            Orientation = Terminal.Gui.ViewBase.Orientation.Vertical,
            X = Pos.AnchorEnd(2),
            Y = 0,
            Width = 1,
            Height = Dim.Fill(),
            VisibilityMode = ScrollBarVisibilityMode.Auto, // 内容不超高时自动隐藏
            CanFocus = false,
        };
        _scrollBar.ValueChanged += (_, _) => SyncPanelFromBar();
        _scrollBar.MouseEvent += (_, e) => OnWheelScroll(_scrollBar, e);

        Add(navBox, _panel, _scrollBar);
        RefreshMsState(); // 已登录时按钮显示"退出正版", 状态行显示正版名字
        ShowSection(0);
    }

    public override void OnPageEnter()
    {
        // 切进来重排: 任务栏宽度/窗口尺寸可能变过, 否则控件会停在旧尺寸
        SetNeedsLayout();
        Layout();
        SetNeedsDraw();
        SyncScrollBar();
    }

    private void ShowSection(int i)
    {
        _panel.RemoveAll();
        _panel.ScrollVertical(-1000); // 切分类回到顶部

        switch (i)
        {
            case 0: LayoutLaunch(); break;
            case 1: LayoutPersonalize(); break;
            case 2: LayoutPerformance(); break;
            case 3: LayoutMisc(); break;
        }
        _panel.SetNeedsDraw();

        // 布局完一帧后再算内容总高 (Frame 这时才有值), 高过窗口就能滚
        Application.AddTimeout(TimeSpan.FromMilliseconds(1), () =>
        {
            int bottom = 0;
            foreach (var v in _panel.SubViews)
                bottom = Math.Max(bottom, v.Frame.Y + v.Frame.Height);

            // 递归绑滚轮: groupbox 边框/标题/内部空白、Label、CheckBox 全都要能带动整页
            // (ListView/TextField 自己处理滚轮, 跳过)
            void BindWheel(View v)
            {
                if (v is ListView or TextField or TextView)
                    return;
                v.MouseEvent -= OnWheelScroll;
                v.MouseEvent += OnWheelScroll;
                foreach (var child in v.SubViews)
                    BindWheel(child);
            }
            BindWheel(_panel);
            int w = Math.Max(_panel.Viewport.Width, 40);
            _panel.SetContentSize(new System.Drawing.Size(w, Math.Max(bottom + 2, _panel.Viewport.Height)));
            SyncScrollBar();
            return false;
        });
    }

    /// <summary>把面板的滚动状态推给滚动条 (内容高度 / 可视高度 / 当前位置)</summary>
    private void SyncScrollBar()
    {
        if (_scrollBar is null)
            return;

        _syncingScroll = true;
        try
        {
            var content = _panel.GetContentSize();
            _scrollBar.ScrollableContentSize = Math.Max(content.Height, 1);
            _scrollBar.VisibleContentSize = Math.Max(_panel.Viewport.Height, 1);
            _scrollBar.Value = Math.Clamp(_panel.Viewport.Y, 0, Math.Max(content.Height - _panel.Viewport.Height, 0));
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    /// <summary>拖滑块 / 点滚动条 → 带动面板</summary>
    private void SyncPanelFromBar()
    {
        if (_syncingScroll)
            return;

        var delta = _scrollBar.Value - _panel.Viewport.Y;
        if (delta != 0)
            _panel.ScrollVertical(delta);

        // 面板可能因为边界 clamp 没走到滑块的位置, 反向校一次
        SyncScrollBar();
        _panel.SetNeedsDraw();
    }

    /// <summary>列表滚轮只滚动视图, 不改变选中项 (Scroll 而非 Move, 不触发 ValueChanged)</summary>
    private static void ScrollOnly(ListView list)
    {
        list.MouseEvent += (_, e) =>
        {
            if (e.Flags.HasFlag(MouseFlags.WheeledDown))
            {
                list.InvokeCommand(Command.ScrollDown);
                e.Handled = true;
            }
            else if (e.Flags.HasFlag(MouseFlags.WheeledUp))
            {
                list.InvokeCommand(Command.ScrollUp);
                e.Handled = true;
            }
        };
    }

    /// <summary>滚轮滚动 (panel 与各 groupbox 共用)</summary>
    private void OnWheelScroll(object? sender, Mouse e)
    {
        if (e.Flags.HasFlag(MouseFlags.WheeledDown))
        {
            _panel.ScrollVertical(1);
            SyncScrollBar();
            e.Handled = true;
        }
        else if (e.Flags.HasFlag(MouseFlags.WheeledUp))
        {
            _panel.ScrollVertical(-1);
            SyncScrollBar();
            e.Handled = true;
        }
    }

    // ---------- 微软正版登录 ----------

    /// <summary>账户状态文案 (正版显示名字, 离线显示离线名)</summary>
    private string MsStateText() => AuthState.IsMicrosoft(Config)
        ? Loc.T("set.msStateOn", Config.MsName)
        : Loc.T("set.msStateOff", Config.PlayerName);

    private void RefreshMsState()
    {
        _msStateLbl.Text = MsStateText();
        _msLoginBtn.Text = Loc.T(AuthState.IsMicrosoft(Config) ? "set.msLogout" : "set.msLogin");
        _panel.SetNeedsDraw();
    }

    /// <summary>按钮: 已登录 → 退出; 未登录 → 开始 device code 流程</summary>
    private void OnMicrosoftAuthButton()
    {
        if (AuthState.IsMicrosoft(Config))
        {
            var r = MessageBox.Query(Application.Instance, Loc.T("ms.title"),
                "确定退出正版登录? 会切回离线模式,需要重新登录才能联机。",
                "退出", "取消");
            if (r != 0)
                return;
            AuthState.Logout(Config);
            RefreshMsState();
            return;
        }

        var clientId = (_msClientId.Text ?? "").Trim();
        if (clientId.Length == 0)
        {
            MessageBox.ErrorQuery(Application.Instance, Loc.T("common.error"),
                Loc.T("ms.needClientId"), Loc.T("common.confirm"));
            return;
        }

        Config.MicrosoftClientId = clientId;
        Task.Run(async () =>
        {
            try
            {
                var info = await MicrosoftAuth.RequestDeviceCodeAsync(clientId, CancellationToken.None)
                    .ConfigureAwait(false);
                Application.Invoke(() => ShowLoginDialog(info));
            }
            catch (Exception ex)
            {
                Application.Invoke(() => MessageBox.ErrorQuery(Application.Instance,
                    Loc.T("common.error"), Loc.T("ms.failed", ex.Message), Loc.T("common.confirm")));
            }
        });
    }

    /// <summary>显示验证码窗口 + 后台轮询授权结果</summary>
    private void ShowLoginDialog(DeviceCodeInfo info)
    {
        using var cts = new CancellationTokenSource();
        var dialog = new Dialog
        {
            Title = Loc.T("ms.title"),
            Width = Math.Min(78, Application.Driver?.Cols - 4 ?? 70),
            Height = 14,
        };

        var title1 = new Label { Text = Loc.T("ms.step1"), X = 1, Y = 0, Width = Dim.Fill(2) };
        var urlLbl = new Label { Text = info.VerificationUri, X = 2, Y = 1, Width = Dim.Fill(3) };
        var title2 = new Label { Text = Loc.T("ms.step2"), X = 1, Y = 3 };
        var codeLbl = new Label { Text = info.UserCode, X = 2, Y = 4 };
        var status = new Label { Text = Loc.T("ms.waiting"), X = 1, Y = 6, Width = Dim.Fill(2) };
        var cancel = new Button { Text = Loc.T("common.cancel"), X = Pos.Center(), Y = Pos.AnchorEnd(2) };
        cancel.Accepting += (_, _) =>
        {
            cts.Cancel();
            dialog.RequestStop();
        };

        dialog.Add(title1, urlLbl, title2, codeLbl, status, cancel);

        // 自动打开浏览器 (失败也不影响, 用户可以手动输网址)
        TryOpenBrowser(info.VerificationUri);

        Task.Run(async () =>
        {
            try
            {
                var account = await AuthState.LoginAsync(Config, info,
                    msg => Application.Invoke(() =>
                    {
                        try { status.Text = msg; } catch { /* 窗口已关 */ }
                    }), cts.Token).ConfigureAwait(false);

                Application.Invoke(() =>
                {
                    dialog.RequestStop();
                    RefreshMsState();
                    MessageBox.Query(Application.Instance, Loc.T("ms.title"),
                        Loc.T("ms.success", account.Name), Loc.T("common.confirm"));
                });
            }
            catch (OperationCanceledException)
            {
                // 用户点了取消
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    dialog.RequestStop();
                    MessageBox.ErrorQuery(Application.Instance, Loc.T("common.error"),
                        Loc.T("ms.failed", ex.Message), Loc.T("common.confirm"));
                });
            }
        });

        Application.Run(dialog);
        dialog.Dispose();
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打不开就让用户手动复制网址
        }
    }

    /// <summary>解析输入框整数, 乱填/越界回退</summary>
    private static int ParseInt(object? text, int def, int min, int max)
    {
        var s = text?.ToString()?.Trim();
        return int.TryParse(s, out var v) ? Math.Clamp(v, min, max) : def;
    }

    /// <summary>建一个直角 groupbox</summary>
    private static FrameView Box(string title, int y, int height) => new()
    {
        Title = title,
        X = 0,
        Y = y,
        Width = Dim.Fill(1),
        Height = height,
        BorderStyle = LineStyle.Single,
    };

    // ---------- 启动 ----------
    private void LayoutLaunch()
    {
        int y = 1;
        _playerName.Y = y; _panel.Add(Row(Loc.T("set.playerName"), _playerName, ref y));
        _msClientId.Y = y; _panel.Add(Row(Loc.T("set.msClientId"), _msClientId, ref y));

        _msStateLbl.X = 1; _msStateLbl.Y = y; y += 1;
        _panel.Add(_msStateLbl);

        _msLoginBtn.X = 1; _msLoginBtn.Y = y; y += 2; // Button 自动高度 2 行
        _panel.Add(_msLoginBtn);

        _mcPath.Y = y; _panel.Add(Row(Loc.T("set.mcPath"), _mcPath, ref y));

        // Java 路径这一行比别处长一个「下载」按钮（缺 Java 的机器不用自己去官网翻）
        _javaDrop.Y = y;
        _javaDrop.X = Pos.Percent(35);
        var javaDlBtn = new Button
        {
            Text = "下载 Java",
            X = Pos.Right(_javaDrop) + 1,
            Y = y,
            NoPadding = true,
        };
        javaDlBtn.Accepting += (_, _) => ShowJavaDownloadDialog();
        _panel.Add(new Label { Text = Loc.T("set.javaPath"), X = 1, Y = y });
        _panel.Add(_javaDrop, javaDlBtn);
        y += 2;

        _jvmArgs.Y = y; _panel.Add(Row(Loc.T("set.jvmArgs"), _jvmArgs, ref y));

        _ramModeDrop.Y = y; _panel.Add(Row("内存模式", _ramModeDrop, ref y));
        _maxMem.Y = y; _panel.Add(Row(Loc.T("set.maxMemory"), _maxMem, ref y));
        _minMem.Y = y; _panel.Add(Row(Loc.T("set.minMemory"), _minMem, ref y));
        SyncMemEnabled(); // 初始状态跟着模式走
        _winWidth.Y = y; _panel.Add(Row(Loc.T("set.windowWidth"), _winWidth, ref y));
        _winHeight.Y = y; _panel.Add(Row(Loc.T("set.windowHeight"), _winHeight, ref y));
        y += 1;
        _fullScreen.X = 1; _fullScreen.Y = y; y += 1; _panel.Add(_fullScreen);
        y += 1;
        _isolationDrop.Y = y;
        _panel.Add(Row(Loc.T("set.versionIsolation"), _isolationDrop, ref y));

        var save = new Button { Text = Loc.T("set.saveBack"), Y = y + 1, X = Pos.Center() };
        save.Accepting += (_, _) => OnSave();
        _panel.Add(save);
    }

    // ---------- 个性化 ----------
    private void LayoutPersonalize()
    {
        int y = 0; // 贴顶, 与其他分类保持一致

        // ===== 主页内容 =====
        var homeBox = Box(Loc.T("set.homeContent"), y, 1);
        int hy = 1;
        _homeModeList.X = 3;
        _homeModeList.Y = hy;
        _homeModeList.Text = HomeModes[(int)Config.HomePage];
        homeBox.Add(_homeModeList);
        hy += 2;
        _homeSource.Y = hy;
        homeBox.Add(Row(Loc.T("set.homeSource"), homeBox, _homeSource, ref hy));
        homeBox.Height = hy + 1;
        _panel.Add(homeBox);
        y += hy + 2;

        // ===== 启动动画 (放最上面, 首屏直接可见) =====
        var animBox = Box(Loc.T("set.startupAnimation"), y, 4);
        _startupAnim.X = 1;
        _startupAnim.Y = 1;
        animBox.Add(_startupAnim);
        _panel.Add(animBox);
        y += 5;

        // ===== 外观: 主题/配色 + 语言 =====
        var lookBox = Box(Loc.T("set.appearance"), y, 1);
        int ly = 1;

        _themeList.X = 1;
        _themeList.Y = ly;
        _themeSyncing = true;
        _themeList.SelectedItem = _themeCatalog[ThemeCatalog.IndexOf(_themeCatalog, Config.Theme, Config.ColorSchemeId)].Display;
        _themeSyncing = false;
        lookBox.Add(_themeList);
        ly += _themeList.Frame.Height > 0 ? _themeList.Frame.Height : 10;
        ly += 1;

        _langList.X = 1;
        _langList.Y = ly;
        _langList.SelectedItem = Loc.LanguageNames[Loc.LangIndex(Config.Language)];
        lookBox.Add(_langList);
        ly += (_langList.Frame.Height > 0 ? _langList.Frame.Height : 2) + 1;

        lookBox.Height = ly + 1;
        _panel.Add(lookBox);
        y += ly + 2;

        var save = new Button { Text = Loc.T("set.save"), Y = y + 1, X = Pos.Center() };
        save.Accepting += (_, _) => OnSave();
        _panel.Add(save);
    }

    // ---------- 性能 ----------
    private void LayoutPerformance()
    {
        // 渲染性能 box
        int y = 1;
        var renderBox = Box(Loc.T("set.renderPerf"), 0, 1);
        _fpsLimit.Y = y;
        renderBox.Add(Row(Loc.T("set.fpsLimit"), renderBox, _fpsLimit, ref y));
        _disableAnim.X = 1; _disableAnim.Y = y; y += 1; renderBox.Add(_disableAnim);
        _inputDebounce.Y = y;
        renderBox.Add(Row(Loc.T("set.inputDebounce"), renderBox, _inputDebounce, ref y));
        _listCache.Y = y;
        renderBox.Add(Row(Loc.T("set.listCache"), renderBox, _listCache, ref y));
        renderBox.Height = y + 1;
        _panel.Add(renderBox);

        // 网络性能 box
        int outerY = y + 2;
        y = 1;
        var netBox = Box(Loc.T("set.netPerf"), outerY, 1);
        _poolSize.Y = y;
        netBox.Add(Row(Loc.T("set.poolSize"), netBox, _poolSize, ref y));
        _dnsPreheat.X = 1; _dnsPreheat.Y = y; y += 1; netBox.Add(_dnsPreheat);
        _cacheMax.Y = y;
        netBox.Add(Row(Loc.T("set.cacheMax"), netBox, _cacheMax, ref y));
        netBox.Height = y + 1;
        _panel.Add(netBox);

        // 任务栏 box
        outerY += y + 2;
        y = 1;
        var taskBox = Box(Loc.T("set.taskBox"), outerY, 1);
        _taskCapacity.Y = y;
        taskBox.Add(Row(Loc.T("set.taskCapacity"), taskBox, _taskCapacity, ref y));
        taskBox.Height = y + 1;
        _panel.Add(taskBox);
        outerY += y + 2;

        _lowMem.X = 1; _lowMem.Y = outerY; outerY += 2; _panel.Add(_lowMem);

        var apply = new Button { Text = Loc.T("set.applyPerf"), Y = outerY, X = Pos.Center() };
        apply.Accepting += (_, _) => OnSave();
        _panel.Add(apply);
    }

    // ---------- 其他 ----------
    private void LayoutMisc()
    {
        // 下载 box
        int y = 1;
        var dlBox = Box(Loc.T("set.dlBox"), 0, 1);
        _dlSourceList.X = 2;
        _dlSourceList.Y = y;
        _dlSourceList.SelectedItem = DlSources[Config.DownloadSource switch { "official" => 1, "bmclapi" => 2, _ => 0 }];
        dlBox.Add(_dlSourceList);
        y += 4;
        _maxThreads.Y = y;
        dlBox.Add(Row(Loc.T("set.maxThreads"), dlBox, _maxThreads, ref y));
        _timeout.Y = y;
        dlBox.Add(Row(Loc.T("set.timeout"), dlBox, _timeout, ref y));
        _cfKey.Y = y; dlBox.Add(Row(Loc.T("set.cfKey"), dlBox, _cfKey, ref y));
        _modNameList.SelectedItem = ModNameStyles[Math.Clamp(Config.ModNameStyle, 0, ModNameStyles.Length - 1)];
        _modNameList.Y = y;
        dlBox.Add(Row(Loc.T("set.modNameStyle"), dlBox, _modNameList, ref y));
        y += 4; // 5 项列表占 5 行, Row 只 +2, 这里补足
        _ignoreSsl.X = 1; _ignoreSsl.Y = y; y += 1; dlBox.Add(_ignoreSsl);
        // FrameView 内容区 = Height − 2 (上下边框), 写 y+1 会把最后一行裁掉
        dlBox.Height = y + 2;
        _panel.Add(dlBox);

        // 日志 box (紧跟下载框, 不重叠)
        int outerY = y + 3;
        y = 1;
        var logBox = Box("日志与调试", outerY, 1);
        _logLevelList.X = 2;
        _logLevelList.Y = y;
        _logLevelList.SelectedItem = LogLevels[(int)Config.LogLevel];
        logBox.Add(_logLevelList);
        y += 5;
        _showDebug.X = 1; _showDebug.Y = y; y += 2; logBox.Add(_showDebug);
        logBox.Height = y + 1;
        _panel.Add(logBox);
        outerY += y + 2;

        var save = new Button { Text = Loc.T("set.save"), Y = outerY, X = Pos.Center() };
        save.Accepting += (_, _) => OnSave();
        _panel.Add(save);
    }

    // ---------- Java 自动下载 ----------

    /// <summary>选版本 → 后台下载解压 → 刷新 Java 下拉</summary>
    private void ShowJavaDownloadDialog()
    {
        var majors = JavaInstaller.OfferMajors;
        var labels = new[]
        {
            "Java 21   (1.20.5+)",
            "Java 17   (1.18~1.20.4)",
            "Java 8    (≤1.16)",
        };
        if (labels.Length != majors.Length)
            return;

        var dlg = new Dialog
        {
            Title = "下载 Java(Eclipse Temurin)",
            Width = 52,
            Height = 12,
            BorderStyle = LineStyle.Single,
        };
        var src = new ObservableCollection<string>(labels);
        var list = new ListView<string> { X = 1, Y = 1, Width = Dim.Fill(2), Height = 5 };
        list.SetSource(src);
        list.SelectedItem = src[1]; // 默认 17，覆盖最多版本

        var ok = new Button { Text = "开始下载", X = 1, Y = 7, IsDefault = true };
        var cancel = new Button { Text = "取消", X = Pos.Right(ok) + 2, Y = 7 };

        var chosen = -1;
        void Confirm()
        {
            chosen = list.SelectedItem is { } s ? src.IndexOf(s) : -1;
            dlg.RequestStop();
        }
        ok.Accepting += (_, _) => Confirm();
        list.Accepted += (_, _) => Confirm();
        cancel.Accepting += (_, _) => dlg.RequestStop();

        dlg.Add(list, ok, cancel);
        list.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();

        if (chosen < 0 || chosen >= majors.Length)
            return;
        var major = majors[chosen];

        TaskManager.Submit($"下载 Java {major}", async (task, ct) =>
        {
            try
            {
                task.LogLine($"查询 Adoptium 最新的 Java {major}...");
                var pkg = await JavaInstaller.FetchLatestAsync(major, ct).ConfigureAwait(false);
                if (pkg is null)
                {
                    task.LogLine("没查到这个版本的 JDK(可能是网络问题或该版本已下架)");
                    Application.Invoke(() => MessageBox.ErrorQuery(Application.Instance, "下载 Java",
                        $"没能查到 Java {major} 的下载地址。\n检查网络后重试。", "好"));
                    return;
                }

                task.LogLine(pkg.Display);
                var exe = await JavaInstaller.InstallAsync(
                    pkg,
                    (pct, msg) =>
                    {
                        task.Report(pct);
                        task.LogLine(msg);
                    },
                    ct).ConfigureAwait(false);

                task.Report(1.0);
                task.LogLine($"完成: {exe}");
                Application.Invoke(() =>
                {
                    RefreshJavaList();
                    MessageBox.Query(Application.Instance, "下载 Java",
                        $"Java {major} 装好了,已加进上面的列表。\n\n{exe}", "好");
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                task.LogLine("已取消");
                throw;
            }
            catch (Exception ex)
            {
                LauncherLog.Error($"下载 Java {major} 失败: {ex}");
                foreach (var line in Net.Describe(ex).Split('\n'))
                    task.LogLine(line.TrimEnd());
                Application.Invoke(() => MessageBox.ErrorQuery(Application.Instance,
                    "下载 Java 失败", Net.Brief(ex), "好"));
                throw;
            }
        });
    }

    /// <summary>重扫 Java 并刷新下拉（保留用户当前选择）</summary>
    private void RefreshJavaList()
    {
        _suppressJavaChange = true;
        try
        {
            var keep = Config.JavaPath;
            _javas = JavaDetector.Detect();
            var items = new List<string> { Loc.T("set.javaAuto") };
            items.AddRange(_javas.Select(j => j.Display));
            _javaDrop.Source = new ListWrapper<string>(new ObservableCollection<string>(items));
            _javaDrop.Text = string.IsNullOrWhiteSpace(keep)
                ? items[0]
                : (_javas.FirstOrDefault(j => string.Equals(j.Path, keep, StringComparison.OrdinalIgnoreCase))?.Display ?? keep);
        }
        finally
        {
            _suppressJavaChange = false;
        }
    }

    // ---------- 工具 ----------

    /// <summary>内存模式联动：只有"手动指定"才让填最大/最小内存</summary>
    private void SyncMemEnabled()
    {
        var manual = _ramModeDrop.Text == "手动指定";
        _maxMem.Enabled = manual;
        _minMem.Enabled = manual;
        _maxMem.SetNeedsDraw();
        _minMem.SetNeedsDraw();
    }

    /// <summary>一行「标签 + 输入控件」, 控件挂到 _panel。注意 input 必须一起 Add, 否则文本框不显示。</summary>
    private Label Row(string caption, View input, ref int y)
        => Row(caption, _panel, input, ref y);

    /// <summary>一行「标签 + 输入控件」, 控件挂到指定容器 (groupbox 内必须用这个重载, 否则控件跑到 _panel 上)</summary>
    private Label Row(string caption, View container, View input, ref int y)
    {
        input.Y = y;
        input.X = Pos.Percent(35);
        var l = new Label { Text = caption, X = 1, Y = y };
        container.Add(input);
        y += 2;
        return l;
    }

    private void OnSave()
    {
        try
        {
            Config.PlayerName = _playerName.Text.Trim();
            Config.MicrosoftClientId = (_msClientId.Text ?? "").Trim();
            Config.MinecraftPath = _mcPath.Text.Trim();
            // Java 路径由下拉框 ValueChanged 即时写入 Config.JavaPath
            Config.JvmArgs = _jvmArgs.Text.Trim();
            Config.HomePageSource = _homeSource.Text.Trim();

            Config.RenderFpsLimit = ParseInt(_fpsLimit.Text, 0);
            Config.DisableAnimations = _disableAnim.Value == CheckState.Checked;
            Config.StartupAnimation = _startupAnim.Value == CheckState.Checked;
            var isoIdx = Array.IndexOf(_isolationNames, _isolationDrop.Text);
            Config.Isolation = (IsolationMode)Math.Clamp(isoIdx, 0, 2);
            Config.RamMode = _ramModeDrop.Text == "手动指定" ? 1 : 0;
            Config.MaxMemoryMb = ParseInt(_maxMem.Text, 2048, 256, 65536);
            Config.MinMemoryMb = Math.Min(ParseInt(_minMem.Text, 512, 128, 65536), Config.MaxMemoryMb);
            Config.WindowWidth = ParseInt(_winWidth.Text, 854, 320, 8192);
            Config.WindowHeight = ParseInt(_winHeight.Text, 480, 240, 8192);
            Config.FullScreen = _fullScreen.Value == CheckState.Checked;
            Config.LowMemoryMode = _lowMem.Value == CheckState.Checked;
            Config.InputDebounceMs = Math.Clamp(ParseInt(_inputDebounce.Text, 15), 0, 500);
            Config.HttpConnectionPoolSize = Math.Clamp(ParseInt(_poolSize.Text, 32), 1, 512);
            Config.DnsPreheat = _dnsPreheat.Value == CheckState.Checked;
            Config.ListViewCacheLines = Math.Clamp(ParseInt(_listCache.Text, 2000), 100, 100000);
            Config.CacheMaxSizeMb = Math.Clamp(ParseInt(_cacheMax.Text, 2048), 64, 65536);
            Config.TaskCapacity = Math.Clamp(ParseInt(_taskCapacity.Text, 16), TaskManager.MinCapacity, TaskManager.MaxCapacity);
            TaskManager.Capacity = Config.TaskCapacity;

            Config.MaxDownloadThreads = Math.Clamp(ParseInt(_maxThreads.Text, 64), 1, 512);
            Config.DownloadTimeoutSec = Math.Clamp(ParseInt(_timeout.Text, 60), 5, 600);
            Config.ShowDebugInfo = _showDebug.Value == CheckState.Checked;
            Config.IgnoreSslErrors = _ignoreSsl.Value == CheckState.Checked;
            Config.CurseForgeApiKey = (_cfKey.Text ?? "").Trim();
            // 文件名样式: ValueChanged 已即时写入, 这里兜底防呆 (没手选过就保持原值)
            var nameIdx = Array.IndexOf(ModNameStyles, _modNameList.Value ?? "");
            if (nameIdx >= 0)
                Config.ModNameStyle = nameIdx;

            // Theme 已在 ValueChanged 里即时写入 Config, 这里兜底防呆
            if (string.IsNullOrEmpty(Config.Theme))
                Config.Theme = "Default";

            // 日志级别即时生效
            LauncherLog.Level = Math.Clamp((int)Config.LogLevel, 0, 3);
            LauncherLog.Info($"设置已保存(日志级别={Config.LogLevel})");

            Config.Save();
            MessageBox.Query(Application.Instance, Loc.T("common.settings"), Loc.T("set.savedTo", LauncherConfig.RootDir), Loc.T("common.confirm"));
            _backToLaunch();
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery(Application.Instance, Loc.T("common.error"), Loc.T("set.saveFailed", ex.Message), Loc.T("common.confirm"));
        }
    }

    private static int ParseInt(string s, int def) => int.TryParse(s.Trim(), out var v) ? v : def;
}
