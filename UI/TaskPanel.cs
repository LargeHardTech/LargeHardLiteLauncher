using System.Collections.ObjectModel;
using LargeHardLiteLauncher.Core;
using Terminal.Gui;
using Terminal.Gui.Input;

namespace LargeHardLiteLauncher.UI;

/// <summary>
/// 右侧任务栏：任务列表 + 进度条 + 底部按钮 (TG Button, 不设 Height 以免边框被压扁只剩阴影)。
/// 布局: 列表 → 当前任务进度条 → 总进度条 → 底部 [结束X 弹性] [折叠 贴右]。
/// 终态(完成/失败/取消)任务显示 3 秒后自动移除; 列表为空显示 IDLE。
/// </summary>
public class TaskPanel : FrameView
{
    /// <summary>面板默认宽度 (列)</summary>
    public const int DefaultWidth = 34;
    /// <summary>面板最小宽度 (列)</summary>
    public const int MinWidth = 20;
    /// <summary>面板最大宽度 (列)</summary>
    public const int MaxWidth = 80;
    /// <summary>终态任务在列表里保留多久。</summary>
    private static readonly TimeSpan FinishedVisible = TimeSpan.FromSeconds(20);
    /// <summary>折叠按钮固定宽度，需精确匹配 "[折叠(F9)]" 的 10 列。</summary>
    private const int FoldBtnWidth = 10;

    private ObservableCollection<string> _items = new();
    private readonly ListView<string> _list;
    private readonly Button _cancelBtn;
    private readonly Button _foldBtn;
    private readonly ProgressBar _taskProgress;
    private readonly ProgressBar _totalProgress;
    private readonly Label _taskPctLbl;
    private readonly Label _totalPctLbl;
    private readonly Action _onToggleCollapse;

    /// <summary>已安排移除的终态任务 (避免重复排)</summary>
    private readonly HashSet<TaskItem> _scheduled = new();

    public TaskPanel(Action onToggleCollapse)
    {
        _onToggleCollapse = onToggleCollapse;

        Title = Loc.T("task.title");
        // X 由 MainWindow 布局时设置 (Pos.Right(_content))
        Y = 1;
        Width = DefaultWidth;
        Height = Dim.Fill();
        BorderStyle = LineStyle.Single;

        _items.Add($"[1] {Loc.T("task.idle")}");
        _list = new ListView<string>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(3), // 底部: 2 行进度条 + 1 行按钮
        };
        _list.SetSource(_items);
        _list.ValueChanged += (_, _) => RefreshSelected();
        _list.Accepted += (_, _) =>
        {
            // 第 0 行是启动器日志: 用当前显示集合的首项判断
            // (不能用 LogRowText 直接比, 它含行数, 每次求值都可能变)
            if (_items.Count > 0 && _list.Value == _items[0])
            {
                ShowLauncherLog();
                return;
            }
            // 任务自带跳转目标（比如 JAR 启动任务 → 启动 JAR 页）就跳过去，否则弹日志
            if (Selected?.OnActivate is { } activate)
            {
                activate();
                return;
            }
            ShowLog();
        };

        // 当前任务进度条 (底部往上第 3 行), 右侧留 4 列给百分比标签
        _taskProgress = new ProgressBar
        {
            X = 0,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(4),
            Height = 1,
        };
        _taskPctLbl = new Label
        {
            Text = "0%",
            X = Pos.AnchorEnd(4),
            Y = Pos.AnchorEnd(3),
            Width = 4,
            Height = 1,
        };

        // 总进度条 (底部往上第 2 行), 右侧同样留 4 列
        _totalProgress = new ProgressBar
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(4),
            Height = 1,
        };
        _totalPctLbl = new Label
        {
            Text = "0%",
            X = Pos.AnchorEnd(4),
            Y = Pos.AnchorEnd(2),
            Width = 4,
            Height = 1,
        };

        // 结束按钮: 弹性宽度填满左侧, 贴底。
        // NoPadding 去掉 " text " 硬编码空格; TextAlignment=Start 去掉居中填充空格
        _cancelBtn = new Button
        {
            Text = Loc.T("task.cancel"),
            NoPadding = true,
            TextAlignment = Alignment.Start,
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(FoldBtnWidth + 1),
        };
        _cancelBtn.Accepting += (_, _) => CancelSelected();

        // 折叠按钮: 贴最右, 贴底, 宽度精确匹配文本 (10 = "折叠(F9)" 8 列 + 括号 2 列)
        _foldBtn = new Button
        {
            Text = Loc.T("task.fold"),
            NoPadding = true,
            TextAlignment = Alignment.Start,
            X = Pos.AnchorEnd(FoldBtnWidth),
            Y = Pos.AnchorEnd(1),
            Width = FoldBtnWidth,
        };
        _foldBtn.Accepting += (_, _) => _onToggleCollapse();

        Add(_list, _taskProgress, _taskPctLbl, _totalProgress, _totalPctLbl, _cancelBtn, _foldBtn);

        // 启动时铺满所有槽位行 (否则只有构造时的 [1] IDLE 一行)
        RefreshList();

        TaskManager.ListChanged += OnTaskManagerChanged;
        // 任务运行中 Report/LogLine → 实时刷新进度条与百分比 (不重建列表)
        TaskManager.TaskUpdated += OnTaskUpdated;
        // 面板级鼠标路由兜底: TG 2.5 Button 鼠标点击可能被 ShadowView 吞
        // (GetViewsUnderLocation 的 TransparentMouse 过滤跳过 Button 本体),
        // 命中按钮矩形时直接 InvokeCommand 保证点击可靠
        MouseEvent += OnPanelMouseEvent;
    }

    /// <summary>面板级鼠标路由兜底</summary>
    private void OnPanelMouseEvent(object? sender, Mouse e)
    {
        if (!e.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            return;

        if (Hit(_cancelBtn, e.ScreenPosition))
        {
            CancelSelected();
            e.Handled = true;
            return;
        }
        if (Hit(_foldBtn, e.ScreenPosition))
        {
            _onToggleCollapse();
            e.Handled = true;
        }
    }

    private static bool Hit(View v, System.Drawing.Point screen)
    {
        var rect = v.ViewportToScreen(new System.Drawing.Rectangle(
            System.Drawing.Point.Empty, v.Viewport.Size));
        return rect.Contains(screen);
    }

    /// <summary>当前选中的任务 (null = 空槽/无)</summary>
    private TaskItem? Selected
    {
        get
        {
            var slots = TaskManager.Slots();
            var val = _list.Value; // 泛型 ListView 的 SelectedItem 就是选中项本身
            if (string.IsNullOrEmpty(val))
                return null;
            // 启动器日志行不是任务, 选中它时返回 null
            if (_items.Count > 0 && val == _items[0])
                return null;
            // 显示格式 "[n] ...", 按槽位号反查
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] is { } t && t.Display == val)
                    return t;
            }
            return null;
        }
    }

    private void OnTaskManagerChanged()
    {
        // 任务线程触发, 切 UI 线程
        Application.Invoke(RefreshAll);
    }

    /// <summary>任务运行中更新 (Report/LogLine): 刷新进度 + 就地更新该行文本, 不重建列表</summary>
    private void OnTaskUpdated(TaskItem item)
    {
        Application.Invoke(() =>
        {
            RefreshProgressBars();
            // 列表第 0 行是日志项, 槽位 n 对应索引 n (不是 n-1)
            int idx = item.Slot;
            if (idx >= 0 && idx < _items.Count)
                _items[idx] = item.Display;
        });
    }

    /// <summary>刷新两根进度条 + 右侧百分比标签</summary>
    private void RefreshProgressBars()
    {
        var t = Selected;
        var frac = t is { Progress: > 0 } ? (float)t.Progress : 0f;
        _taskProgress.Fraction = frac;
        // 选中空槽位显示 IDLE; 任务不确定进度显示 ...; 否则百分比
        _taskPctLbl.Text = t is null
            ? (IsLogRowSelected ? Loc.T("task.logTag") : Loc.T("task.idleShort"))
            : (t.Progress < 0 ? "..." : t.Progress >= 0.999 ? "100%" : $"{frac * 100:0}%");
        RefreshTotal();
    }

    private void RefreshAll()
    {
        // 终态任务: 安排 3 秒后自动移除 (槽位回归 IDLE)
        foreach (var t in TaskManager.Slots())
        {
            if (t is { IsDone: true } && _scheduled.Add(t))
            {
                Application.AddTimeout(FinishedVisible, () =>
                {
                    TaskManager.Remove(t);
                    return false; // 不重复
                });
            }
        }

        RefreshList();
        RefreshSelected();
    }

    private void RefreshList()
    {
        var slots = TaskManager.Slots();
        // 第 0 项: 启动器自身日志 (常驻, 不占任务槽位)
        // 固定槽位显示: 1~Capacity, 空槽 IDLE
        // 一次性替换 Source: 增量 Add 会触发 O(n^2) 的 MaxItemLength 重算
        var items = new List<string>(slots.Length + 1);
        items.Add(LogRowText);
        for (int i = 0; i < slots.Length; i++)
            items.Add(slots[i] is { } t ? t.Display : $"[{i + 1}] {Loc.T("task.idle")}");
        _items = new ObservableCollection<string>(items);
        _list.SetSource(_items);
        // 新提交的任务: 自动选中它 (焦点跟到最新任务), 否则保持默认第 0 行
        var newest = TaskManager.Slots()
            .Where(t => t is { IsDone: false })
            .OrderByDescending(t => t!.Slot)
            .FirstOrDefault();
        string? target = items.Count > 0 ? items[0] : null;
        if (newest is not null && !ReferenceEquals(newest, _lastAutoSelected))
        {
            _lastAutoSelected = newest;
            int idx = newest.Slot; // 槽位 n 对应列表索引 n (索引 0 是日志行)
            if (idx >= 0 && idx < items.Count)
                target = items[idx];
        }

        // 替换 Source 后旧选中索引可能越界 (任务已被自动移除), 必须 clamp,
        // 否则双击时 ListView.OnActivated -> SetSelection(旧索引) 抛 ArgumentOutOfRange
        _list.SelectedItem = target;
        RefreshTotal();
    }

    private void RefreshSelected()
    {
        var t = Selected;
        if (t is null)
        {
            _cancelBtn.Text = Loc.T("task.cancel");
            _taskProgress.Fraction = 0;
            // 日志行显示 LOG, 空槽位显示 IDLE
            _taskPctLbl.Text = IsLogRowSelected ? Loc.T("task.logTag") : Loc.T("task.idleShort");
            return;
        }
        // 截断宽度随按钮(任务栏)宽度变化: 按钮文本 "结束NAME" 占满 [ ... ] 内部,
        // 可用列宽 = 按钮宽 - 括号 2 列 - "结束" 4 列
        var btnWidth = _cancelBtn.Frame.Width;
        if (btnWidth <= 0)
            btnWidth = Frame.Width; // 尚未布局时用面板宽兜底
        var nameBudget = Math.Max(4, btnWidth - 2 - 4);
        var name = TruncateByColumns(t.Name, nameBudget);
        _cancelBtn.Text = Loc.T("task.cancel"); // 统一"结束任务", 不带任务名
        RefreshProgressBars(); // 进度条逻辑只保留一处, 避免和这里写死的百分比打架
    }

    /// <summary>按终端列宽截断字符串 (汉字算 2 列), 超出部分用 … 代替</summary>
    private static string TruncateByColumns(string s, int maxColumns)
    {
        if (s.GetColumns() <= maxColumns)
            return s;
        var sb = new System.Text.StringBuilder();
        int cols = 0;
        foreach (var ch in s)
        {
            int w = ch.ToString().GetColumns();
            if (cols + w > maxColumns - 1) // 预留 1 列给 …
                break;
            sb.Append(ch);
            cols += w;
        }
        sb.Append('…');
        return sb.ToString();
    }

    /// <summary>总进度: 所有运行中任务进度的平均 (无运行任务则 0)</summary>
    private void RefreshTotal()
    {
        var slots = TaskManager.Slots();
        int running = 0;
        double sum = 0;
        foreach (var t in slots)
        {
            if (t is null || t.IsDone)
                continue;
            running++;
            sum += t.Progress < 0 ? 0 : t.Progress;
        }
        if (running == 0)
        {
            // 全部完成 / 没有任务: 满格 + DONE (不再显示容易误解的 0%)
            _totalProgress.Fraction = 1f;
            _totalPctLbl.Text = Loc.T("task.done");
            return;
        }
        var total = (float)(sum / running);
        _totalProgress.Fraction = total;
        _totalPctLbl.Text = $"{total * 100:0}%";
    }

    private void CancelSelected()
    {
        var t = Selected;
        if (t is null || t.IsDone)
            return;
        TaskManager.Cancel(t);
        RefreshAll();
    }

    private TaskItem? _lastAutoSelected; // 上次自动选中的任务, 避免每次刷新都抢焦点

    /// <summary>当前选中的是不是第 0 行（启动器日志）。</summary>
    private bool IsLogRowSelected => _items.Count > 0 && _list.Value == _items[0];

    /// <summary>第 0 项: 启动器日志行文本 (固定, 不含易变信息, 便于比较)</summary>
    private static string LogRowText => Loc.T("task.launcherLogRow");

    /// <summary>启动器日志窗 (含保存日志到指定位置)</summary>
    private void ShowLauncherLog()
    {
        var dialog = new Dialog
        {
            Title = Loc.T("log.title"),
            Width = Dim.Percent(80),
            Height = Dim.Percent(85),
            BorderStyle = LineStyle.Single,
        };
        var logView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ReadOnly = true,
            Text = LauncherLog.Text,
        };
        var saveBtn = new Button { Text = Loc.T("log.save"), X = 1, Y = Pos.AnchorEnd(1) };
        saveBtn.Accepting += (_, _) => ShowSaveLogDialog();

        var clearBtn = new Button { Text = Loc.T("log.clear"), X = Pos.Right(saveBtn) + 2, Y = Pos.AnchorEnd(1) };
        clearBtn.Accepting += (_, _) =>
        {
            LauncherLog.Clear();
            logView.Text = LauncherLog.Text;
            RefreshAll();
        };

        var closeBtn = new Button
        {
            Text = Loc.T("task.close"),
            X = Pos.AnchorEnd(12),
            Y = Pos.AnchorEnd(1),
            Width = 10,
        };
        closeBtn.Accepting += (_, _) => Application.RequestStop(dialog);

        dialog.Add(logView, saveBtn, clearBtn, closeBtn);

        // 新日志实时刷新: 200ms 轮询行数 (比订阅静态事件简单, 也不用操心线程)
        int lastCount = -1;
        object? timer = null;
        void Tick()
        {
            int n = LauncherLog.LineCount;
            if (n != lastCount)
            {
                lastCount = n;
                logView.Text = LauncherLog.Text;
                logView.MoveEnd();
                logView.SetNeedsDraw();
                dialog.SetNeedsDraw();
            }
            timer = Application.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
            {
                Tick();
                return false;
            });
        }
        Tick();

        Application.Run(dialog);
        if (timer is not null)
            Application.RemoveTimeout(timer);
        dialog.Dispose();
        RefreshAll();
    }

    /// <summary>保存日志对话框: 路径可编辑, 默认写到 ~/.largehardlitelauncher/logs</summary>
    private void ShowSaveLogDialog()
    {
        var dialog = new Dialog
        {
            Title = Loc.T("log.saveTitle"),
            Width = Dim.Percent(80),
            Height = 9,
            BorderStyle = LineStyle.Single,
        };
        var label = new Label { Text = Loc.T("log.savePath"), X = 1, Y = 1 };
        var pathField = new TextField
        {
            Text = LauncherLog.DefaultLogPath(),
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
        };
        var okBtn = new Button { Text = Loc.T("common.confirm"), X = 1, Y = 4 };
        okBtn.Accepting += (_, _) =>
        {
            var path = pathField.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                LauncherLog.Save(path);
                Application.RequestStop(dialog);
                MessageBox.Query(Application.Instance, Loc.T("log.save"), Loc.T("log.saved", path), Loc.T("common.confirm"));
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery(Application.Instance, Loc.T("common.error"), Loc.T("log.saveFailed", ex.Message), Loc.T("common.confirm"));
            }
        };
        var cancelBtn = new Button { Text = Loc.T("common.cancel"), X = Pos.Right(okBtn) + 2, Y = 4 };
        cancelBtn.Accepting += (_, _) => Application.RequestStop(dialog);

        dialog.Add(label, pathField, okBtn, cancelBtn);
        Application.Run(dialog);
        dialog.Dispose();
    }

    private void ShowLog()
    {
        var t = Selected;
        if (t is null)
            return;

        var dialog = new Dialog
        {
            Title = Loc.T("task.logTitle", t.Name),
            Width = Dim.Percent(70),
            Height = Dim.Percent(80),
            BorderStyle = LineStyle.Single,
        };
        var logView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ReadOnly = true,
            Text = string.IsNullOrEmpty(t.LogText) ? Loc.T("task.noLog") : t.LogText,
        };
        var closeBtn = new Button
        {
            Text = Loc.T("task.close"),
            X = Pos.AnchorEnd(12),
            Y = Pos.AnchorEnd(1),
            Width = 10,
        };
        closeBtn.Accepting += (_, _) => Application.RequestStop(dialog);
        dialog.Add(logView, closeBtn);

        // 任务还在跑时日志会持续增长, 同样轮询刷新
        string lastText = logView.Text?.ToString() ?? "";
        object? timer = null;
        void TickTask()
        {
            var txt = string.IsNullOrEmpty(t.LogText) ? Loc.T("task.noLog") : t.LogText;
            if (txt != lastText)
            {
                lastText = txt;
                logView.Text = txt;
                logView.MoveEnd();
                logView.SetNeedsDraw();
                dialog.SetNeedsDraw();
            }
            timer = Application.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
            {
                TickTask();
                return false;
            });
        }
        TickTask();

        Application.Run(dialog);
        if (timer is not null)
            Application.RemoveTimeout(timer);
        dialog.Dispose();

        RefreshAll();
    }
}
