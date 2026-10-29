using Terminal.Gui;

namespace LargeHardLiteLauncher.UI;

/// <summary>
/// 启动动画:
/// 0. 黑屏, 正中显示 LARGEHARD 大字 banner (逐行 Label)
/// 1. banner 撤掉, 双线「假边框」居中闪两下
/// 2. 假边框横向占满, 竖向从 0 高度长到全屏
/// 3. 撤掉假边框、恢复真边框 (内容仍隐藏)
/// 4. 各大控件在 0~500ms 内随机时刻「亮 → 灭 → 常亮」(用 View.Visible 切换, 不动配色)
/// </summary>
public static class StartupAnimation
{
    private const int TickMs = 12;          // 每帧间隔
    private const int RowsPerTick = 3;      // 每帧增高 (越大展开越快)
    private const int FlashWindowMs = 500;
    private const int FlashGapMs = 70;
    private const int BannerHoldMs = 950;   // banner 停留时长 (原 700, 用户要求 +250)

    /// <summary>LARGEHARD 大字 (逐行, 保持原样空格)</summary>
    private static readonly string[] Banner =
    {
        "██╗      █████╗ ██████╗  ██████╗ ███████╗██╗  ██╗ █████╗ ██████╗ ██████╗",
        "██║     ██╔══██╗██╔══██╗██╔════╝ ██╔════╝██║  ██║██╔══██╗██╔══██╗██╔══██╗",
        "██║     ███████║██████╔╝██║  ███╗█████╗  ███████║███████║██████╔╝██║  ██║",
        "██║     ██╔══██║██╔══██╗██║   ██║██╔══╝  ██╔══██║██╔══██║██╔══██╗██║  ██║",
        "███████╗██║  ██║██║  ██║╚██████╔╝███████╗██║  ██║██║  ██║██║  ██║██████╔╝",
        "╚══════╝╚═╝  ╚═╝╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═╝╚═╝  ╚═╝╚═╝  ╚═╝╚═════╝",
    };

    /// <summary>
    /// 播放动画。flashed 为需要闪烁的顶层控件 (原本不可见的会被跳过)。
    /// </summary>
    public static void Play(Window win, IReadOnlyList<View> flashed, bool flashControls = true,
        Action? onFinished = null)
    {
        var realStyle = win.BorderStyle;
        var targets = flashed.Where(v => v.Visible).ToList();
        if (targets.Count == 0)
        {
            onFinished?.Invoke();
            return;
        }

        // 清场: 内容全隐, 真边框藏起来
        foreach (var v in targets)
            v.Visible = false;
        win.BorderStyle = LineStyle.None;

        var fake = new FrameView
        {
            Title = "LargeHardLiteLauncher",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1, // 起始只有一行高 (0 高度什么都看不见)
            BorderStyle = LineStyle.Double,
            CanFocus = false,
            Visible = false, // 加进来时先藏着, 否则首帧会闪现在顶部 (Y=0)
        };
        win.Add(fake);
        win.SetNeedsDraw();

        int maxH = 0;
        int h = 0;
        bool growStarted = false;

        void Grow()
        {
            if (maxH == 0)
                maxH = Math.Max(win.Viewport.Height, 8);

            h = Math.Min(h + RowsPerTick, maxH);
            fake.Y = Math.Max(0, (maxH - h) / 2); // 垂直居中扩展
            fake.Height = h;
            win.SetNeedsDraw();

            if (h < maxH)
            {
                Application.AddTimeout(TimeSpan.FromMilliseconds(TickMs), () =>
                {
                    Grow();
                    return false;
                });
                return;
            }

            // 长满: 撤假边框, 真边框回来 (内容还藏着)
            win.Remove(fake);
            fake.Dispose();
            win.BorderStyle = realStyle;
            win.SetNeedsDraw();

            // 重复实例: 不加载控件, 直接回调 (弹提示并退出)
            if (!flashControls)
            {
                onFinished?.Invoke();
                return;
            }

            Flash(targets, onFinished);
        }

        void BeginGrow()
        {
            if (growStarted)
                return;
            growStarted = true;
            Application.AddTimeout(TimeSpan.FromMilliseconds(TickMs), () =>
            {
                Grow();
                return false;
            });
        }

        // 整个动画延到首帧之后跑: 构造期 Viewport 还是 0, 早点算会算错中心
        Application.AddTimeout(TimeSpan.FromMilliseconds(1), () =>
        {
            maxH = Math.Max(win.Viewport.Height, 8);

            // ===== 第 0 阶段: 正中显示大字 banner =====
            var banner = ShowBanner(win, Math.Max(win.Viewport.Width, 20), maxH);
            // banner 也闪两下: 灭 → 亮 → 灭 → 亮
            foreach (var bv in banner)
                bv.Visible = false;
            foreach (var bv in banner)
            {
                SetVisibleLater(bv, 40, true);   // 亮 1
                SetVisibleLater(bv, 120, false);
                SetVisibleLater(bv, 200, true);  // 亮 2
                SetVisibleLater(bv, 280, false);
                SetVisibleLater(bv, 360, true);  // 亮 3 (常亮)
            }
            win.SetNeedsDraw();

            Application.AddTimeout(TimeSpan.FromMilliseconds(BannerHoldMs), () =>
            {
                foreach (var v in banner)
                {
                    win.Remove(v);
                    v.Dispose();
                }
                win.SetNeedsDraw();

                // ===== 第 1 阶段: 假边框直接以 1 行高出现在正中 (不再闪) =====
                fake.Y = Math.Max(0, (maxH - 1) / 2);
                fake.Visible = true;
                win.SetNeedsDraw();

                // ===== 第 2 阶段: 从这一行开始向上下展开 =====
                Application.AddTimeout(TimeSpan.FromMilliseconds(40), () =>
                {
                    h = 1;
                    BeginGrow();
                    return false;
                });
                return false;
            });
            return false;
        });
    }

    /// <summary>把大字 banner 逐行铺成 Label, 居中显示</summary>
    private static List<View> ShowBanner(Window win, int width, int height)
    {
        var views = new List<View>(Banner.Length);
        int lineW = Banner.Max(l => l.Length);
        int cx = Math.Max(0, (width - lineW) / 2);
        int cy = Math.Max(0, (height - Banner.Length) / 2);

        for (int i = 0; i < Banner.Length; i++)
        {
            var lbl = new Label
            {
                Text = Banner[i],
                X = cx,
                Y = cy + i,
                CanFocus = false,
            };
            win.Add(lbl);
            views.Add(lbl);
        }
        return views;
    }

    /// <summary>各控件随机时刻 亮 → 灭 → 常亮</summary>
    private static void Flash(IReadOnlyList<View> targets, Action? onFinished)
    {
        var rnd = new Random();
        foreach (var v in targets)
        {
            int at = rnd.Next(0, FlashWindowMs);
            SetVisibleLater(v, at, true);                    // 亮
            SetVisibleLater(v, at + FlashGapMs, false);      // 灭
            SetVisibleLater(v, at + FlashGapMs * 2, true);   // 常亮
        }

        Application.AddTimeout(TimeSpan.FromMilliseconds(FlashWindowMs + FlashGapMs * 2 + 120), () =>
        {
            foreach (var v in targets)
                v.Visible = true;
            onFinished?.Invoke();
            return false;
        });
    }

    private static void SetVisibleLater(View v, int ms, bool visible)
    {
        Application.AddTimeout(TimeSpan.FromMilliseconds(ms), () =>
        {
            v.Visible = visible;
            v.SetNeedsDraw();
            return false;
        });
    }
}
