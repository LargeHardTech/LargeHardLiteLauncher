using System.Collections.ObjectModel;
using LargeHardLiteLauncher.Core;
using Terminal.Gui;

namespace LargeHardLiteLauncher.UI;

/// <summary>版本管理页的 Mod 管理 + 补全版本两块（学 PCL 的 PageInstanceMod / 补全版本）</summary>
public partial class LaunchPage
{
    // 页内状态：切换导航时重建 UI，但数据留着，免得每次回来都重扫
    private List<ModInfo> _mods = new();
    private string _modKeyword = "";
    private int _modFilter; // 0=全部 1=已启用 2=已禁用
    private RepairPlan? _repairPlan;
    /// <summary>勾选的 Mod（存 EnabledName，启用/禁用共用同一个 key, 和 PCL 一致）</summary>
    private readonly HashSet<string> _modSelected = new(StringComparer.OrdinalIgnoreCase);

    // ================= Mod 管理 =================

    private void BuildModPage(View content, MinecraftVersion ver, Action render)
    {
        var modsDir = VersionSettings.InstalledModsDir(Config, ver);

        // ---- 第 0 行: 搜索 + 筛选 ----
        var searchLbl = new Label { Text = "搜索", X = 0, Y = 0 };
        var searchField = new TextField
        {
            Text = _modKeyword,
            X = Pos.Right(searchLbl) + 1,
            Y = 0,
            Width = 24,
        };
        var filterNames = new[] { "全部", "已启用", "已禁用" };
        var filterDrop = new DropDownList
        {
            X = Pos.Right(searchField) + 1,
            Y = 0,
            Width = 10,
            Source = new ListWrapper<string>(new ObservableCollection<string>(filterNames)),
        };
        filterDrop.Text = filterNames[Math.Clamp(_modFilter, 0, 2)];

        // ---- 列表 ----
        var shown = new List<ModInfo>();
        var curTexts = new List<string>(); // 和 shown 一一对应（占位行时为 1 条空数据）
        var list = new ListView<string>
        {
            X = 0,
            Y = 3,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        void Rebuild()
        {
            var q = (_modKeyword ?? "").Trim();
            shown.Clear();
            foreach (var m in _mods)
            {
                if (_modFilter == 1 && !m.Enabled) continue;
                if (_modFilter == 2 && m.Enabled) continue;
                if (q.Length > 0
                    && !m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !m.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !m.ModId.Contains(q, StringComparison.OrdinalIgnoreCase))
                    continue;
                shown.Add(m);
            }

            // 勾选状态不再显示在列表里（勾选框就是勾选），启用/禁用用后缀文字区分
            curTexts = shown
                .Select(m => $"{(_modSelected.Contains(m.EnabledName) ? "[✓]" : "[ ]")} {m.Display}")
                .ToList();
            if (curTexts.Count == 0)
                curTexts.Add(_mods.Count == 0
                    ? "(该版本尚未安装 Mod,可在[下载 → Mod]中搜索)"
                    : "(没有匹配的 Mod)");

            // 必须整体替换 Source: TG 2.5 逐项 Add 是 O(n²) 且每次触发全屏重绘
            list.SetSource(new ObservableCollection<string>(curTexts));
            SyncButtons();
        }

        void Reload()
        {
            if (TaskManager.Slots().Any(t => t is { IsDone: false } && t.Name == "读取 Mod 列表"))
                return;
            TaskManager.Submit("读取 Mod 列表", async (task, ct) =>
            {
                task.LogLine($"扫描 {modsDir}");
                var got = await Task.Run(() => ModScanner.Scan(modsDir), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return;
                task.LogLine($"共 {got.Count} 个文件(启用 {got.Count(m => m.Enabled)} / 禁用 {got.Count(m => !m.Enabled)})");
                Application.Invoke(() =>
                {
                    _mods = got;
                    // 已经不存在的 mod 从勾选里剔掉
                    _modSelected.IntersectWith(got.Select(m => m.EnabledName));
                    Rebuild();
                });
            });
        }

        // ---- 按钮行 ----
        // 宽度按文字算：NoPadding 的 Button 左右各 1 列边框, 不换行的最小值 = 文字列数 + 4
        // （全写死 12 的话 7 个按钮要 96 列, 80 宽的终端直接溢出被裁）
        Button? prevBtn = null;
        Button MkBtn(string text)
        {
            var b = new Button
            {
                Text = text,
                X = prevBtn is null ? 0 : Pos.Right(prevBtn) + 2,
                Y = 1,
                NoPadding = true,
                Width = Math.Max(8, text.GetColumns() + 4),
            };
            prevBtn = b;
            return b;
        }

        var selectAllBtn = MkBtn("全选");
        var enableBtn = MkBtn("启用");
        var disableBtn = MkBtn("禁用");
        var deleteBtn = MkBtn("删除");
        var openBtn = MkBtn("打开目录");
        var refreshBtn = MkBtn("刷新");
        var updateBtn = MkBtn("检查更新");

        ModInfo? Current()
        {
            if (list.SelectedItem is not { } sel)
                return null;
            var idx = curTexts.IndexOf(sel);
            if (idx < 0 || idx >= shown.Count)
                return null; // 占位行
            return shown[idx];
        }

        /// <summary>按当前勾选状态刷新按钮可用性和"全选/清空"文字</summary>
        void SyncButtons()
        {
            var n = _modSelected.Count;
            enableBtn.Enabled = n > 0;
            disableBtn.Enabled = n > 0;
            deleteBtn.Enabled = n > 0;
            selectAllBtn.Text = n > 0 && n >= shown.Count ? "清空" : "全选";
        }

        /// <summary>切换一个 mod 的勾选（单击列表项触发）</summary>
        void ToggleSelect(ModInfo mod)
        {
            if (!_modSelected.Remove(mod.EnabledName))
                _modSelected.Add(mod.EnabledName);
            // 只重画列表文本，不重新扫描
            var sel = list.SelectedItem?.ToString();
            Rebuild();
            if (sel is not null && curTexts.Contains(sel))
                list.SelectedItem = sel;
        }

        /// <summary>批量启用/禁用</summary>
        void ApplyBulk(bool enable)
        {
            var targets = _mods
                .Where(m => _modSelected.Contains(m.EnabledName) && m.Enabled != enable)
                .ToList();
            if (targets.Count == 0)
            {
                MessageBox.Query(Application.Instance, enable ? "批量启用" : "批量禁用",
                    enable ? "勾选的 Mod 都已经启用了。" : "勾选的 Mod 都已经禁用了。", "好");
                return;
            }

            var ok = 0;
            string? firstErr = null;
            foreach (var m in targets)
            {
                if (ModScanner.SetEnabled(m, enable, out var msg))
                    ok++;
                else
                    firstErr ??= $"{m.FileName}: {msg}";
            }

            if (firstErr is not null)
                MessageBox.ErrorQuery(Application.Instance, "部分失败",
                    $"成功 {ok} 个,失败 {targets.Count - ok} 个。\n\n第一个失败原因:\n{firstErr}", "好");

            _modSelected.Clear();
            ModScanner.ClearCache();
            Reload();
        }

        void ShowDetail(ModInfo m)
        {
            var dlg = new Dialog
            {
                Title = "Mod 详情",
                Width = Math.Min(84, Math.Max(50, m.Name.Length + 20)),
                Height = 16,
                BorderStyle = LineStyle.Single,
            };
            var lines = new[]
            {
                $"名称    {m.Name}",
                $"ID      {(m.ModId.Length > 0 ? m.ModId : "(未知)")}",
                $"版本    {(m.Version.Length > 0 ? m.Version : "(未知)")}",
                $"加载器  {m.Loader}",
                $"状态    {(m.Enabled ? "已启用" : "已禁用")}",
                $"文件    {m.FileName}",
                $"大小    {FormatSize(m.Size)}",
                $"修改    {m.Modified:yyyy-MM-dd HH:mm}",
                $"描述    {(m.Description.Length > 0 ? m.Description : "(无)")}",
            };
            var y = 1;
            foreach (var line in lines)
            {
                dlg.Add(new Label { Text = line, X = 1, Y = y, Width = Dim.Fill(2) });
                y++;
            }
            var close = new Button { Text = "关闭", X = 1, Y = y + 1, IsDefault = true };
            close.Accepting += (_, _) => dlg.RequestStop();
            dlg.Add(close);
            Application.Run(dlg);
            dlg.Dispose();
        }

        // ---- 事件 ----
        selectAllBtn.Accepting += (_, _) =>
        {
            if (_modSelected.Count > 0)
                _modSelected.Clear();           // 已有勾选 → 一键清空
            else
            {
                foreach (var m in shown)
                    _modSelected.Add(m.EnabledName);
            }
            Rebuild();
        };
        enableBtn.Accepting += (_, _) => ApplyBulk(true);
        disableBtn.Accepting += (_, _) => ApplyBulk(false);
        deleteBtn.Accepting += (_, _) =>
        {
            var targets = _mods.Where(m => _modSelected.Contains(m.EnabledName)).ToList();
            if (targets.Count == 0)
                return;
            var preview = string.Join("\n", targets.Take(6).Select(m => "  " + m.FileName));
            if (targets.Count > 6)
                preview += $"\n  ...还有 {targets.Count - 6} 个";
            var r = MessageBox.Query(Application.Instance, "删除 Mod",
                $"确定删除这 {targets.Count} 个文件?\n\n{preview}\n\n(将直接删除磁盘文件,不进入回收站)", "删除", "取消");
            if (r != 0)
                return;

            var ok = 0;
            string? firstErr = null;
            foreach (var m in targets)
            {
                if (ModScanner.Delete(m, out var msg))
                    ok++;
                else
                    firstErr ??= $"{m.FileName}: {msg}";
            }
            if (firstErr is not null)
                MessageBox.ErrorQuery(Application.Instance, "部分删除失败",
                    $"成功 {ok} 个,失败 {targets.Count - ok} 个。\n\n第一个失败原因:\n{firstErr}", "好");
            _modSelected.Clear();
            Reload();
        };
        openBtn.Accepting += (_, _) => OpenFolder(modsDir);
        refreshBtn.Accepting += (_, _) =>
        {
            ModScanner.ClearCache();
            Reload();
        };
        updateBtn.Accepting += (_, _) => CheckModUpdates(ver, modsDir, render);

        // 单击 = 切换这一项的勾选（学 PCL：点整行就能勾）
        // 依赖时序: ListView 处理点击时会把该行设为 SelectedItem，handler 里读到的已经是新行
        list.MouseEvent += (_, e) =>
        {
            if (!e.IsSingleClicked)
                return;
            var idx = list.SelectedItem is { } s ? curTexts.IndexOf(s) : -1;
            if (idx < 0 || idx >= shown.Count)
                return;
            ToggleSelect(shown[idx]);
        };

        // 回车/双击 = 看详情
        list.Accepted += (_, _) =>
        {
            if (Current() is { } m)
                ShowDetail(m);
        };

        searchField.TextChanged += (_, _) =>
        {
            _modKeyword = searchField.Text ?? "";
            Rebuild();
        };
        filterDrop.ValueChanged += (_, _) =>
        {
            var i = Array.IndexOf(filterNames, filterDrop.Text ?? "");
            _modFilter = i < 0 ? 0 : i;
            Rebuild();
        };

        content.Add(searchLbl, searchField, filterDrop,
            selectAllBtn, enableBtn, disableBtn, deleteBtn, openBtn, refreshBtn, updateBtn, list);

        // 首次进来（或数据被别处清了）就拉一次
        Rebuild();
        if (_mods.Count == 0)
            Reload();
    }

    /// <summary>文件大小显示</summary>
    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1048576 => $"{bytes / 1048576.0:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };

    // ================= Mod 更新检查 =================

    /// <summary>
    /// 检查更新：后台算哈希 + 查 Modrinth，结果弹出更新列表。
    /// 加载器/MC 版本都从当前版本推，保证只拿到「这个版本能用」的新文件。
    /// </summary>
    private void CheckModUpdates(MinecraftVersion ver, string modsDir, Action render)
    {
        if (TaskManager.Slots().Any(t => t is { IsDone: false } && t.Name == "检查 Mod 更新"))
            return;

        TaskManager.Submit("检查 Mod 更新", async (task, ct) =>
        {
            var all = VersionScanner.Scan(Config.MinecraftPath);
            var baseMc = VersionHelper.ResolveBaseVersion(ver, all);
            var loader = GameLauncher.DetectLoader(ver);
            task.LogLine($"版本 {ver.Id}:MC {baseMc},加载器 {loader ?? "原版"}");

            var mods = await Task.Run(() => ModScanner.Scan(modsDir), ct).ConfigureAwait(false);
            task.LogLine($"已装 {mods.Count} 个文件");

            var updates = await ModUpdateChecker
                .CheckAsync(mods, baseMc, loader, task, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
                return;

            Application.Invoke(() => ShowUpdateDialog(updates, modsDir, render));
        });
    }

    /// <summary>更新结果对话框：勾选 → 更新选中。没有更新就给一句提示。</summary>
    private void ShowUpdateDialog(List<ModUpdateInfo> updates, string modsDir, Action render)
    {
        if (updates.Count == 0)
        {
            MessageBox.Query(Application.Instance, "检查更新",
                "所有能识别的 Mod 都是最新的。\n\n" +
                "(仅能查询 Modrinth 上的 Mod,CurseForge 上的项目与本地 Mod 不在此列)", "好");
            return;
        }

        var dlg = new Dialog
        {
            Title = $"Mod 更新:{updates.Count} 个可更新",
            Width = Dim.Fill(-1),
            Height = Dim.Fill(-1),
        };

        var hint = new Label
        {
            Text = "单击行可切换勾选。更新会下载新文件并删除旧文件,不影响存档",
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
        };

        var chosen = new HashSet<int>(Enumerable.Range(0, updates.Count)); // 默认全选
        var texts = new List<string>();
        var list = new ListView<string>
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(5),
        };

        void Rebuild()
        {
            texts = updates
                .Select((u, i) =>
                {
                    var mark = chosen.Contains(i) ? "[✓]" : "[ ]";
                    var tail = u.SameFileName ? "  (同名,原地替换)" : "";
                    return $"{mark} {u.Display}{tail}";
                })
                .ToList();
            list.SetSource(new ObservableCollection<string>(texts));
        }
        Rebuild();

        list.MouseEvent += (_, e) =>
        {
            if (!e.IsSingleClicked)
                return;
            var idx = list.SelectedItem is { } sel ? texts.IndexOf(sel) : -1;
            if (idx < 0 || idx >= updates.Count)
                return;
            if (!chosen.Remove(idx))
                chosen.Add(idx);
            Rebuild();
            // 列表顺序不变, 直接按索引还原选中行（比按文本找更稳）
            if (idx < texts.Count)
                list.SelectedItem = texts[idx];
        };

        var allBtn = new Button { Text = "全选", X = 1, Y = Pos.AnchorEnd(2), NoPadding = true, Width = 8 };
        // IsDefault 的按钮会多占一行，
        // 文字就被挤到第二行去了, 这里显式钉成 2 行，宽度也按文字算再留点余量
        var doBtn = new Button
        {
            Text = "更新选中",
            X = Pos.Right(allBtn) + 2,
            Y = Pos.AnchorEnd(2),
            NoPadding = true,
            Height = 2,
            Width = Math.Max(14, "更新选中".GetColumns() + 6),
            IsDefault = true,
        };
        var cancel = new Button
        {
            Text = "取消",
            X = Pos.Right(doBtn) + 2,
            Y = Pos.AnchorEnd(2),
            NoPadding = true,
            Width = 8,
        };

        allBtn.Accepting += (_, _) =>
        {
            if (chosen.Count > 0)
                chosen.Clear();
            else
                for (var i = 0; i < updates.Count; i++)
                    chosen.Add(i);
            allBtn.Text = chosen.Count > 0 ? "清空" : "全选";
            Rebuild();
        };
        cancel.Accepting += (_, _) => dlg.RequestStop();
        doBtn.Accepting += (_, _) =>
        {
            var picked = updates.Where((_, i) => chosen.Contains(i)).ToList();
            if (picked.Count == 0)
                return;
            dlg.RequestStop();
            RunModUpdates(picked, modsDir, render);
        };

        dlg.Add(hint, list, allBtn, doBtn, cancel);
        list.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();
    }

    /// <summary>真正执行更新（走任务栏，能看进度）</summary>
    private void RunModUpdates(List<ModUpdateInfo> picked, string modsDir, Action render)
    {
        TaskManager.Submit($"更新 {picked.Count} 个 Mod", async (task, ct) =>
        {
            var (ok, fail) = await ModUpdateChecker.ApplyAsync(picked, task, ct).ConfigureAwait(false);
            task.LogLine($"完成:成功 {ok},失败 {fail}");
            if (fail > 0 && !ct.IsCancellationRequested)
            {
                Application.Invoke(() => MessageBox.ErrorQuery(Application.Instance, "更新 Mod",
                    $"成功 {ok} 个,失败 {fail} 个。\n\n失败原因见任务日志。", "好"));
            }

            // 文件换过了，缓存必须清，否则列表还显示老文件名
            ModScanner.ClearCache();
            var fresh = await Task.Run(() => ModScanner.Scan(modsDir), ct).ConfigureAwait(false);
            Application.Invoke(() =>
            {
                _mods = fresh;
                _modSelected.Clear();
                render(); // 整块重建最省事：列表/按钮/筛选全都跟着刷
            });
        });
    }

    // ================= 补全版本 =================

    private void BuildRepairPage(View content, MinecraftVersion ver)
    {
        var summary = new Label
        {
            Text = _repairPlan is null ? "还没检查。" : _repairPlan.Summary,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
        };

        var list = new ListView<string>
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(6),
        };
        list.SetSource(new ObservableCollection<string>());

        void ShowPlan()
        {
            summary.Text = _repairPlan is null ? "还没检查。" : _repairPlan.Summary;
            var rows = new List<string>();
            if (_repairPlan is not null)
            {
                if (!_repairPlan.HasProblems)
                    rows.Add($"{_repairPlan.Checked} 项文件全部完整,无需补全。");
                else
                {
                    rows.AddRange(_repairPlan.Samples);
                    if (_repairPlan.Samples.Count < _repairPlan.TotalMissing)
                        rows.Add($"...还有 {_repairPlan.TotalMissing - _repairPlan.Samples.Count} 项");
                }
            }
            list.SetSource(new ObservableCollection<string>(rows));
        }

        var checkBtn = new Button { Text = "检查文件", X = 0, Y = Pos.AnchorEnd(4), NoPadding = true };
        var fixBtn = new Button { Text = "补全缺失", X = Pos.Right(checkBtn) + 3, Y = Pos.AnchorEnd(4), NoPadding = true };
        var deepLbl = new Label
        {
            Text = "(将逐项核对客户端 jar,库,natives 与资源文件)",
            X = Pos.Right(fixBtn) + 3,
            Y = Pos.AnchorEnd(4),
        };

        checkBtn.Accepting += (_, _) =>
        {
            if (TaskManager.Slots().Any(t => t is { IsDone: false } && t.Name == "检查版本文件"))
                return;
            summary.Text = "检查中...";
            TaskManager.Submit("检查版本文件", async (task, ct) =>
            {
                task.LogLine($"核对 {ver.Id} 的文件...");
                var plan = await Task.Run(() => VersionChecker.Build(Config, ver), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return;
                task.LogLine($"检查 {plan.Checked} 项 → {plan.Summary}");
                Application.Invoke(() =>
                {
                    _repairPlan = plan;
                    ShowPlan();
                });
            });
        };

        fixBtn.Accepting += (_, _) =>
        {
            if (_repairPlan is null)
            {
                MessageBox.Query(Application.Instance, "补全版本", "先点[检查文件]看看缺什么。", "好");
                return;
            }
            if (!_repairPlan.HasProblems)
            {
                MessageBox.Query(Application.Instance, "补全版本", "文件都在,不用补。", "好");
                return;
            }
            var plan = _repairPlan;
            TaskManager.Submit($"补全 {ver.Id}", async (task, ct) =>
            {
                try
                {
                    task.LogLine($"补全 {ver.Id}: {plan.Summary}");
                    GameDownloader.Configure(Config.IgnoreSslErrors);
                    // quickCheck: 只比大小不算哈希, 3000+ 个资源文件全算哈希要好几分钟
                    await GameDownloader.DownloadAsync(plan.Files, task, ct, quickCheck: true)
                        .ConfigureAwait(false);
                    task.LogLine("补全完成,重新核对...");
                    var again = await Task.Run(() => VersionChecker.Build(Config, ver), ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                        return;
                    task.LogLine($"重新核对: {again.Summary}");
                    Application.Invoke(() =>
                    {
                        _repairPlan = again;
                        ShowPlan();
                        MessageBox.Query(Application.Instance, "补全版本",
                            again.HasProblems
                                ? $"补全完成,仍有 {again.TotalMissing} 项缺失(部分文件可能源站未提供)。\n详情见任务日志。"
                                : "补全完成,文件都齐了。",
                            "好");
                    });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    task.LogLine("已取消");
                    throw;
                }
                catch (Exception ex)
                {
                    LauncherLog.Error($"补全 {ver.Id} 失败: {ex}");
                    foreach (var line in Net.Describe(ex).Split('\n'))
                        task.LogLine(line.TrimEnd());
                    Application.Invoke(() => MessageBox.ErrorQuery(Application.Instance,
                        "补全失败", Net.Brief(ex), "好"));
                    throw;
                }
            });
        };

        content.Add(summary, list, checkBtn, fixBtn, deepLbl);
        ShowPlan();
    }
}
