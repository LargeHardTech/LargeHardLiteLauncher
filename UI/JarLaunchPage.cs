using LargeHardLiteLauncher.Core;
using Terminal.Gui;

namespace LargeHardLiteLauncher.UI;

/// <summary>
/// 「启动 JAR」页：独立跑一个 jar（服务端 / 客户端 / 任何 Java 程序），
/// 图形化调内存、Java、GC、启动参数，右边是实时输出 + 控制台命令输入。
///
/// 和 MC 版本无关, 不需要版本 json，纯 <c>java [jvm] -jar xxx.jar [args]</c>。
/// </summary>
public class JarLaunchPage : PageBase
{
    private readonly TextField _jarField;
    private readonly TextField _workDirField;
    private readonly DropDownList _javaDrop;
    private readonly TextField _maxMem;
    private readonly TextField _minMem;
    private readonly DropDownList _gcDrop;
    private readonly Label _gcHint;
    private readonly TextField _jvmArgs;
    private readonly TextField _extraArgs;
    private readonly CheckBox _serverMode;
    private readonly Button _startBtn;
    private readonly Button _stopBtn;
    private readonly TextView _logView;
    private readonly TextField _cmdField;

    private readonly List<string> _log = new();
    private readonly object _logLock = new();
    private readonly Action _onActivate; // 双击任务栏时切回本页
    private List<JavaInfo> _javas = new();
    /// <summary>正在跑的任务（任务栏里的那一项）；null = 空闲</summary>
    private TaskItem? _runningTask;
    private DateTime _lastFlush = DateTime.MinValue;

    public JarLaunchPage(LauncherConfig config, Action onActivate) : base(config, " 启动 JAR ")
    {
        _onActivate = onActivate;
        // ===== 左：配置 =====
        var left = new FrameView
        {
            Title = " 配置 ",
            X = 0,
            Y = 0,
            Width = Dim.Percent(46),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.Single,
        };

        static Label Cap(string text, int y) => new() { Text = text, X = 0, Y = y };

        _jarField = new TextField { X = 5, Y = 0, Width = Dim.Fill(10) };
        var jarBrowse = new Button
        {
            Text = "浏览...",
            X = Pos.Right(_jarField) + 1,
            Y = 0,
            NoPadding = true,
            Width = 9,
        };
        jarBrowse.Accepting += (_, _) => BrowseJar();

        _workDirField = new TextField { X = 5, Y = 2, Width = Dim.Fill(10) };
        var dirBrowse = new Button
        {
            Text = "浏览...",
            X = Pos.Right(_workDirField) + 1,
            Y = 2,
            NoPadding = true,
            Width = 9,
        };
        dirBrowse.Accepting += (_, _) => BrowseWorkingDir();

        // Java
        _javas = JavaDetector.Detect();
        _javaDrop = new DropDownList
        {
            X = 5,
            Y = 4,
            Width = Dim.Fill(1),
            Source = new ListWrapper<string>(new System.Collections.ObjectModel.ObservableCollection<string>(JavaItems())),
        };
        _javaDrop.Text = JavaItems()[0];

        // 内存
        _maxMem = new TextField { Text = "2048", X = 5, Y = 5, Width = 7 };
        _minMem = new TextField { Text = "512", X = 20, Y = 5, Width = 7 };

        // GC
        _gcDrop = new DropDownList
        {
            X = 5,
            Y = 6,
            Width = Dim.Fill(1),
            Source = new ListWrapper<string>(new System.Collections.ObjectModel.ObservableCollection<string>(
                JarLauncher.GcPresets.Select(p => p.Display))),
        };
        _gcDrop.Text = JarLauncher.GcPresets[0].Display;
        _gcHint = new Label { Text = "", X = 1, Y = 7, Width = Dim.Fill(1) };
        _gcDrop.ValueChanged += (_, _) => UpdateGcHint();

        _jvmArgs = new TextField { X = 5, Y = 9, Width = Dim.Fill(1) };
        _extraArgs = new TextField { X = 5, Y = 10, Width = Dim.Fill(1) };

        _serverMode = new CheckBox
        {
            Text = "服务端模式(nogui)",
            X = 0,
            Y = 11,
            Value = CheckState.Checked,
        };

        _startBtn = new Button
        {
            Text = "启动",
            X = 0,
            Y = Pos.AnchorEnd(4),
            NoPadding = true,
            Width = 10,
        };
        _stopBtn = new Button
        {
            Text = "停止",
            X = Pos.Right(_startBtn) + 2,
            Y = Pos.AnchorEnd(4),
            NoPadding = true,
            Width = 10,
            Enabled = false,
        };
        var clearBtn = new Button
        {
            Text = "清空日志",
            X = Pos.Right(_stopBtn) + 2,
            Y = Pos.AnchorEnd(4),
            NoPadding = true,
            Width = 12,
        };

        _startBtn.Accepting += (_, _) => _ = StartAsync();
        _stopBtn.Accepting += (_, _) => Stop();
        clearBtn.Accepting += (_, _) =>
        {
            lock (_logLock) _log.Clear();
            FlushLog();
        };

        left.Add(
            Cap("JAR", 0), _jarField, jarBrowse,
            Cap("目录", 2), _workDirField, dirBrowse,
            Cap("Java", 4), _javaDrop,
            Cap("内存", 5), _maxMem, new Label { Text = "~", X = 13, Y = 5 }, _minMem,
            new Label { Text = "MB", X = 28, Y = 5 },
            Cap("GC", 6), _gcDrop,
            _gcHint,
            Cap("JVM", 9), _jvmArgs,
            Cap("参数", 10), _extraArgs,
            _serverMode,
            _startBtn, _stopBtn, clearBtn);

        // 内存那行补两个说明标签（对齐用）
        left.Add(new Label { Text = "最大", X = 5, Y = 5, Visible = false });
        left.Add(new Label { Text = "最大/最小", X = 1, Y = 5, Width = 4, Visible = false });

        // ===== 右：输出 =====
        var right = new FrameView
        {
            Title = " 输出 ",
            X = Pos.Right(left),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.Single,
        };

        _logView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            ReadOnly = true,
            WordWrap = false,
        };

        // 控制台命令（服务器要 stop / list 之类）
        _cmdField = new TextField { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(9) };
        var sendBtn = new Button
        {
            Text = "发送",
            X = Pos.Right(_cmdField) + 1,
            Y = Pos.AnchorEnd(2),
            NoPadding = true,
            Width = 8,
        };
        void SendCommand()
        {
            var cmd = (_cmdField.Text ?? "").Trim();
            if (cmd.Length == 0)
                return;
            _cmdField.Text = "";
            if (JarLauncher.TrySendCommand(cmd))
                AppendLog($"> {cmd}");
            else
                AppendLog("[提示] 没有正在运行的进程");
        }
        sendBtn.Accepting += (_, _) => SendCommand();
        _cmdField.Accepted += (_, _) => SendCommand();

        right.Add(_logView, _cmdField, sendBtn);

        Add(left, right);
        UpdateGcHint();
    }

    public override void OnPageEnter()
    {
        // 用户可能在设置页刚装了 Java，进来时重扫一下
        RefreshJavaList();
    }

    /// <summary>从命令行带进来的 jar 路径（<c>LargeHardLiteLauncher xxx.jar</c>）</summary>
    public void Prefill(string jarPath)
    {
        if (string.IsNullOrWhiteSpace(jarPath))
            return;
        try
        {
            jarPath = Path.GetFullPath(jarPath);
        }
        catch
        {
            // 路径怪就原样用
        }
        _jarField.Text = jarPath;
        // 工作目录跟着 jar 走（服务器习惯）
        var dir = Path.GetDirectoryName(jarPath);
        if (!string.IsNullOrEmpty(dir))
            _workDirField.Text = dir;
        AppendLog($"已载入: {jarPath}");
    }

    // ---------- 控件辅助 ----------

    private List<string> JavaItems()
    {
        var items = new List<string> { "自动(最合适)" };
        items.AddRange(_javas.Select(j => j.Display));
        return items;
    }

    private void RefreshJavaList()
    {
        var keep = _javaDrop.Text;
        _javas = JavaDetector.Detect();
        _javaDrop.Source = new ListWrapper<string>(
            new System.Collections.ObjectModel.ObservableCollection<string>(JavaItems()));
        var items = JavaItems();
        _javaDrop.Text = !string.IsNullOrEmpty(keep) && items.Contains(keep) ? keep : items[0];
    }

    private void UpdateGcHint()
    {
        var preset = JarLauncher.GcPresets.FirstOrDefault(p => p.Display == _gcDrop.Text)
                     ?? JarLauncher.GcPresets[0];
        _gcHint.Text = preset.Description;
        _gcHint.SetNeedsDraw();
    }

    private void BrowseJar()
    {
        var fd = new FileDialog
        {
            Title = "选择 jar 文件",
            OpenMode = OpenMode.File,
            Path = string.IsNullOrWhiteSpace(_jarField.Text as string)
                ? Environment.CurrentDirectory
                : _jarField.Text,
        };
        Application.Run(fd);
        if (fd.Canceled != true && !string.IsNullOrWhiteSpace(fd.Path))
        {
            _jarField.Text = fd.Path;
            if (string.IsNullOrWhiteSpace(_workDirField.Text as string))
                _workDirField.Text = Path.GetDirectoryName(fd.Path) ?? "";
        }
        fd.Dispose();
    }

    private void BrowseWorkingDir()
    {
        var fd = new FileDialog
        {
            Title = "选择工作目录",
            OpenMode = OpenMode.Directory,
            Path = string.IsNullOrWhiteSpace(_workDirField.Text as string)
                ? Environment.CurrentDirectory
                : _workDirField.Text,
            MustExist = true,
        };
        Application.Run(fd);
        if (fd.Canceled != true && !string.IsNullOrWhiteSpace(fd.Path))
            _workDirField.Text = fd.Path;
        fd.Dispose();
    }

    // ---------- 启动 / 停止 ----------

    private JarLaunchOptions CollectOptions()
    {
        var preset = JarLauncher.GcPresets.FirstOrDefault(p => p.Display == _gcDrop.Text)
                     ?? JarLauncher.GcPresets[0];
        return new JarLaunchOptions
        {
            JarPath = (_jarField.Text ?? "").Trim(),
            WorkingDir = (_workDirField.Text ?? "").Trim(),
            JavaPath = "", // 下面按下拉选的解析
            MaxMemoryMb = int.TryParse(_maxMem.Text, out var mx) ? mx : 2048,
            MinMemoryMb = int.TryParse(_minMem.Text, out var mn) ? mn : 512,
            GcPreset = preset.Key,
            ServerMode = _serverMode.Value == CheckState.Checked,
            ExtraJvmArgs = (_jvmArgs.Text ?? "").Trim(),
            ExtraArgs = (_extraArgs.Text ?? "").Trim(),
        };
    }

    private string? ResolveJava(out int major)
    {
        major = 0;
        var text = _javaDrop.Text ?? "";
        if (text.StartsWith("自动", StringComparison.Ordinal))
        {
            // 独立 jar 不知道要什么版本，挑 17 及以上最高的（服务端主流）
            var pick = JavaDetector.AutoPick();
            if (pick is null)
                return null;
            major = pick.Major;
            return pick.Path;
        }
        var hit = _javas.FirstOrDefault(j => j.Display == text);
        if (hit is null)
            return null;
        major = hit.Major;
        return hit.Path;
    }

    private async Task StartAsync()
    {
        if (_runningTask is not null)
            return; // 已经在跑

        var opt = CollectOptions();
        var err = JarLauncher.Validate(opt);
        if (err is not null)
        {
            MessageBox.ErrorQuery(Application.Instance, "启动 JAR", err, "好");
            return;
        }

        var javaPath = ResolveJava(out var javaMajor);
        if (javaPath is null)
        {
            MessageBox.ErrorQuery(Application.Instance, "启动 JAR",
                "未找到可用的 Java。请在[设置 → Java]中指定,或使用该页的下载功能。", "好");
            return;
        }

        lock (_logLock) _log.Clear();
        FlushLog();

        var jarName = Path.GetFileName(opt.JarPath);
        var item = TaskManager.Submit($"启动 JAR: {jarName}", async (task, ct) =>
        {
            task.LogLine($"工作目录: {JarLauncher.ResolveWorkingDir(opt)}");
            var code = await JarLauncher.RunAsync(opt, javaPath, javaMajor,
                line =>
                {
                    AppendLog(line);
                    task.LogLine(line);
                }, ct).ConfigureAwait(false);

            task.LogLine($"进程退出,代码 {code}");
            if (code != 0)
                throw new InvalidOperationException($"进程退出码 {code}");
        });

        // 双击任务栏里这一项 → 切回本页
        item.OnActivate = _onActivate;
        _runningTask = item;
        _startBtn.Enabled = false;
        _stopBtn.Enabled = true;

        // 任务结束后把按钮恢复（Changed 可能在后台线程触发，要 Invoke）
        void OnTaskDone()
        {
            if (!item.IsDone)
                return;
            Application.Invoke(() =>
            {
                _runningTask = null;
                _startBtn.Enabled = true;
                _stopBtn.Enabled = false;
            });
        }
        item.Changed += OnTaskDone;
        // 兜底：进程可能极快就挂了（比如 jar 有问题），Finish 早于上面这行订阅
        OnTaskDone();
    }

    private void Stop()
    {
        var task = _runningTask;
        var proc = JarLauncher.LastProcess;
        if (proc is null)
        {
            task?.Cts.Cancel();
            return;
        }
        AppendLog("=== 正在停止进程 ===");
        task?.Cts.Cancel(); // 标成"用户取消"，别记成失败
        JarLauncher.TryKill(proc);
    }

    // ---------- 日志 ----------

    /// <summary>追加一行（后台线程调用；150ms 内的合并成一次刷新）</summary>
    private void AppendLog(string line)
    {
        lock (_logLock)
        {
            _log.Add(line);
            if (_log.Count > 3000)
                _log.RemoveRange(0, 1000); // 别让内存和渲染无限涨
        }

        if ((DateTime.UtcNow - _lastFlush).TotalMilliseconds < 150)
            return;
        FlushLog();
    }

    private void FlushLog()
    {
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
