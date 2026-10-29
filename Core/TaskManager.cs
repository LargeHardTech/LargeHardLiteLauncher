namespace LargeHardLiteLauncher.Core;

/// <summary>任务状态</summary>
public enum TaskStatus
{
    /// <summary>排队/运行中</summary>
    Running,
    /// <summary>已完成</summary>
    Completed,
    /// <summary>失败</summary>
    Failed,
    /// <summary>被用户终止</summary>
    Cancelled,
}

/// <summary>
/// 后台任务项。所有预计耗时 > 0.5 秒的操作都通过 TaskManager.Submit 提交,
/// 在右侧任务栏显示, 可查看日志/终止。
/// </summary>
public sealed class TaskItem
{
    private readonly List<string> _log = new();
    private readonly object _logLock = new();

    /// <summary>所在槽位编号 (1~Capacity)</summary>
    public int Slot { get; set; }
    public string Name { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.Running;
    /// <summary>进度 0~1; -1 表示不确定(循环进度)</summary>
    public double Progress { get; set; } = -1;
    public string Error { get; set; } = "";
    public DateTime StartTime { get; set; } = DateTime.Now;

    public CancellationTokenSource Cts { get; } = new();

    /// <summary>UI 刷新事件 (状态/进度变化时触发; 调用方负责切到 UI 线程)</summary>
    public event Action? Changed;

    /// <summary>
    /// 双击任务项时要做什么（比如切到「启动 JAR」页看输出）。
    /// 为空则退回默认行为（弹日志框）。Core 层不引用 UI，所以用委托而不是 PageId。
    /// </summary>
    public Action? OnActivate { get; set; }

    public TaskItem(string name) => Name = name;

    public bool IsDone => Status is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Cancelled;

    public string StatusText => Status switch
    {
        TaskStatus.Running => Loc.T("task.running"),
        TaskStatus.Completed => Loc.T("task.completed"),
        TaskStatus.Failed => Loc.T("task.failed"),
        TaskStatus.Cancelled => Loc.T("task.cancelled"),
        _ => "?",
    };

    public string ProgressText => Progress < 0 ? "..." : $"{Progress * 100:0}%";

    /// <summary>列表显示: [1] 检查版本列表  [进行中]  42%</summary>
    public string Display => $"[{Slot}] {Name}  [{StatusText}]  {ProgressText}";

    public void Report(double progress)
    {
        Progress = Math.Clamp(progress, 0, 1);
        Changed?.Invoke();
    }

    public void LogLine(string line)
    {
        lock (_logLock)
            _log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        Changed?.Invoke();
    }

    public string LogText
    {
        get
        {
            lock (_logLock)
                return string.Join('\n', _log);
        }
    }

    public void Finish(TaskStatus status, string error = "")
    {
        Status = status;
        Error = error;
        if (status == TaskStatus.Completed)
            Progress = 1;
        Changed?.Invoke();
    }

    /// <summary>请求终止</summary>
    public void Cancel() => Cts.Cancel();
}

/// <summary>
/// 全局任务管理器。固定槽位模型: 槽位 1~Capacity, 空槽显示 IDLE,
/// 新任务占用最小的未使用槽位; 任务移除后槽位回归 IDLE。
/// 容量可在 设置 → 性能 调整 (4~256)。
/// </summary>
public static class TaskManager
{
    public const int MinCapacity = 4;
    public const int MaxCapacity = 256;

    private static readonly object _lock = new();
    private static TaskItem?[] _slots;
    private static int _capacity = 16;

    static TaskManager()
    {
        _slots = new TaskItem?[_capacity];
    }

    /// <summary>任务栏容量上限 (槽位数)</summary>
    public static int Capacity
    {
        get
        {
            lock (_lock)
                return _capacity;
        }
        set
        {
            value = Math.Clamp(value, MinCapacity, MaxCapacity);
            lock (_lock)
            {
                if (value == _capacity)
                    return;
                // 扩容保留现有任务, 缩容只裁空槽 (有任务的槽位不裁)
                var newSlots = new TaskItem?[value];
                for (int i = 0; i < Math.Min(value, _capacity); i++)
                    newSlots[i] = _slots[i];
                _slots = newSlots;
                _capacity = value;
            }
            ListChanged?.Invoke();
        }
    }

    /// <summary>槽位快照 (索引 0 对应槽位 1), 空槽为 null</summary>
    public static TaskItem?[] Slots()
    {
        lock (_lock)
            return (TaskItem?[])_slots.Clone();
    }

    /// <summary>列表变化事件 (新增/移除/容量变化)</summary>
    public static event Action? ListChanged;

    /// <summary>
    /// 任务运行中更新事件: 任务 Report(progress) / LogLine 时触发,
    /// UI 订阅它就能实时刷新进度条和日志 (不重建列表, 不打断选中)。
    /// </summary>
    public static event Action<TaskItem>? TaskUpdated;

    /// <summary>
    /// 提交后台任务。占用最小的未使用槽位。body 内使用 item.Report(progress) / item.LogLine() 更新,
    /// 通过 token 响应终止。UI 更新由调用方自行 Application.Invoke。
    /// </summary>
    public static TaskItem Submit(string name, Func<TaskItem, CancellationToken, Task> body)
    {
        var item = new TaskItem(name);
        lock (_lock)
        {
            // 找最小空闲槽位
            int slot = -1;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i] is null)
                {
                    slot = i;
                    break;
                }
            }
            if (slot < 0)
            {
                // 全满: 挤掉最旧的已完成任务, 否则兜底占最后一个
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i] is { IsDone: true })
                    {
                        slot = i;
                        break;
                    }
                }
                if (slot < 0)
                    slot = _slots.Length - 1;
            }
            item.Slot = slot + 1;
            _slots[slot] = item;
        }
        // 任务内部每次 Report/LogLine 都会触发 Changed, 转发给 UI
        item.Changed += () => TaskUpdated?.Invoke(item);
        ListChanged?.Invoke();

        _ = Task.Run(async () =>
        {
            try
            {
                await body(item, item.Cts.Token);
                item.Finish(item.Cts.IsCancellationRequested ? TaskStatus.Cancelled : TaskStatus.Completed);
            }
            catch (OperationCanceledException)
            {
                item.Finish(TaskStatus.Cancelled);
            }
            catch (Exception ex)
            {
                item.LogLine($"错误: {ex.Message}");
                item.Finish(TaskStatus.Failed, ex.Message);
            }
            finally
            {
                ListChanged?.Invoke();
            }
        });

        return item;
    }

    /// <summary>终止指定任务</summary>
    public static void Cancel(TaskItem item)
    {
        item.Cancel();
        item.LogLine("用户请求终止");
        ListChanged?.Invoke();
    }

    /// <summary>移除指定任务, 槽位回归 IDLE (终态任务自动清理用)</summary>
    public static void Remove(TaskItem item)
    {
        lock (_lock)
        {
            int idx = item.Slot - 1;
            if (idx >= 0 && idx < _slots.Length && ReferenceEquals(_slots[idx], item))
                _slots[idx] = null;
        }
        ListChanged?.Invoke();
    }
}
