using System.Collections.ObjectModel;
using LargeHardLiteLauncher.Core;
using Terminal.Gui;

namespace LargeHardLiteLauncher.UI;

/// <summary>
/// 版本管理页的「资源包」和「光影」两块。
/// 结构照 Mod 管理页：单击勾选、顶部按钮批量启用/禁用/删除、双击看详情。
/// 光影页额外判断有没有光影加载器（Iris / OptiFine），没有就在顶部给警告。
/// </summary>
public partial class LaunchPage
{
    /// <summary>资源包 / 光影包的一个条目（文件或文件夹都算）</summary>
    private sealed class PackEntry
    {
        public string Path { get; init; } = "";
        public string FileName { get; init; } = "";
        public string EnabledName { get; init; } = "";
        /// <summary>
        /// 是否启用。**注意**：这个值不是从文件名推的，而是从 options.txt / optionsshaders.txt
        /// 里的配置读出来的（资源包可以被勾选多份，光影只能有一份生效）
        /// </summary>
        public bool Enabled { get; set; }
        public bool IsDir { get; init; }
        public long Size { get; init; }
        public DateTime Modified { get; init; }

        public string Display
        {
            get
            {
                var baseName = EnabledName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? EnabledName[..^4]
                    : EnabledName;
                var size = IsDir ? "文件夹" : FormatSize(Size);
                var state = Enabled ? "使用中" : "";
                return $"{baseName}    [{size}]{state}";
            }
        }
    }

    // 两个页面的勾选集合分开存，来回切不会串
    private readonly HashSet<string> _packSelRes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _packSelShader = new(StringComparer.OrdinalIgnoreCase);
    private string _packKeyword = "";

    /// <summary>
    /// 扫资源包 / 光影目录。只认 .zip 和子文件夹（包里也可能塞着 readme 之类，忽略）。
    /// 这里**不看启用状态**, 启用状态在配置文件里，由调用方回填。
    /// </summary>
    private static List<PackEntry> ScanPacks(string dir)
    {
        var result = new List<PackEntry>();
        if (!Directory.Exists(dir))
            return result;

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir);
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"扫描 {dir} 失败: {ex.Message}");
            return result;
        }

        foreach (var path in entries)
        {
            var fileName = Path.GetFileName(path);
            var isDir = Directory.Exists(path);

            // 旧版把「禁用」实现成改名加 .disabled，这里也认一下（MigrateDisabledNames 会先还原）
            var enabledName = fileName.EndsWith(PackDisabledSuffix, StringComparison.OrdinalIgnoreCase)
                ? fileName[..^PackDisabledSuffix.Length]
                : fileName;

            if (!isDir && !enabledName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                continue; // 不是资源包/光影包

            long size = 0;
            var modified = DateTime.MinValue;
            try
            {
                if (isDir)
                    modified = Directory.GetLastWriteTime(path);
                else
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists)
                        continue;
                    size = fi.Length;
                    modified = fi.LastWriteTime;
                }
            }
            catch
            {
                continue;
            }

            result.Add(new PackEntry
            {
                Path = path,
                FileName = fileName,
                EnabledName = enabledName,
                IsDir = isDir,
                Size = size,
                Modified = modified,
            });
        }

        return result.OrderBy(p => p.EnabledName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private const string PackDisabledSuffix = ".disabled";

    /// <summary>
    /// 这个版本能不能用光影。判据（任一条成立就算支持）：
    /// 版本本身是 OptiFine；mods 里有 Iris / OptiFine / Oculus。
    /// 只有 Forge/Fabric 而没有光影加载器 → 光影不生效，得先提示用户。
    /// </summary>
    private (bool Ok, string Hint) ShaderSupport(MinecraftVersion ver)
    {
        if (ver.MainClass?.Contains("optifine", StringComparison.OrdinalIgnoreCase) == true)
            return (true, "版本自带 OptiFine");

        var loader = GameLauncher.DetectLoader(ver);
        if (loader is null)
            return (false, "该版本为原版(无 Forge/Fabric,也不是 OptiFine)。原版不支持光影,放入 shaderpacks 不会生效");

        try
        {
            var modsDir = VersionSettings.InstalledModsDir(Config, ver);
            if (Directory.Exists(modsDir))
            {
                foreach (var f in Directory.EnumerateFiles(modsDir, "*.jar"))
                {
                    var n = Path.GetFileName(f);
                    var l = n.ToLowerInvariant();
                    if (l.Contains("iris") || l.Contains("optifine") || l.Contains("oculus"))
                        return (true, n);
                }
            }
        }
        catch (Exception ex)
        {
            LauncherLog.Warn($"检查光影加载器失败: {ex.Message}");
        }

        return (false, $"已安装 {loader},但 mods 中未找到 Iris / OptiFine,光影不会生效,请先安装 Iris");
    }

    /// <summary>资源包 / 光影页入口。<paramref name="shader"/> = true 时是光影页</summary>
    private void BuildPackPage(View content, MinecraftVersion ver, bool shader)
    {
        var gameDir = GameDirOf(ver);
        var dir = Path.Combine(gameDir, shader ? "shaderpacks" : "resourcepacks");
        var selected = shader ? _packSelShader : _packSelRes;

        // 旧版把「禁用」实现成改名加 .disabled, 那会让游戏完全看不见这个包（等于卸载）。
        // 进页面时先还原文件名，之后启停全部交给配置文件。
        var healed = PackConfig.MigrateDisabledNames(dir);
        if (healed > 0)
            LauncherLog.Info($"{dir}:已还原 {healed} 个旧版 .disabled 命名文件");

        var entries = ScanPacks(dir);

        // 回填启用状态：资源包是列表（可同时勾多份），光影只能有一份生效
        if (shader)
        {
            var activeShader = PackConfig.ReadActiveShader(gameDir);
            foreach (var p in entries)
                p.Enabled = string.Equals(p.EnabledName, activeShader, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var enabledNames = PackConfig.ReadEnabledPacks(gameDir);
            foreach (var p in entries)
                p.Enabled = enabledNames.Contains(p.EnabledName);
        }
        entries = entries
            .OrderByDescending(p => p.Enabled)
            .ThenBy(p => p.EnabledName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        selected.IntersectWith(entries.Select(p => p.EnabledName));

        // ---- 第 0 行: 状态 / 加载器警告 ----
        var (shaderOk, shaderHint) = shader ? ShaderSupport(ver) : (true, "");
        var activeName = shader ? entries.FirstOrDefault(p => p.Enabled)?.EnabledName : null;
        var statusText = shader
            ? (shaderOk ? $"光影加载器:{shaderHint}" : $"⚠ {shaderHint}")
              + $"    当前:{(activeName is null ? "未启用（原版画质）" : activeName)}"
            : $"{dir}    共 {entries.Count} 个资源包(启用 {entries.Count(p => p.Enabled)} / 未启用 {entries.Count(p => !p.Enabled)})";
        var status = new Label
        {
            Text = statusText,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
        };

        // ---- 第 1 行: 搜索 ----
        var searchLbl = new Label { Text = "搜索", X = 0, Y = 1 };
        var searchField = new TextField
        {
            Text = _packKeyword,
            X = Pos.Right(searchLbl) + 1,
            Y = 1,
            Width = 24,
        };

        // ---- 第 2 行: 按钮 ----
        var selectAllBtn = new Button { Text = "全选", X = 0, Y = 2, NoPadding = true, Width = 12 };
        var enableBtn = new Button { Text = "启用", X = Pos.Right(selectAllBtn) + 2, Y = 2, NoPadding = true, Width = 12 };
        var disableBtn = new Button { Text = "禁用", X = Pos.Right(enableBtn) + 2, Y = 2, NoPadding = true, Width = 12 };
        var deleteBtn = new Button { Text = "删除", X = Pos.Right(disableBtn) + 2, Y = 2, NoPadding = true, Width = 12 };
        var openBtn = new Button { Text = "打开目录", X = Pos.Right(deleteBtn) + 2, Y = 2, NoPadding = true, Width = 12 };
        var refreshBtn = new Button { Text = "刷新", X = Pos.Right(openBtn) + 2, Y = 2, NoPadding = true, Width = 12 };

        // ---- 列表 ----
        var shown = new List<PackEntry>();
        var curTexts = new List<string>();
        var list = new ListView<string>
        {
            X = 0,
            Y = 4,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        void Rebuild()
        {
            var q = (_packKeyword ?? "").Trim();
            shown.Clear();
            foreach (var p in entries)
            {
                if (q.Length > 0 && !p.EnabledName.Contains(q, StringComparison.OrdinalIgnoreCase))
                    continue;
                shown.Add(p);
            }
            curTexts = shown
                .Select(p => $"{(selected.Contains(p.EnabledName) ? "[✓]" : "[ ]")} {p.Display}")
                .ToList();
            if (curTexts.Count == 0)
                curTexts.Add(entries.Count == 0
                    ? (shader
                        ? "(尚未安装光影包,可在[下载 → 光影]中搜索,需要 Iris 或 OptiFine)"
                        : "(尚未安装资源包,可在[下载 → 资源包]中搜索)")
                    : "(没有匹配的项)");

            list.SetSource(new ObservableCollection<string>(curTexts));
            SyncButtons();
        }

        void SyncButtons()
        {
            var n = selected.Count;
            enableBtn.Enabled = n > 0;
            disableBtn.Enabled = n > 0;
            deleteBtn.Enabled = n > 0;
            selectAllBtn.Text = n > 0 && n >= shown.Count ? "清空" : "全选";
        }

        void Toggle(PackEntry p)
        {
            if (!selected.Remove(p.EnabledName))
                selected.Add(p.EnabledName);
            var keep = list.SelectedItem?.ToString();
            Rebuild();
            if (keep is not null && curTexts.Contains(keep))
                list.SelectedItem = keep;
        }

        /// <summary>
        /// 启停。**改配置文件，不碰文件名**：
        /// 资源包写 options.txt 的 resourcePacks 列表，光影写 optionsshaders.txt / iris.properties。
        /// </summary>
        bool SetEnabled(PackEntry p, bool enable, out string message)
        {
            message = "";
            if (p.Enabled == enable)
                return true;

            var ok = shader
                ? PackConfig.SetActiveShader(gameDir, enable ? p.EnabledName : null, out message)
                : PackConfig.SetPackEnabled(gameDir, p.EnabledName, enable, out message);
            if (!ok)
                return false;

            // 光影是独占的：开了这一份，其它在本页状态里一并标成关闭
            if (shader && enable)
                foreach (var other in entries)
                    other.Enabled = ReferenceEquals(other, p);

            return true;
        }

        void ApplyBulk(bool enable)
        {
            var targets = entries.Where(p => selected.Contains(p.EnabledName) && p.Enabled != enable).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Query(Application.Instance, enable ? "批量启用" : "批量禁用",
                    enable ? "勾选的项都已经启用了。" : "勾选的项都没有启用。", "好");
                return;
            }

            // 光影同时只能生效一份，勾多了只认第一个（否则最后一个会覆盖前面的，用户看不明白）
            if (shader && enable && targets.Count > 1)
            {
                MessageBox.Query(Application.Instance, "光影只能开一份",
                    $"光影同时只能生效一份,本次仅启用:\n  {targets[0].EnabledName}\n\n" +
                    $"其余 {targets.Count - 1} 个勾选已忽略。", "好");
                targets = targets.Take(1).ToList();
            }

            var ok = 0;
            string? firstErr = null;
            foreach (var p in targets)
            {
                if (SetEnabled(p, enable, out var msg))
                    ok++;
                else
                    firstErr ??= $"{p.EnabledName}: {msg}";
            }
            if (firstErr is not null)
                MessageBox.ErrorQuery(Application.Instance, "部分失败",
                    $"成功 {ok} 个,失败 {targets.Count - ok} 个。\n\n第一个失败原因:\n{firstErr}", "好");
            selected.Clear();
            // 重建整个页面（配置改过了，状态得重新读）
            content.RemoveAll();
            BuildPackPage(content, ver, shader);
        }

        selectAllBtn.Accepting += (_, _) =>
        {
            if (selected.Count > 0)
                selected.Clear();
            else
                foreach (var p in shown)
                    selected.Add(p.EnabledName);
            Rebuild();
        };
        enableBtn.Accepting += (_, _) => ApplyBulk(true);
        disableBtn.Accepting += (_, _) => ApplyBulk(false);
        deleteBtn.Accepting += (_, _) =>
        {
            var targets = entries.Where(p => selected.Contains(p.EnabledName)).ToList();
            if (targets.Count == 0)
                return;
            var preview = string.Join("\n", targets.Take(6).Select(p => "  " + p.FileName));
            if (targets.Count > 6)
                preview += $"\n  ...还有 {targets.Count - 6} 个";
            var kind = shader ? "光影" : "资源包";
            var r = MessageBox.Query(Application.Instance, $"删除{kind}",
                $"确定删除这 {targets.Count} 个{kind}?\n\n{preview}\n\n(将直接删除磁盘文件,不进入回收站)", "删除", "取消");
            if (r != 0)
                return;

            var ok = 0;
            string? firstErr = null;
            foreach (var p in targets)
            {
                try
                {
                    if (p.IsDir)
                        Directory.Delete(p.Path, recursive: true);
                    else
                        File.Delete(p.Path);
                    ok++;
                }
                catch (Exception ex)
                {
                    firstErr ??= $"{p.FileName}: {ex.Message}";
                }
            }
            if (firstErr is not null)
                MessageBox.ErrorQuery(Application.Instance, "部分删除失败",
                    $"成功 {ok} 个,失败 {targets.Count - ok} 个。\n\n第一个失败原因:\n{firstErr}", "好");
            selected.Clear();
            content.RemoveAll();
            BuildPackPage(content, ver, shader);
        };
        openBtn.Accepting += (_, _) => OpenFolder(dir);
        refreshBtn.Accepting += (_, _) =>
        {
            content.RemoveAll();
            BuildPackPage(content, ver, shader);
        };

        // 单击 = 勾选（和 Mod 页一致；ListView 点击时已更新 SelectedItem）
        list.MouseEvent += (_, e) =>
        {
            if (!e.IsSingleClicked)
                return;
            var idx = list.SelectedItem is { } s ? curTexts.IndexOf(s) : -1;
            if (idx < 0 || idx >= shown.Count)
                return;
            Toggle(shown[idx]);
        };

        // 回车/双击 = 详情
        list.Accepted += (_, _) =>
        {
            if (list.SelectedItem is not { } sel)
                return;
            var i = curTexts.IndexOf(sel);
            if (i < 0 || i >= shown.Count)
                return;
            var p = shown[i];
            var stateText = shader
                ? (p.Enabled ? "使用中" : "未使用(光影同时只有一份生效)")
                : (p.Enabled ? "已启用" : "未启用(游戏里能看到,只是没勾上)");
            MessageBox.Query(Application.Instance, p.EnabledName,
                $"文件:{p.FileName}\n" +
                $"状态:{stateText}\n" +
                $"类型:{(p.IsDir ? "文件夹（目录形式的包）" : "zip 压缩包")}\n" +
                $"大小:{(p.IsDir ? "—" : FormatSize(p.Size))}\n" +
                $"修改:{p.Modified:yyyy-MM-dd HH:mm}\n\n" +
                $"路径:{p.Path}\n\n" +
                $"启用状态记录在 {(shader ? "optionsshaders.txt / iris.properties" : "options.txt 的 resourcePacks")}", "好");
        };

        searchField.TextChanged += (_, _) =>
        {
            _packKeyword = searchField.Text ?? "";
            Rebuild();
        };

        content.Add(status, searchLbl, searchField,
            selectAllBtn, enableBtn, disableBtn, deleteBtn, openBtn, refreshBtn, list);
        Rebuild();
    }
}
