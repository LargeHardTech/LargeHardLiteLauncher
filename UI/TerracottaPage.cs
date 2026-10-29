using LargeHardLiteLauncher.Core;
using Terminal.Gui;

namespace LargeHardLiteLauncher.UI;

/// <summary>
/// 「联机」页：陶瓦联机（Terracotta）的图形化入口。
///
/// 干的事：
/// 1. 首次进来把内嵌的 terracotta.exe 解压到数据目录
/// 2. 起进程 → 拿端口 → 每 600ms 轮询 /state
/// 3. 创建房间（扫本机 MC 世界）→ 显示房间码给朋友
/// 4. 加入房间（填房间码）→ 显示连上的地址
///
/// 切到别的页面**只停轮询、不停进程**, 用户要开着联机去启动游戏。
/// </summary>
public class TerracottaPage : PageBase
{
    private readonly TerracottaService _svc = new();
    private readonly Action _onActivate; // 双击任务栏那条 → 跳回本页
    private readonly Label _stateLbl;
    private readonly Label _playerLbl;
    private readonly Label _nodeLbl;
    private readonly Label _roomLbl;
    private readonly Label _urlLbl;
    private readonly ListView<string> _playerList;
    private readonly TextView _logView;
    private readonly Button _hostBtn;
    private readonly Button _guestBtn;
    private readonly Button _leaveBtn;

    private readonly List<string> _log = new();
    private readonly object _logLock = new();
    private TerracottaSnapshot? _snap;
    private List<string> _nodes = new();
    private bool _active;
    private bool _polling;
    private bool _starting;
    private bool _logDirty;
    private bool _timersStarted;
    private int _pollFails;
    private Task<bool>? _startTask;
    private int _lastIndex = -1;
    private DateTime _lastFlush = DateTime.MinValue;

    public TerracottaPage(LauncherConfig config, Action onActivate) : base(config, " 联机 ")
    {
        _onActivate = onActivate;

        // terracotta 自己的输出 → 页面日志（出问题时这是唯一的线索）
        _svc.LogLine += line => AppendLog("[tc] " + line);

        // ===== 左：状态 + 操作 =====
        var stateBox = new FrameView
        {
            Title = " 状态 ",
            X = 0,
            Y = 0,
            Width = Dim.Percent(52),
            Height = 12,
            BorderStyle = LineStyle.Single,
        };

        _stateLbl = new Label { Text = "待机(未启动)", X = 1, Y = 0, Width = Dim.Fill(1) };
        _playerLbl = new Label { Text = "玩家  " + Config.PlayerName, X = 1, Y = 1, Width = Dim.Fill(1) };
        _nodeLbl = new Label { Text = "节点  未加载", X = 1, Y = 2, Width = Dim.Fill(1) };
        stateBox.Add(
            new Label { Text = "状态", X = 1, Y = 0, Width = 5 },
            _stateLbl,
            new Label { Text = "玩家", X = 1, Y = 1, Width = 5 },
            _playerLbl,
            new Label { Text = "节点", X = 1, Y = 2, Width = 5 },
            _nodeLbl,
            new Label
            {
                Text = $"内核  Terracotta v{TerracottaService.Version}(内嵌,AGPL-3.0)",
                X = 1,
                Y = 4,
                Width = Dim.Fill(1),
            });

        _hostBtn = new Button { Text = "创建房间", X = 0, Y = Pos.AnchorEnd(5), NoPadding = true, Width = 14 };
        _guestBtn = new Button { Text = "加入房间", X = Pos.Right(_hostBtn) + 2, Y = Pos.AnchorEnd(5), NoPadding = true, Width = 14 };
        _leaveBtn = new Button { Text = "离开", X = Pos.Right(_guestBtn) + 2, Y = Pos.AnchorEnd(5), NoPadding = true, Width = 10 };

        var logBtn = new Button { Text = "导出日志", X = 0, Y = Pos.AnchorEnd(3), NoPadding = true, Width = 14 };
        var licBtn = new Button { Text = "许可证", X = Pos.Right(logBtn) + 2, Y = Pos.AnchorEnd(3), NoPadding = true, Width = 12 };

        _hostBtn.Accepting += (_, _) => StartHost();
        _guestBtn.Accepting += (_, _) => StartGuest();
        _leaveBtn.Accepting += (_, _) => _ = LeaveAsync();
        logBtn.Accepting += (_, _) => _ = ExportLogAsync();
        licBtn.Accepting += (_, _) => ShowLicense();

        stateBox.Add(_hostBtn, _guestBtn, _leaveBtn, logBtn, licBtn);

        // ===== 右：房间信息 =====
        var roomBox = new FrameView
        {
            Title = " 房间 ",
            X = Pos.Right(stateBox),
            Y = 0,
            Width = Dim.Fill(),
            Height = 12,
            BorderStyle = LineStyle.Single,
        };

        _roomLbl = new Label { Text = "(还没创建)", X = 1, Y = 1, Width = Dim.Fill(1) };
        _urlLbl = new Label { Text = "", X = 1, Y = 3, Width = Dim.Fill(1) };

        var copyBtn = new Button { Text = "复制房间码", X = 1, Y = 5, NoPadding = true, Width = 16 };
        copyBtn.Accepting += (_, _) => CopyRoomCode();

        _playerList = new ListView<string>
        {
            X = 1,
            Y = 7,
            Width = Dim.Fill(2),
            Height = Dim.Fill(1),
        };
        _playerList.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>());

        roomBox.Add(
            new Label { Text = "房间码", X = 1, Y = 0, Width = 8 },
            _roomLbl,
            new Label { Text = "连接", X = 1, Y = 2, Width = 8 },
            _urlLbl,
            copyBtn,
            new Label { Text = "玩家", X = 1, Y = 6, Width = 8 },
            _playerList);

        // ===== 下：日志 =====
        var logBox = new FrameView
        {
            Title = " 日志 ",
            X = 0,
            Y = 12,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.Single,
        };
        _logView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = false,
        };
        logBox.Add(_logView);

        Add(stateBox, roomBox, logBox);

        AppendLog("联机内核随启动器分发(AGPL-3.0)。点击[创建房间]开始。");
        RefreshButtons();
    }

    /// <summary>
    /// 定时器等到真正进页面才注册。
    /// 不能在构造函数里注册：selftest 会构造 MainWindow 并驱动主循环，
    /// 定时器一触发就去 FlushLog → Application.Invoke → 卡死。
    /// </summary>
    private void StartTimers()
    {
        if (_timersStarted)
            return;
        _timersStarted = true;

        // 轮询：一直挂着，靠 _active 开关
        Application.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            if (_active && !_polling && _svc.IsReady)
            {
                _polling = true;
                _ = PollAsync();
            }
            return true;
        });

        // 日志补刷：AppendLog 的 150ms 节流会丢最后一次刷新，这里兜底
        Application.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            if (_logDirty)
                FlushLog();
            return true;
        });
    }

    public override void OnPageEnter()
    {
        _active = true;
        StartTimers();
        RefreshButtons();
    }

    public override void OnPageLeave()
    {
        // 只停轮询，进程留着, 用户可能正要去启动游戏
        _active = false;
    }

    // ---------- 启动服务 ----------

    /// <summary>
    /// 确保 terracotta 进程起来了。**走任务栏**（用户要求），
    /// 双击任务栏里那条就能跳回本页（<see cref="_onActivate"/>）。
    /// </summary>
    private Task<bool> EnsureStartedAsync()
    {
        if (_svc.IsReady)
            return Task.FromResult(true);
        if (_startTask is { IsCompleted: false })
            return _startTask; // 已经在起了，复用同一个

        _starting = true;
        RefreshButtons();
        AppendLog("正在启动联机服务(首次约 1 秒)...");

        var tcs = new TaskCompletionSource<bool>();
        var item = TaskManager.Submit("启动陶瓦联机", async (task, ct) =>
        {
            try
            {
                await _svc.StartAsync(task, ct).ConfigureAwait(false);
                var nodes = await TerracottaService.FetchNodesAsync(ct).ConfigureAwait(false);
                task.LogLine($"中继节点 {nodes.Count} 个");
                Application.Invoke(() =>
                {
                    _nodes = nodes;
                    _nodeLbl.Text = nodes.Count > 0
                        ? $"{nodes.Count} 个公共中继 · {NodeHosts(nodes)}"
                        : "用内置默认节点";
                    _nodeLbl.SetNeedsDraw();
                    AppendLog($"服务就绪,端口 {_svc.Port},中继节点 {nodes.Count} 个");
                    RefreshButtons();
                });
                tcs.TrySetResult(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                tcs.TrySetResult(false);
                throw;
            }
            catch (Exception ex)
            {
                LauncherLog.Error($"启动陶瓦联机失败: {ex}");
                foreach (var line in Net.Describe(ex).Split('\n'))
                    task.LogLine(line.TrimEnd());
                Application.Invoke(() =>
                {
                    AppendLog("[启动失败] " + ex.Message);
                    RefreshButtons();
                    MessageBox.ErrorQuery(Application.Instance, "陶瓦联机",
                        "启动失败:\n\n" + ex.Message, "好");
                });
                tcs.TrySetResult(false);
                throw;
            }
            finally
            {
                _starting = false;
                Application.Invoke(RefreshButtons);
            }
        });

        // 双击任务栏那一条 → 跳回本页
        item.OnActivate = _onActivate;
        _startTask = tcs.Task;
        return _startTask;
    }

    /// <summary>从节点 URL 里抽域名，够短，能塞进一行标签</summary>
    private static string NodeHosts(List<string> nodes)
    {
        var hosts = nodes
            .Select(n => Uri.TryCreate(n, UriKind.Absolute, out var u) ? u.Host : n)
            .Distinct()
            .ToList();
        return string.Join(", ", hosts);
    }

    // ---------- 开房 / 加房 ----------

    private async void StartHost()
    {
        if (!await EnsureStartedAsync())
            return;

        AppendLog($"创建房间(玩家名 {Config.PlayerName})...");
        try
        {
            await _svc.HostAsync(Config.PlayerName, _nodes, CancellationToken.None);
            AppendLog("已请求开房,等它扫到本机的 MC 世界...");
            AppendLog("提示:请先在游戏中对局域网开放世界,此处才能扫描到");
        }
        catch (Exception ex)
        {
            AppendLog("[失败] " + ex.Message);
            MessageBox.ErrorQuery(Application.Instance, "创建房间", ex.Message, "好");
        }
    }

    private async void StartGuest()
    {
        if (!await EnsureStartedAsync())
            return;

        var code = PromptRoomCode();
        if (string.IsNullOrWhiteSpace(code))
            return;

        code = code.Trim();
        if (!code.StartsWith("U/", StringComparison.OrdinalIgnoreCase))
            code = "U/" + code;

        AppendLog($"加入房间 {code}...");
        try
        {
            await _svc.GuestAsync(code, Config.PlayerName, _nodes, CancellationToken.None);
            AppendLog("已请求加入,正在连...");
        }
        catch (Exception ex)
        {
            AppendLog("[失败] " + ex.Message);
            MessageBox.ErrorQuery(Application.Instance, "加入房间",
                "加入失败:\n\n" + ex.Message + "\n\n房间码要形如 U/XXXX-XXXX-XXXX-XXXX", "好");
        }
    }

    private async Task LeaveAsync()
    {
        if (!_svc.IsReady)
            return;
        try
        {
            await _svc.SetWaitingAsync(CancellationToken.None);
            AppendLog("已离开房间");
        }
        catch (Exception ex)
        {
            AppendLog("[失败] " + ex.Message);
        }
    }

    /// <summary>输入房间码（带格式提示）</summary>
    private static string? PromptRoomCode()
    {
        var dlg = new Dialog
        {
            Title = "加入房间",
            Width = 56,
            Height = 9,
            BorderStyle = LineStyle.Single,
        };
        dlg.Add(new Label { Text = "由房主提供的房间码", X = 1, Y = 1 });
        var field = new TextField { Text = "", X = 1, Y = 2, Width = Dim.Fill(2) };
        dlg.Add(new Label { Text = "格式 U/XXXX-XXXX-XXXX-XXXX", X = 1, Y = 4 });
        var ok = new Button { Text = "加入", X = 1, Y = 5, IsDefault = true };
        var cancel = new Button { Text = "取消", X = Pos.Right(ok) + 2, Y = 5 };

        string? result = null;
        ok.Accepting += (_, _) =>
        {
            var t = (field.Text ?? "").Trim();
            if (t.Length < 8)
                return; // 太短不关框
            result = t;
            dlg.RequestStop();
        };
        cancel.Accepting += (_, _) => dlg.RequestStop();

        dlg.Add(field, ok, cancel);
        field.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();
        return result;
    }

    // ---------- 轮询 ----------

    private async Task PollAsync()
    {
        try
        {
            var snap = await _svc.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
            _pollFails = 0;
            if (snap is not null)
                Application.Invoke(() => ApplyState(snap));
        }
        catch (Exception ex)
        {
            // 连不上 = 服务真没了（父进程退出是正常的，HTTP 断开才是真断）
            _pollFails++;
            if (_pollFails == 3)
            {
                Application.Invoke(() =>
                {
                    AppendLog("[断开] 无法连接联机服务:" + ex.Message);
                    AppendLog("       点[创建房间]会重新启动内核");
                    _snap = null;
                    _lastIndex = -1;
                    _stateLbl.Text = "服务已断开";
                    _stateLbl.SetNeedsDraw();
                    RefreshButtons();
                });
            }
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>把状态刷到界面（index 没变就跳过，省重绘）</summary>
    private void ApplyState(TerracottaSnapshot snap)
    {
        if (snap.Index == _lastIndex)
            return;
        var prev = _snap?.State;
        _lastIndex = snap.Index;
        _snap = snap;

        _stateLbl.Text = snap.StateText;
        _stateLbl.SetNeedsDraw();

        _roomLbl.Text = string.IsNullOrEmpty(snap.Room) ? "(还没创建)" : snap.Room;
        _roomLbl.SetNeedsDraw();

        _urlLbl.Text = string.IsNullOrEmpty(snap.Url) ? "" : snap.Url;
        _urlLbl.SetNeedsDraw();

        var rows = snap.Profiles.Count > 0
            ? snap.Profiles.Select(p => "  " + p).ToList()
            : new List<string> { "  (房间里还没有人)" };
        _playerList.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(rows));

        // 状态变化写日志（同状态只在 index 变时记一次）
        if (prev != snap.State)
        {
            AppendLog($"→ {snap.StateText}");
            if (snap.IsHostOk && !string.IsNullOrEmpty(snap.Room))
                AppendLog($"房间码:{snap.Room}(发给朋友)");
            if (snap.IsGuestOk && !string.IsNullOrEmpty(snap.Url))
                AppendLog($"已连上,服务器地址:{snap.Url}");
        }

        RefreshButtons();
    }

    private void RefreshButtons()
    {
        var snap = _snap;
        var busy = _starting || snap?.IsBusy == true;
        _hostBtn.Enabled = !busy && snap?.IsHostOk != true;
        _guestBtn.Enabled = !busy && snap?.IsGuestOk != true;
        _leaveBtn.Enabled = snap?.CanLeave == true;
        SetNeedsDraw();
    }

    // ---------- 杂项 ----------

    /// <summary>把房间码塞进剪贴板（TG 自带 Clipboard）</summary>
    private void CopyRoomCode()
    {
        var code = _snap?.Room;
        if (string.IsNullOrEmpty(code))
        {
            MessageBox.Query(Application.Instance, "房间码", "尚无房间码,请先点击[创建房间]。", "好");
            return;
        }
        try
        {
            if (Clipboard.TrySetClipboardData(code))
                AppendLog($"已复制房间码:{code}");
            else
                MessageBox.Query(Application.Instance, "房间码", "复制失败,请手动记录:\n\n" + code, "好");
        }
        catch (Exception ex)
        {
            MessageBox.Query(Application.Instance, "房间码", $"复制失败({ex.Message}):\n\n{code}", "好");
        }
    }

    /// <summary>把 Terracotta 自己的日志拉到本地文件</summary>
    private async Task ExportLogAsync()
    {
        if (!_svc.IsReady)
        {
            MessageBox.Query(Application.Instance, "导出日志", "联机服务还没启动。", "好");
            return;
        }
        try
        {
            var text = await _svc.ExportLogAsync(CancellationToken.None).ConfigureAwait(false);
            var dir = Path.Combine(LauncherConfig.RootDir, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"terracotta-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            await File.WriteAllTextAsync(path, text).ConfigureAwait(false);
            Application.Invoke(() =>
            {
                AppendLog($"日志已保存:{path}");
                MessageBox.Query(Application.Instance, "导出日志", $"已保存到:\n{path}", "好");
            });
        }
        catch (Exception ex)
        {
            Application.Invoke(() => MessageBox.ErrorQuery(Application.Instance,
                "导出日志", ex.Message, "好"));
        }
    }

    /// <summary>展示内嵌的 AGPL-3.0 全文（可滚动）</summary>
    private static void ShowLicense()
    {
        var dlg = new Dialog
        {
            Title = "Terracotta 许可证 (AGPL-3.0)",
            Width = Dim.Percent(90),
            Height = Dim.Percent(85),
            BorderStyle = LineStyle.Single,
        };
        var view = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(3),
            ReadOnly = true,
            Text = TerracottaService.ReadLicense(),
        };
        var note = new Label
        {
            Text = "本启动器随附分发 Terracotta 二进制(未修改)。源码:https://github.com/burningtnt/Terracotta",
            X = 1,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(2),
        };
        var ok = new Button { Text = "关闭", X = Pos.Center(), Y = Pos.AnchorEnd(2) };
        ok.Accepting += (_, _) => dlg.RequestStop();
        dlg.Add(view, note, ok);
        view.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();
    }

    // ---------- 日志区 ----------

    private void AppendLog(string line)
    {
        lock (_logLock)
        {
            _log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            if (_log.Count > 500)
                _log.RemoveRange(0, 200);
        }

        // 以前这里直接 return 就完了, 150ms 内的日志永远不会显示（没有补刷）。
        // 现在只标记脏，由 300ms 的定时器兜底刷一次。
        _logDirty = true;
        if ((DateTime.UtcNow - _lastFlush).TotalMilliseconds >= 150)
            FlushLog();
    }

    private void FlushLog()
    {
        _logDirty = false;
        _lastFlush = DateTime.UtcNow;
        string text;
        lock (_logLock)
            text = string.Join('\n', _log);
        Application.Invoke(() =>
        {
            _logView.Text = text;
            _logView.MoveEnd();
            _logView.SetNeedsDraw();
        });
    }
}
