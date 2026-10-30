using System.Collections.ObjectModel;
using LargeHardLiteLauncher.Core;
using Terminal.Gui;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace LargeHardLiteLauncher.UI;

/// <summary>
/// 更多页（照 PCL 的布局）：左边二级导航（帮助 / 关于与鸣谢 / 百宝箱 / 反馈 / 新功能投票），
/// 右边对应内容。反馈和新功能投票点开浏览器，不切内容区。
/// </summary>
public class MorePage : PageBase
{
    private static readonly string[] NavItems =
    {
        "帮助", "关于与鸣谢", "百宝箱", "反馈", "新功能投票",
    };

    /// <summary>反馈 / 新功能投票都先指到这个（PCL 是官方论坛，我们先用 GitHub）</summary>
    private const string GithubUrl = "https://github.com/LargeHardTech";

    private readonly View _content;
    private readonly List<string> _shownNav = new();

    // 回声洞：内容可由 GitHub 讨论帖提供，这几个字段保存当前列表与来源说明
    private List<string> _echoes = EchoHole.Builtin.ToList();
    private string _echoSource = "内置";
    private Label? _echoLabel;
    private FrameView? _echoBox;

    public MorePage(LauncherConfig config) : base(config, $" {Loc.T("tab.more")} ")
    {
        var navBox = new FrameView
        {
            Title = " 更多 ",
            X = 0,
            Y = 0,
            Width = 17,
            Height = Dim.Fill(),
            BorderStyle = LineStyle.Single,
        };
        _shownNav.AddRange(NavItems);
        var nav = new ListView<string>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        nav.SetSource(new ObservableCollection<string>(_shownNav));
        navBox.Add(nav);

        _content = new View
        {
            X = 17,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        void Activate(int idx)
        {
            if (idx is 3 or 4)
            {
                // 反馈 / 新功能投票：直接开浏览器，内容区不动
                OpenUrl(GithubUrl);
                return;
            }
            Render(idx);
        }

        nav.ValueChanged += (_, _) => Activate(Index(_shownNav, nav.SelectedItem));
        nav.Accepted += (_, _) => Activate(Index(_shownNav, nav.SelectedItem));

        Add(navBox, _content);
        Render(0);
    }

    private static int Index(List<string> list, object? item) =>
        item is null ? 0 : Math.Max(0, list.IndexOf(item.ToString() ?? ""));

    private void Render(int idx)
    {
        _content.RemoveAll();
        switch (idx)
        {
            case 1: BuildAbout(); break;
            case 2: BuildToolbox(); break;
            default: BuildHelp(); break;
        }
        _content.SetNeedsLayout();
        _content.SetNeedsDraw();
    }

    // ================= 帮助 =================

    private void BuildHelp()
    {
        var guidesBox = new FrameView
        {
            Title = " 指南 ",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 10,
            BorderStyle = LineStyle.Single,
        };

        var search = new TextField { X = 1, Y = 0, Width = Dim.Fill(2) };
        var guideList = new ListView<string>
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        var guides = new (string Title, string Desc)[]
        {
            ("Minecraft 教程", "安装原版游戏,加载器与选择 Java 的完整顺序"),
            ("整合包制作指南", "使用[导出整合包]把当前 Mod 组合打包分享"),
            ("资源安装指南", "Mod,资源包,光影与数据包各自的存放位置"),
            ("崩溃排查指南", "游戏异常退出后的排查步骤"),
            ("开服指南", "使用[启动 JAR]页配置内存与 GC 启动服务端"),
        };

        void Rebuild()
        {
            var q = (search.Text ?? "").Trim();
            var rows = guides
                .Where(g => q.Length == 0
                            || g.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || g.Desc.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(g => $"{g.Title,-16} {g.Desc}")
                .ToList();
            if (rows.Count == 0)
                rows.Add("(没有匹配的条目)");
            guideList.SetSource(new ObservableCollection<string>(rows));
        }

        search.TextChanged += (_, _) => Rebuild();
        guideList.Accepted += (_, _) =>
        {
            var sel = guideList.SelectedItem?.ToString() ?? "";
            var hit = guides.FirstOrDefault(g => sel.StartsWith(g.Title, StringComparison.Ordinal));
            if (hit.Title is null)
                return;
            MessageBox.Query(Application.Instance, hit.Title, GuideBody(hit.Title), "好");
        };

        guidesBox.Add(new Label { Text = "搜索", X = 0, Y = 0 }, search, guideList);

        _content.Add(guidesBox);
        _content.Add(new Label
        {
            Text = "快捷键   F1 启动   F2 下载   F3 联机   F4 启动 JAR   F5 设置   F6 更多",
            X = 0,
            Y = 11,
            Width = Dim.Fill(1),
        });
        _content.Add(new Label
        {
            Text = "命令行传入 jar 路径,或把 jar 拖到程序图标上,可直接进入[启动 JAR]页",
            X = 0,
            Y = 12,
            Width = Dim.Fill(1),
        });
        _content.Add(new Label
        {
            Text = "使用 ↑ / ↓ 选择条目,回车查看内容",
            X = 0,
            Y = 14,
            Width = Dim.Fill(1),
        });

        Rebuild();
    }

    private static string GuideBody(string title) => title switch
    {
        "Minecraft 教程" =>
            "1) 在[下载 → 原版游戏]中安装一个版本(建议选择正式版)\n" +
            "2) 双击版本列表中的条目,安装 Forge 或 Fabric(仅使用 Mod 时需要)\n" +
            "3) 回到[启动]页选中该版本,点击启动游戏\n\n" +
            "Java 无需预先安装:设置页提供[下载 Java]入口。1.20.5 及以上选 21,1.18 至 1.20.4 选 17。",
        "整合包制作指南" =>
            "入口:启动页 → 版本设置 → 概览 → 第二行[导出整合包]。\n\n" +
            "勾选[联网匹配在线项目]后,能在 Modrinth 查询到的 Mod 只记录链接,不打包进文件。\n" +
            "实测 62 个 Mod 的整合包由 518 MB 降至 186 MB。\n\n" +
            "导出的 .mrpack 可供他人安装,也可通过本启动器的整合包安装功能还原。",
        "资源安装指南" =>
            "Mod          → mods/(.jar,禁用时改名为 .jar.disabled)\n" +
            "资源包       → resourcepacks/(.zip 或文件夹)\n" +
            "光影         → shaderpacks/(需要 Iris 或 OptiFine 才生效)\n" +
            "数据包       → datapacks/(全局)或 saves/<世界>/datapacks/\n\n" +
            "以上内容均可在版本设置中启用,禁用或删除。",
        "崩溃排查指南" =>
            "1) 查看启动器弹出的崩溃分析结果,常见原因均有说明\n" +
            "2) 提示文件缺失时,执行[版本设置 → 补全版本]\n" +
            "3) 提示 Mod 冲突或缺少前置时,在 Mod 管理中删除或补充\n" +
            "4) 仍无法解决时更换 Java(设置 → Java),1.20.1 建议使用 17\n" +
            "5) 最后可查看 crash-reports 目录中的完整堆栈信息",
        "开服指南" =>
            "[启动 JAR]页(F4):\n" +
            "1) 选择 server.jar,工作目录会自动跟随该文件\n" +
            "2) 内存按在线人数配置:10 人建议 4G,20 人建议 8G\n" +
            "3) GC 默认使用 G1;内存 8G 以上且 Java 17 及以上可尝试 ZGC\n" +
            "4) 保持[服务端模式]勾选(向服务端传递 nogui 参数)\n" +
            "5) 右下角命令输入框可直接执行 stop,list 等指令\n\n" +
            "首次启动需同意 EULA:运行一次后退出,将 eula.txt 中的值改为 true 再重新启动。",
        _ => "(没有这个条目)",
    };

    // ================= 关于与鸣谢 =================

    private void BuildAbout()
    {
        var aboutBox = new FrameView
        {
            Title = " 关于 ",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 11,
            BorderStyle = LineStyle.Single,
        };
        aboutBox.Add(
            new Label { Text = "LargeHardLiteLauncher", X = 1, Y = 0, Width = Dim.Fill(1) },
            new Label { Text = "开发者    巨硬科技 LHT", X = 1, Y = 2, Width = Dim.Fill(1) },
            new Label { Text = "模型      DeepSeek V4.1 Flash", X = 1, Y = 3, Width = Dim.Fill(1) },
            new Label { Text = "版本      v0.1.0(NativeAOT 单文件,零运行时依赖)", X = 1, Y = 4, Width = Dim.Fill(1) },
            new Label { Text = "数据目录  " + LauncherConfig.RootDir, X = 1, Y = 6, Width = Dim.Fill(1) },
            new Label { Text = "游戏目录  " + Config.MinecraftPath, X = 1, Y = 7, Width = Dim.Fill(1) });

        var ghBtn = MkButton("GitHub", 1, 9);
        var logBtn = MkButton("打开日志", 14, 9);
        ghBtn.Accepting += (_, _) => OpenUrl(GithubUrl);
        logBtn.Accepting += (_, _) => OpenFolder(Path.Combine(LauncherConfig.RootDir, "logs"));
        aboutBox.Add(ghBtn, logBtn);

        var thanksBox = new FrameView
        {
            Title = " 特别鸣谢 ",
            X = 0,
            Y = 11,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.Single,
        };

        var thanks = new (string Name, string Desc, string Url)[]
        {
            ("陶瓦联机", "P2P 联机内核(Terracotta),本启动器内嵌分发 · AGPL-3.0",
                "https://github.com/burningtnt/Terracotta"),
            ("EasyTier", "去中心化 P2P 组网,陶瓦联机的底层网络",
                "https://easytier.cn"),
            ("BMCLAPI", "提供 Minecraft 下载镜像和 Forge 安装器", "https://bmclapi2.bangbang93.com"),
            ("MC 百科", "提供 Mod 的中文翻译和相关资料", "https://www.mcmod.cn"),
            ("Modrinth", "提供 Mod / 整合包 / 资源包的搜索和下载", "https://modrinth.com"),
            ("CurseForge", "提供 Mod 资源(需要 API key)", "https://www.curseforge.com"),
            ("Adoptium", "提供 Java 运行时的下载(Temurin 构建)", "https://adoptium.net"),
            ("Terminal.Gui", "这个终端界面的 UI 框架", "https://github.com/gui-cs/Terminal.Gui"),
        };

        var thanksList = new ListView<string>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        var rows = thanks.Select(t => $"{t.Name,-14} {t.Desc}").ToList();
        thanksList.SetSource(new ObservableCollection<string>(rows));
        thanksList.Accepted += (_, _) =>
        {
            var i = thanksList.SelectedItem is { } s ? rows.IndexOf(s) : -1;
            if (i >= 0 && i < thanks.Length)
                OpenUrl(thanks[i].Url);
        };
        thanksBox.Add(thanksList);

        _content.Add(aboutBox, thanksBox);
    }

    // ================= 百宝箱 =================

    private void BuildToolbox()
    {
        var toolBox = new FrameView
        {
            Title = " 工具 ",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 8,
            BorderStyle = LineStyle.Single,
        };

        // 按钮用链式定位（不写死 X），宽度 = 文字宽度 + 4（NoPadding 按钮左右各有 1 列边框，
        // +1 会换行，+4 是不换行的最小值）
        var cleanBtn = MkButton("清理下载残留", 0, 0);
        var javaBtn = MkButton("检查 Java", Pos.Right(cleanBtn) + 2, 0);
        var dataBtn = MkButton("数据目录", Pos.Right(javaBtn) + 2, 0);
        var gameBtn = MkButton("游戏目录", Pos.Right(dataBtn) + 2, 0);
        var neverBtn = MkButton("千万别点", Pos.Right(gameBtn) + 2, 0);

        cleanBtn.Accepting += (_, _) => CleanLeftovers();
        javaBtn.Accepting += (_, _) => ShowJavaList();
        dataBtn.Accepting += (_, _) => OpenFolder(LauncherConfig.RootDir);
        gameBtn.Accepting += (_, _) => OpenFolder(Config.MinecraftPath);
        neverBtn.Accepting += (_, _) => NeverClick();

        toolBox.Add(cleanBtn, javaBtn, dataBtn, gameBtn, neverBtn,
            new Label
            {
                Text = "下载残留指各版本目录下的 .tmp 文件(未完成的下载),清理不影响已安装内容",
                X = 1,
                Y = 3,
                Width = Dim.Fill(1),
            });

        // ---- 回声洞：点 label 或整个 groupbox 都刷新 ----
        var echoBox = new FrameView
        {
            Title = $" 回声洞 · {_echoSource} ",
            X = 0,
            Y = 8,
            Width = Dim.Fill(),
            Height = 5,
            BorderStyle = LineStyle.Single,
        };

        var echoLabel = new Label
        {
            Text = PickEcho(null),
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            // Label 默认不可聚焦，鼠标事件会被跳过；给它焦点才能点
            CanFocus = true,
        };

        // 异步刷新回来时要用到这两个引用
        _echoBox = echoBox;
        _echoLabel = echoLabel;

        void Roll()
        {
            echoLabel.Text = PickEcho(echoLabel.Text);
            echoLabel.SetNeedsDraw();
        }

        echoLabel.MouseEvent += (_, e) =>
        {
            if (e.IsSingleClicked)
                Roll();
        };
        echoBox.MouseEvent += (_, e) =>
        {
            if (e.IsSingleClicked)
                Roll();
        };

        echoBox.Add(echoLabel);
        _content.Add(toolBox, echoBox);
    }

    // ================= 回声洞内容 =================

    /// <summary>随机抽一句。和当前显示的一样时顺延到下一句，否则看着像没反应。</summary>
    private string PickEcho(string? current)
    {
        if (_echoes.Count == 0)
            return EchoHole.Builtin[0];

        var next = _echoes[Random.Shared.Next(_echoes.Count)];
        if (current is not null && next == current && _echoes.Count > 1)
            next = _echoes[(_echoes.IndexOf(next) + 1) % _echoes.Count];
        return next;
    }

    /// <summary>
    /// 每次进页面都尝试刷新一次。内部有 6 小时缓存，正常情况不会重复请求。
    /// 取不到内容时静默保留内置句子，不弹错误。
    /// </summary>
    public override void OnPageEnter()
    {
        _ = RefreshEchoesAsync();
    }

    private async Task RefreshEchoesAsync()
    {
        var result = await EchoHole.LoadAsync(Config).ConfigureAwait(false);

        try
        {
            Application.Invoke(Apply);
        }
        catch
        {
            // 测试或退出过程中 Application 可能已经关掉，忽略
        }

        void Apply()
        {
            try
            {
                _echoes = result.Items;
                _echoSource = result.Source;

                if (_echoBox is not null)
                    _echoBox.Title = $" 回声洞 · {_echoSource} ";
                if (_echoLabel is not null)
                    _echoLabel.Text = PickEcho(null);

                _echoBox?.SetNeedsDraw();
                _echoLabel?.SetNeedsDraw();
            }
            catch
            {
                // 控件可能已销毁，忽略即可
            }
        }
    }

    // ================= 千万别点（学 PCL 的彩蛋）=================

    /// <summary>
    /// 免责声明 → 随机一种恶搞形态。
    /// 所有形态都是独立的全屏/独立 Dialog，**不碰主窗口状态**，玩坏了最多关掉就恢复。
    /// </summary>
    private void NeverClick()
    {
        var r = MessageBox.Query(Application.Instance, "免责声明",
            "此按钮没有实际功能。\n\n" +
            "点击后只会短暂改变界面显示,不会删除文件,不会上传数据,也不会影响存档与游戏版本。",
            "继续", "取消");
        if (r != 0)
            return;

        switch (Random.Shared.Next(7))
        {
            case 0: FakeBsod(); break;
            case 1: FakeDeleteAll(); break;
            case 2: GarbageScreen(); break;
            case 3: FakeWindowsUpdate(); break;
            case 4: FakeCmdFlood(); break;
            case 5: FakeCrashedDialog(); break;
            default: FakeRansomNote(); break;
        }
    }

    /// <summary>注册一个临时配色（TG 2.5 的配色只能按名字注册）</summary>
    private static void RegisterScheme(string name, Color fg, Color bg, Color accent)
    {
        SchemeManager.AddScheme(name, new Scheme(new Attribute(fg, bg))
        {
            Normal = new Attribute(fg, bg),
            Focus = new Attribute(bg, fg),
            HotNormal = new Attribute(accent, bg),
            HotFocus = new Attribute(bg, accent),
            Disabled = new Attribute(Color.Gray, bg),
        });
    }

    /// <summary>形态四：假 Windows 更新（经典卡 35%）</summary>
    private static void FakeWindowsUpdate()
    {
        RegisterScheme("LLL_Update", Color.White, Color.Blue, Color.BrightCyan);
        var dlg = new Dialog
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.Single,
            SchemeName = "LLL_Update",
        };

        var title = new Label { Text = "正在配置 Windows 更新", X = 2, Y = 2, Width = Dim.Fill(4) };
        var pctLbl = new Label { Text = "0% 完成", X = 2, Y = 4, Width = Dim.Fill(4) };
        var bar = new ProgressBar { X = 2, Y = 5, Width = Dim.Fill(6), Height = 1 };
        var tip = new Label
        {
            Text = "请不要关闭计算机。这可能需要一些时间。",
            X = 2,
            Y = 7,
            Width = Dim.Fill(4),
        };

        var stages = new[] { "正在配置 Windows 更新", "正在还原更改", "正在清理,请不要关闭计算机" };
        var stage = 0;
        var pct = 0;
        var stall = 0;
        var stalled35 = false;

        Application.AddTimeout(TimeSpan.FromMilliseconds(260), () =>
        {
            try
            {
                if (stall > 0)
                {
                    stall--;
                    return true; // 卡着不动，最像真的
                }

                pct += Random.Shared.Next(1, 5);
                if (stage == 0 && !stalled35 && pct > 35)
                {
                    pct = 35;
                    stall = 8; // 经典：卡死在 35%
                    stalled35 = true;
                }
                if (pct >= 100)
                {
                    stage++;
                    pct = 0;
                    if (stage >= stages.Length)
                    {
                        dlg.RequestStop();
                        return false;
                    }
                    title.Text = stages[stage];
                    tip.Text = stage == 2 ? "别关别关别关别关..." : "请不要关闭计算机。这可能需要一些时间。";
                    title.SetNeedsDraw();
                    tip.SetNeedsDraw();
                }

                pctLbl.Text = $"{pct}% 完成";
                bar.Fraction = pct / 100f;
                pctLbl.SetNeedsDraw();
                bar.SetNeedsDraw();
            }
            catch
            {
                return false;
            }
            return true;
        });

        dlg.Add(title, pctLbl, bar, tip);
        Application.Run(dlg);
        dlg.Dispose();
    }

    /// <summary>形态五：假 CMD 疯狂刷屏</summary>
    private static void FakeCmdFlood()
    {
        RegisterScheme("LLL_Cmd", Color.White, Color.Black, Color.BrightGreen);
        var dlg = new Dialog
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.Single,
            SchemeName = "LLL_Cmd",
        };
        var body = new Label { Text = "", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        dlg.Add(body);

        var cmds = new[]
        {
            @"C:\Windows\system32> del /f /s /q C:\*.*",
            @"C:\Windows\system32> format D: /q /fs:NTFS",
            @"C:\Windows\system32> rd /s /q C:\Users\lwy",
            @"C:\Windows\system32> takeown /f C:\Windows /r",
            @"C:\Windows\system32> icacls C:\ /grant Everyone:F",
            @"C:\Windows\system32> cipher /w:C",
            @"C:\Windows\system32> bcdedit /set {default} safeboot minimal",
            @"C:\Windows\system32> sc delete DcomLaunch",
            @"C:\Windows\system32> reg delete HKLM\SOFTWARE /f",
            @"C:\Windows\system32> net user administrator /active:yes",
        };
        var replies = new[]
        {
            "系统找不到指定的文件。",
            "拒绝访问。",
            "另一个程序正在使用此文件,进程无法访问。",
            "文件名,目录名或卷标语法不正确。",
            "操作成功完成。",
            "错误:系统找不到指定的注册表项或值。",
        };

        var lines = new List<string> { "Microsoft Windows [版本 10.0.26100.1742]", "(c) Microsoft Corporation。保留所有权利。", "" };
        var ticks = 0;
        Application.AddTimeout(TimeSpan.FromMilliseconds(55), () =>
        {
            try
            {
                ticks++;
                if (ticks > 90)
                {
                    lines.Add("");
                    lines.Add("好,什么都没发生 :)  按 Esc 回去");
                    body.Text = string.Join('\n', lines.TakeLast(24));
                    body.SetNeedsDraw();
                    return false;
                }

                if (Random.Shared.Next(3) == 0)
                    lines.Add("  " + replies[Random.Shared.Next(replies.Length)]);
                else
                    lines.Add(cmds[Random.Shared.Next(cmds.Length)]);

                body.Text = string.Join('\n', lines.TakeLast(24));
                body.SetNeedsDraw();
            }
            catch
            {
                return false;
            }
            return true;
        });

        Application.Run(dlg);
        dlg.Dispose();
    }

    /// <summary>形态六：假「已停止工作」+ 经典三按钮</summary>
    private static void FakeCrashedDialog()
    {
        var dlg = new Dialog
        {
            Title = "LargeHardLiteLauncher",
            Width = 64,
            Height = 12,
            BorderStyle = LineStyle.Single,
        };

        dlg.Add(new Label
        {
            Text = "LargeHardLiteLauncher 已停止工作",
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
        });
        dlg.Add(new Label
        {
            Text = "Windows 可以联机检查该问题的解决方案。",
            X = 1,
            Y = 3,
            Width = Dim.Fill(2),
        });
        dlg.Add(new Label
        {
            Text = "(它活得好好的,这是个假的)",
            X = 1,
            Y = 4,
            Width = Dim.Fill(2),
        });

        var b1 = new Button { Text = "联机检查解决方案", X = 1, Y = 6, NoPadding = true, Width = 22 };
        var b2 = new Button { Text = "关闭程序", X = Pos.Right(b1) + 1, Y = 6, NoPadding = true, Width = 14 };
        var b3 = new Button { Text = "调试程序", X = 1, Y = 8, NoPadding = true, Width = 14 };

        b1.Accepting += (_, _) =>
        {
            MessageBox.ErrorQuery(Application.Instance, "联机检查解决方案",
                "无法连接到 Internet。\n\n错误代码 0x80072EE7\n\n(骗你的,网络好得很)", "好");
        };
        b2.Accepting += (_, _) => dlg.RequestStop();
        b3.Accepting += (_, _) =>
        {
            MessageBox.ErrorQuery(Application.Instance, "调试程序",
                "未安装 Visual Studio,无法调试。\n\n就算装了也没用,因为程序没崩。", "好");
        };

        dlg.Add(b1, b2, b3);
        Application.Run(dlg);
        dlg.Dispose();
    }

    /// <summary>形态七：假勒索信（红底白字，2.5 秒后自曝）</summary>
    private static void FakeRansomNote()
    {
        RegisterScheme("LLL_Ransom", Color.White, Color.Red, Color.BrightYellow);
        var dlg = new Dialog
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.Single,
            SchemeName = "LLL_Ransom",
        };

        var body = new Label { Text = "", X = 3, Y = 2, Width = Dim.Fill(6), Height = Dim.Fill(4) };
        dlg.Add(body);

        var left = 72 * 3600; // 假倒计时
        string Note(bool exposed) => exposed
            ? "开玩笑的 :)\n\n" +
              "你的存档,mod,截图全都好好的,一个字节都没动。\n\n" +
              "按 Esc 或点下面按钮回去。"
            : "        你的文件已被加密\n\n" +
              "  你的所有存档,mod,截图都已经\n" +
              "  用军用级 AES-256 加密过了。\n\n" +
              "  想恢复?请在 72 小时内向下面地址\n" +
              "  转入 0.5 BTC:\n" +
              "  1A2b3C4d5E6f7G8h9I0jK1lM2nO3pQ4rS5t\n\n" +
              "  然后在 MC 百科私信 @PeterMC10086\n\n" +
              $"  倒计时: {left / 3600:00}:{left / 60 % 60:00}:{left % 60:00}";

        body.Text = Note(false);

        Application.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            try
            {
                left -= 300 / 1000 * 60; // 加速 60 倍，看着有时间在跑
                if (left < 0)
                    left = 72 * 3600;
                body.Text = Note(false);
                body.SetNeedsDraw();
            }
            catch
            {
                return false;
            }
            return true;
        });

        Application.AddTimeout(TimeSpan.FromMilliseconds(2600), () =>
        {
            try
            {
                body.Text = Note(true);
                body.SetNeedsDraw();
            }
            catch
            {
                return false;
            }
            return false; // 只执行一次
        });

        var ok = new Button { Text = "好险", X = Pos.Center(), Y = Pos.AnchorEnd(3) };
        ok.Accepting += (_, _) => dlg.RequestStop();
        dlg.Add(ok);
        Application.Run(dlg);
        dlg.Dispose();
    }

    /// <summary>形态一：假蓝屏（Windows 蓝底白字那套）</summary>
    private static void FakeBsod()
    {
        // TG 2.5 的配色是按名字注册的（SchemeManager），View 上只能设 SchemeName
        const string schemeName = "LLL_FakeBsod";
        SchemeManager.AddScheme(schemeName, new Scheme(new Attribute(Color.White, Color.Blue))
        {
            Normal = new Attribute(Color.White, Color.Blue),
            Focus = new Attribute(Color.Blue, Color.White),
            HotNormal = new Attribute(Color.BrightYellow, Color.Blue),
            HotFocus = new Attribute(Color.BrightYellow, Color.Blue),
            Disabled = new Attribute(Color.Gray, Color.Blue),
        });

        var dlg = new Dialog
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.Single,
            SchemeName = schemeName,
        };

        var body = new Label
        {
            Text = "",
            X = 2,
            Y = 1,
            Width = Dim.Fill(4),
            Height = Dim.Fill(4),
        };

        string Render(int pct) =>
            " :(\n\n" +
            " 你的启动器遇到问题,需要重新启动。\n\n" +
            " 我们只是在收集一些错误信息,然后会为你重启。\n\n" +
            $" {pct}% 完成\n\n" +
            " 更多信息请访问:\n" +
            " STOP CODE: YOU_CLICKED_IT_ANYWAY\n\n" +
            " 你到底为什么要点它?";

        body.Text = Render(0);
        var pct = 0;
        Application.AddTimeout(TimeSpan.FromMilliseconds(350), () =>
        {
            pct = Math.Min(100, pct + Random.Shared.Next(4, 15));
            try
            {
                body.Text = pct >= 100
                    ? " :)\n\n 开玩笑的。\n\n 什么都没坏,按 Esc 或点下面按钮回去。"
                    : Render(pct);
                body.SetNeedsDraw();
            }
            catch
            {
                return false; // 对话框已经销毁，别再碰控件
            }
            return pct < 100;
        });

        var ok = new Button { Text = "我错了", X = Pos.Center(), Y = Pos.AnchorEnd(3) };
        ok.Accepting += (_, _) => dlg.RequestStop();
        dlg.Add(body, ok);
        Application.Run(dlg);
        dlg.Dispose();
    }

    /// <summary>形态二：假"删除系统盘"进度条</summary>
    private static void FakeDeleteAll()
    {
        var dlg = new Dialog
        {
            Title = "系统清理",
            Width = 62,
            Height = 12,
            BorderStyle = LineStyle.Single,
        };
        var label = new Label { Text = "正在删除 C:\\Windows\\System32 ...", X = 1, Y = 1, Width = Dim.Fill(2) };
        var sub = new Label { Text = "0%", X = 1, Y = 2, Width = Dim.Fill(2) };
        var bar = new ProgressBar { X = 1, Y = 4, Width = Dim.Fill(2), Height = 1 };
        var ok = new Button { Text = "停止", X = 1, Y = 6 };

        var pct = 0.0;
        Application.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            pct = Math.Min(1.0, pct + Random.Shared.NextDouble() * 0.12);
            try
            {
                bar.Fraction = (float)pct;
                sub.Text = $"{pct * 100:0}%{(pct > 0.85 ? "   快好了，别关…" : "")}";
                if (pct >= 1.0)
                {
                    label.Text = "开玩笑的,一个字节都没删 :)";
                    sub.Text = "你的存档和版本都好好的";
                    ok.Text = "好";
                }
                label.SetNeedsDraw();
                sub.SetNeedsDraw();
                bar.SetNeedsDraw();
            }
            catch
            {
                return false;
            }
            return pct < 1.0;
        });

        ok.Accepting += (_, _) => dlg.RequestStop();
        dlg.Add(label, sub, bar, ok);
        Application.Run(dlg);
        dlg.Dispose();
    }

    /// <summary>形态三：满屏乱码，2 秒后自己消失</summary>
    private static void GarbageScreen()
    {
        const string schemeName = "LLL_Garbage";
        SchemeManager.AddScheme(schemeName, new Scheme(new Attribute(Color.BrightGreen, Color.Black))
        {
            Normal = new Attribute(Color.BrightGreen, Color.Black),
            Focus = new Attribute(Color.Black, Color.BrightGreen),
            HotNormal = new Attribute(Color.BrightGreen, Color.Black),
            HotFocus = new Attribute(Color.Black, Color.BrightGreen),
            Disabled = new Attribute(Color.Green, Color.Black),
        });

        var dlg = new Dialog
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.Single,
            SchemeName = schemeName,
        };

        const string glyphs = "▓▒░█▄▀■□◆◇○●※＃＄％＆0123456789ABCDEF";
        var sb = new System.Text.StringBuilder();
        var width = Math.Clamp(Application.Driver?.Cols ?? 80, 40, 160);
        for (var row = 0; row < 20; row++)
        {
            for (var i = 0; i < width - 2; i++)
                sb.Append(glyphs[Random.Shared.Next(glyphs.Length)]);
            sb.Append('\n');
        }

        var body = new Label
        {
            Text = sb.ToString(),
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        dlg.Add(body);

        // 自己关掉，不用用户操作（也没法操作）
        Application.AddTimeout(TimeSpan.FromMilliseconds(1800), () =>
        {
            try
            {
                dlg.RequestStop();
            }
            catch
            {
                // 已经关了
            }
            return false;
        });

        Application.Run(dlg);
        dlg.Dispose();
    }

    private static Button MkButton(string text, Pos x, int y) => new()
    {
        Text = text,
        X = x,
        Y = y,
        NoPadding = true,
        // NoPadding 的按钮左右各有 1 列边框，所以 +4 是"刚好不换行"，再少就挤两行
        Width = text.GetColumns() + 4,
    };

    // ================= 工具实现 =================

    /// <summary>
    /// 清理下载残留：各版本目录 / libraries 下的 .tmp。
    /// 断点续传没下完的半截文件，删了最多下次重下，不影响已装好的内容。
    /// </summary>
    private void CleanLeftovers()
    {
        var targets = new List<string>();
        var mcDir = Config.MinecraftPath;

        void Scan(string root, bool recursive)
        {
            if (!Directory.Exists(root))
                return;
            try
            {
                targets.AddRange(Directory.EnumerateFiles(root, "*.tmp",
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly));
            }
            catch (Exception ex)
            {
                LauncherLog.Warn($"扫描 {root} 失败: {ex.Message}");
            }
        }

        var versionsDir = Path.Combine(mcDir, "versions");
        if (Directory.Exists(versionsDir))
        {
            try
            {
                foreach (var vd in Directory.EnumerateDirectories(versionsDir))
                    Scan(vd, recursive: true);
            }
            catch { /* 权限问题跳过 */ }
        }
        Scan(Path.Combine(mcDir, "libraries"), recursive: true);
        Scan(Path.Combine(mcDir, "modpacks"), recursive: false);

        if (targets.Count == 0)
        {
            MessageBox.Query(Application.Instance, "清理下载残留",
                "没有找到残留文件(.tmp),不用清理。", "好");
            return;
        }

        long total = 0;
        foreach (var t in targets)
        {
            try { total += new FileInfo(t).Length; }
            catch { /* 忽略 */ }
        }

        var preview = string.Join("\n", targets.Take(8).Select(t => "  " + Path.GetFileName(t)));
        if (targets.Count > 8)
            preview += $"\n  ...还有 {targets.Count - 8} 个";

        var r = MessageBox.Query(Application.Instance, "清理下载残留",
            $"找到 {targets.Count} 个未完成文件,共 {total / 1048576.0:0.0} MB:\n\n{preview}\n\n" +
            "删除后这些文件需要重新下载(已装好的内容不受影响)。",
            "删除", "取消");
        if (r != 0)
            return;

        var ok = 0;
        long freed = 0;
        foreach (var t in targets)
        {
            try
            {
                var size = new FileInfo(t).Length;
                File.Delete(t);
                freed += size;
                ok++;
            }
            catch (Exception ex)
            {
                LauncherLog.Warn($"删除 {t} 失败: {ex.Message}");
            }
        }

        MessageBox.Query(Application.Instance, "清理下载残留",
            $"删掉 {ok}/{targets.Count} 个,释放 {freed / 1048576.0:0.0} MB。", "好");
    }

    /// <summary>列出检测到的所有 Java, 能直观看出来自动选择为什么挑了这一份</summary>
    private void ShowJavaList()
    {
        var list = JavaDetector.Detect();
        if (list.Count == 0)
        {
            MessageBox.Query(Application.Instance, "检查 Java",
                "没有检测到任何 Java。\n\n到[设置 → Java]用[下载 Java]按钮装一个。", "好");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"共 {list.Count} 个:");
        foreach (var j in list)
            sb.AppendLine($"  Java {j.Major}   {j.Version}   {j.Path}");

        sb.AppendLine();
        var auto = JavaDetector.AutoPick();
        sb.AppendLine(auto is null
            ? "自动选择:无可用 Java"
            : $"自动选择:Java {auto.Major}  →  {auto.Path}");

        MessageBox.Query(Application.Instance, "检查 Java", sb.ToString().TrimEnd(), "好");
    }

    // ---------- 通用 ----------

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
            MessageBox.ErrorQuery(Application.Instance, "打不开", $"{path}\n\n{ex.Message}", "好");
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery(Application.Instance, "打不开链接", $"{url}\n\n{ex.Message}", "好");
        }
    }
}
