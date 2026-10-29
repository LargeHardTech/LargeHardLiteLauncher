using LargeHardLiteLauncher.Core;
using Terminal.Gui;
using Terminal.Gui.Configuration;
using Terminal.Gui.Input;

namespace LargeHardLiteLauncher.UI;

/// <summary>
/// 主窗口：双线边框 + 自绘标签栏（启动/下载/设置/更多）+ 左侧内容区 + 右侧任务栏(可折叠)。
/// 页面切换完全手动管理（显隐 + SetFocus），不依赖 Tabs 的焦点驱动，杜绝标签头/内容撕裂。
/// 标签栏用 Label + 窗口级鼠标坐标路由（TG 2.5.0 的 Button 鼠标点击会被 ShadowView 吞掉）。
/// </summary>
public class MainWindow : Window
{
    private readonly LauncherConfig _config = LauncherConfig.Load();
    private readonly Dictionary<PageId, PageBase> _pages = new();
    private readonly List<(PageId Id, Label Label)> _tabItems = new();
    private View _content = null!;
    private TaskPanel _taskPanel = null!;
    private View _splitter = null!;
    /// <summary>拖动分隔条时的目标宽度 (松手才真正应用)</summary>
    private int _dragWidth;
    private PageId _current;
    private bool _collapsed;
    private bool _pageShown; // 是否已初始化过页面 (保证首次 OnPageEnter 一定执行)
    private DateTime _lastPanelWidthSave; // 任务栏宽度保存节流时间戳
    private readonly bool _duplicateInstance; // 检测到已运行实例 (只提示, 不加载控件)

    /// <summary>已有实例在运行时提示并退出</summary>
    private void ShowDuplicateNotice()
    {
        MessageBox.ErrorQuery(Application.Instance,
            Loc.T("app.duplicateTitle"), Loc.T("app.duplicateBody"), Loc.T("common.confirm"));
        Application.RequestStop();
    }
    private bool _dragging; // 正在拖动分隔条

    public MainWindow(bool duplicateInstance = false, string? initialJar = null)
    {
        _duplicateInstance = duplicateInstance;
        Loc.Current = Loc.Parse(_config.Language);
        // 日志级别来自配置
        LauncherLog.Level = Math.Clamp((int)_config.LogLevel, 0, 3);
        LauncherLog.Info($"启动 LauncherHardLite v0.1.0 | 语言={_config.Language} 主题={_config.Theme} 配色={_config.ColorSchemeId}");
        LauncherLog.Info($"配置目录: {LauncherConfig.RootDir}");
        LauncherLog.Info($"游戏目录: {_config.MinecraftPath}");
        Title = Loc.T("app.title");
        BorderStyle = LineStyle.Double;

        // 启动时应用已保存的主题 (须在创建任何视图前)
        try
        {
            ThemeManager.Theme = _config.Theme;
        }
        catch { /* 主题名无效则保持默认 */ }

        // 主题切换后全量刷新: 清掉所有 View 的 Scheme 缓存 (HasScheme 的 View 会用旧 Scheme 对象)
        ThemeManager.ThemeChanged += (_, _) => RefreshSchemes();
        // 柔和配色应用后同样全量刷新
        ColorSchemes.Applied += RefreshSchemes;

        // 启动时应用已保存的配色 (作为主题套用)
        var savedPalette = ColorSchemes.Find(_config.ColorSchemeId);
        if (savedPalette is not null)
            ColorSchemes.Apply(savedPalette);

        // 任务栏容量来自配置 (必须在 BuildContent 前, TaskPanel 构造时要按容量铺槽位)
        TaskManager.Capacity = Math.Clamp(_config.TaskCapacity, TaskManager.MinCapacity, TaskManager.MaxCapacity);

        BuildTabBar();
        BuildContent();

        // 恢复任务栏折叠状态 (不写盘, 避免启动即保存)
        if (!_config.TaskPanelExpanded)
        {
            _collapsed = true;
            _taskPanel.Visible = false;
            _splitter.Visible = false;
            _content.Width = Dim.Fill();
        }

        // 默认停在启动页
        ShowPage(PageId.Launch);

        // 命令行带了 .jar（LargeHardLiteLauncher xxx.jar）→ 直接进 JAR 启动页并预填
        if (!string.IsNullOrWhiteSpace(initialJar) && _pages.TryGetValue(PageId.JarLaunch, out var jp)
            && jp is JarLaunchPage jarPage)
        {
            jarPage.Prefill(initialJar);
            ShowPage(PageId.JarLaunch);
        }

        // 窗口级鼠标路由: 标签栏点击在此裁决, 不依赖子视图命中测试
        MouseEvent += OnWindowMouseEvent;

        // 标题栏点击: BorderView 是 Adornment, 冒泡会跳过父窗口, 必须直接订阅
        // (与 ShadowView 跳过 Button 同一机理)
        if (Border.View is { } borderView)
            borderView.MouseEvent += OnBorderMouseEvent;

        // 启动动画 (设置里可关)
        // 重复实例: 动画只播到假边框展开完, 然后弹提示退出, 不加载控件
        if (_config.StartupAnimation)
            StartupAnimation.Play(this, new List<View>(SubViews),
                flashControls: !_duplicateInstance,
                onFinished: _duplicateInstance ? ShowDuplicateNotice : null);
        else if (_duplicateInstance)
            ShowDuplicateNotice();
    }

    /// <summary>折叠态下单击窗口标题栏 (顶部边框行) 展开任务栏</summary>
    private void OnBorderMouseEvent(object? sender, Mouse e)
    {
        if (!_collapsed || !e.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            return;
        if (e.ScreenPosition.Y == Frame.Y)
            ToggleCollapse();
    }

    private void BuildTabBar()
    {
        var bar = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            CanFocus = false,
        };

        // 页面列表直接从 PageId 枚举生成, 加页面只改枚举 + TabName，不用在两处维护
        var tabs = Enum.GetValues<PageId>()
            .Select(id => (Id: id, Name: TabName(id)))
            .ToArray();

        int x = 1;
        foreach (var (id, name) in tabs)
        {
            var lbl = new Label
            {
                Text = name,
                X = x,
                Y = 0,
                Height = 1,
                CanFocus = false,
            };
            bar.Add(lbl);
            _tabItems.Add((id, lbl));
            // 汉字占 2 列, 必须用列宽步进, 否则标签互相重叠
            x += name.GetColumns() + 2;
        }

        Add(bar);
    }

    private void BuildContent()
    {
        int panelWidth = Math.Clamp(_config.TaskPanelWidth, TaskPanel.MinWidth, TaskPanel.MaxWidth);
        _content = new View
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(panelWidth), // 右侧留给任务栏 + 分隔条
            Height = Dim.Fill(), // 贴齐窗口底边框
            CanFocus = true, // 容器可聚焦, 子页面才能 SetFocus (v2 规则: 父不可聚焦则子不可设焦点)
        };
        Add(_content);

        _pages[PageId.Launch] = new LaunchPage(_config);
        _pages[PageId.Download] = new DownloadPage(_config);
        _pages[PageId.Terracotta] = new TerracottaPage(_config, () => ShowPage(PageId.Terracotta));
        _pages[PageId.JarLaunch] = new JarLaunchPage(_config, () => ShowPage(PageId.JarLaunch));
        _pages[PageId.Settings] = new SettingsPage(_config, () => ShowPage(PageId.Launch));
        _pages[PageId.More] = new MorePage(_config);

        foreach (var (_, page) in _pages)
        {
            page.Visible = false;
            _content.Add(page);
        }

        _taskPanel = new TaskPanel(ToggleCollapse);
        _taskPanel.Width = panelWidth;
        _taskPanel.X = Pos.Right(_content);
        Add(_taskPanel);

        // 分隔条 (WinForms SplitContainer 手感): 拖动调宽, 双击折叠
        _splitter = new View
        {
            Text = "┃",
            X = Pos.Right(_content),
            Y = 1,
            Width = 1,
            Height = Dim.Fill(),
            CanFocus = false,
        };
        Add(_splitter);
    }

    /// <summary>记录页面切换（只写 verbose 日志）。</summary>
    private void LogPageSwitch(PageId id) => LauncherLog.Verbose($"切换到页面: {id}");

    /// <summary>折叠或展开任务栏，状态持久化到配置。</summary>
    private void ToggleCollapse()
    {
        _collapsed = !_collapsed;
        _taskPanel.Visible = !_collapsed;
        _splitter.Visible = !_collapsed;
        _content.Width = _collapsed ? Dim.Fill() : Dim.Fill(_taskPanel.Frame.Width + 1);
        _config.TaskPanelExpanded = !_collapsed;
        _config.Save();
        SetNeedsLayout();
        Layout();
        SetNeedsDraw();
        Application.LayoutAndDraw(true); // 立即重排+刷屏, 否则拖动时 groupbox 卡在旧尺寸
    }

    /// <summary>拖动分隔条改变任务栏宽度 (宽度实时持久化, 400ms 节流避免狂写盘)</summary>
    private void UpdatePanelWidth(int newWidth)
    {
        newWidth = Math.Clamp(newWidth, TaskPanel.MinWidth, TaskPanel.MaxWidth);
        _taskPanel.Width = newWidth;
        _content.Width = Dim.Fill(newWidth + 1); // +1 是分隔条
        _config.TaskPanelWidth = newWidth;

        SetNeedsLayout();
        SetNeedsDraw();

        // 节流: 拖动过程中最多每 400ms 落盘一次, 松手后由最后一次调用补上
        if ((DateTime.Now - _lastPanelWidthSave).TotalMilliseconds >= 400)
        {
            _lastPanelWidthSave = DateTime.Now;
            try { _config.Save(); } catch { /* 写盘失败不影响拖动 */ }
        }
    }

    /// <summary>
    /// 拖动中: 照常改宽度 (和以前一样, 只 SetNeedsLayout/SetNeedsDraw, 不强制重排),
    /// 松手时 ApplyDragWidth 再全量重排一次收尾。
    /// </summary>
    private void PreviewDragWidth(int mouseX)
    {
        var f = _taskPanel.Frame;
        var w = Math.Clamp(f.X + f.Width - mouseX, TaskPanel.MinWidth, TaskPanel.MaxWidth);
        if (w == _dragWidth)
            return;

        _dragWidth = w;
        UpdatePanelWidth(w);
    }

    /// <summary>松手: 全量重排重绘一次收尾 (宽度在拖动过程中已经逐帧应用了)</summary>
    private void ApplyDragWidth()
    {
        if (!_collapsed && _dragWidth > 0)
        {
            _taskPanel.Width = _dragWidth;
            _content.Width = Dim.Fill(_dragWidth + 1);
            _config.TaskPanelWidth = _dragWidth;
        }

        SetNeedsLayout();
        Layout();
        SetNeedsDraw();
        Application.LayoutAndDraw(true);

        try { _config.Save(); } catch { /* 写盘失败不影响拖动 */ }
    }

    /// <summary>主题切换后遍历视图树清 Scheme 缓存并重绘</summary>
    private void RefreshSchemes()
    {
        void Walk(View v)
        {
            if (v.HasScheme)
                v.SetScheme(null); // 清缓存, 回到按 SchemeName 现查的路径

            // 装饰视图 (边框/边距/内衬) 也要清
            foreach (var adornment in new[] { v.Border.View, v.Margin.View, v.Padding.View })
            {
                if (adornment is null)
                    continue;
                if (adornment.HasScheme)
                    adornment.SetScheme(null);
                foreach (var sub in adornment.SubViews)
                    Walk(sub);
            }

            foreach (var sub in v.SubViews)
                Walk(sub);
        }

        Walk(this);
        SetNeedsLayout();
        SetNeedsDraw();
    }

    /// <summary>切页: 隐藏旧页, 显示新页, 焦点带入新页</summary>
    public void ShowPage(PageId id)
    {
        if (!_pages.TryGetValue(id, out var page))
            return;
        // 首次必须走完整流程 (否则 OnPageEnter 永不触发, 页面数据不初始化)
        if (_pageShown && id == _current && page.Visible)
            return; // 已是当前页, 不抢焦点

        if (_pages.TryGetValue(_current, out var old))
        {
            old.OnPageLeave();
            old.Visible = false;
        }

        _current = id;
        _pageShown = true;
        page.Visible = true;
        page.OnPageEnter();
        UpdateTabBar();

        // 焦点带入新页面 (Visible 设置可能已自动聚焦, 双保险)
        page.SetFocus();
        _content.SetNeedsDraw();
    }

    private void UpdateTabBar()
    {
        foreach (var (id, lbl) in _tabItems)
        {
            var name = TabName(id);
            lbl.Text = id == _current ? $"[{name}]" : $" {name} ";
        }
    }

    /// <summary>
    /// 页面标题。 加新页面必须在这里加一行，否则标签栏会显示成 "?"
    /// （<see cref="TabName"/> 是唯一来源，tab 栏和窗口标题都从它取）
    /// </summary>
    private static string TabName(PageId id) => id switch
    {
        PageId.Launch => Loc.T("tab.launch"),
        PageId.Download => Loc.T("tab.download"),
        PageId.Terracotta => "联机",
        PageId.JarLaunch => "JAR",
        PageId.Settings => Loc.T("tab.settings"),
        PageId.More => Loc.T("tab.more"),
        _ => "?",
    };

    /// <summary>标签栏行在屏幕上的 Y 坐标</summary>
    private int TabBarScreenY()
    {
        var first = _tabItems[0].Label;
        return first.ViewportToScreen(new System.Drawing.Point(0, 0)).Y;
    }

    private void OnWindowMouseEvent(object? sender, Mouse e)
    {
        if (_dragging)
        {
            // 拖动中: 按住移动 (driver 会上报 PositionReport 或带按键的移动)
            if (e.Flags.HasFlag(MouseFlags.LeftButtonReleased))
            {
                _dragging = false;
                ApplyDragWidth(); // 松手才真正重排 + 存盘
                return;
            }
            if (!_collapsed)
                PreviewDragWidth(e.ScreenPosition.X);
            return;
        }

        if (e.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked) && !_collapsed)
        {
            // 双击分隔条 = 折叠
            var sp = _splitter.ViewportToScreen(new System.Drawing.Rectangle(
                System.Drawing.Point.Empty, _splitter.Viewport.Size));
            if (sp.Contains(e.ScreenPosition))
            {
                ToggleCollapse();
                return;
            }
        }

        if (e.Flags.HasFlag(MouseFlags.LeftButtonPressed) && !_collapsed)
        {
            // 按下分隔条 = 开始拖动
            var sp = _splitter.ViewportToScreen(new System.Drawing.Rectangle(
                System.Drawing.Point.Empty, _splitter.Viewport.Size));
            if (sp.Contains(e.ScreenPosition))
            {
                _dragging = true;
                _dragWidth = _config.TaskPanelWidth; // 只点不拖时保持原宽度
                return;
            }
        }

        if (!e.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            return;

        var y = TabBarScreenY();
        if (e.ScreenPosition.Y != y)
            return;

        foreach (var (id, lbl) in _tabItems)
        {
            var rect = lbl.ViewportToScreen(new System.Drawing.Rectangle(
                System.Drawing.Point.Empty, lbl.Viewport.Size));
            if (rect.Contains(e.ScreenPosition))
            {
                ShowPage(id);
                return;
            }
        }
    }

    protected override bool OnKeyDown(Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.F1: ShowPage(PageId.Launch); return true;
            case KeyCode.F2: ShowPage(PageId.Download); return true;
            case KeyCode.F3: ShowPage(PageId.Terracotta); return true;
            case KeyCode.F4: ShowPage(PageId.JarLaunch); return true;
            case KeyCode.F5: ShowPage(PageId.Settings); return true;
            case KeyCode.F6: ShowPage(PageId.More); return true;
            case KeyCode.F9: ToggleCollapse(); return true;
        }
        return base.OnKeyDown(key);
    }
}
