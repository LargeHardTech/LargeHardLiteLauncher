using System.Collections.ObjectModel;
using System.Security.Cryptography;
using LargeHardLiteLauncher.Core;
using Terminal.Gui;

namespace LargeHardLiteLauncher.UI;

/// <summary>
/// 下载页的社区资源部分 (Modrinth / CurseForge)。
/// 拆成 partial 文件, 免得 DownloadPage.cs 越来越长。
/// </summary>
public partial class DownloadPage
{
    /// <summary>当前搜索结果 (与 _resultItems 一一对应)</summary>
    private List<RemoteProject> _communityHits = new();

    /// <summary>每次搜索取多少条 (Modrinth 上限 100, CurseForge 上限 50)</summary>
    private const int SearchPageSize = 50;

    /// <summary>
    /// 已提交但尚未开始执行的搜索签名。同签名不重复提交:
    /// ① TG 的 DropDownList 换过 Source 后 (排序下拉每次切分类都换) 设 Text 会补发一次 ValueChanged;
    /// ② 快速切走再切回同一筛选时前一个任务还没跑完, 签名相同结果就相同, 跳过不亏。
    /// 注意是**签名**去重不是同名去重: 同名不同参数的搜索必须放行 (老代码刻意避开的坑)。
    /// </summary>
    private string _pendingSearchSig = "";

    /// <summary>
    /// 搜索代次。快速切换分类时会有多个搜索并发, 先发起的可能后返回,
    /// 不校验代次的话旧结果会把新分类的列表覆盖掉。
    /// </summary>
    private int _searchEpoch;

    /// <summary>当前分类对应的资源类型 (0=原版 返回 null)</summary>
    private AddonType? CurrentAddonType() => _categoryIndex switch
    {
        1 => AddonType.Mod,
        2 => AddonType.Modpack,
        3 => AddonType.ResourcePack,
        4 => AddonType.Shader,
        5 => AddonType.DataPack,
        _ => null,
    };

    /// <summary>扫盘缓存: 版本扫描是纯 IO, 搜索/详情页/安装各调一次就白扫三遍</summary>
    private static List<MinecraftVersion>? _scanCache;
    private static DateTime _scanAt;
    private static string _scanDir = "";

    /// <summary>版本表 (3 秒内复用; 安装完成等关键点用 InvalidateVersionCache 立刻作废)</summary>
    private List<MinecraftVersion> AllVersions()
    {
        var dir = Config.MinecraftPath;
        if (_scanCache is not null && _scanDir == dir && (DateTime.UtcNow - _scanAt).TotalSeconds < 3)
            return _scanCache;

        _scanCache = VersionScanner.Scan(dir);
        _scanAt = DateTime.UtcNow;
        _scanDir = dir;
        return _scanCache;
    }

    /// <summary>作废版本缓存 (安装完/删版本/切文件夹后调用)。只碰静态缓存, 所以是静态方法</summary>
    internal static void InvalidateVersionCache()
    {
        _scanCache = null;
        _scanAt = default;
    }

    /// <summary>目标实例: 用启动页当前选中的版本 (装了 mod 要放进它的 mods)</summary>
    private MinecraftVersion? TargetInstance()
    {
        var all = AllVersions();
        return all.FirstOrDefault(v => v.Id == Config.SelectedVersion) ?? all.FirstOrDefault();
    }

    /// <summary>实例的基础 MC 版本 (Modrinth/CF 认的那个 "1.20.1", 不是目录名 "1.20.1-forge-test")</summary>
    private string BaseVersionOf(MinecraftVersion inst) =>
        VersionHelper.ResolveBaseVersion(inst, AllVersions());

    /// <summary>
    /// 提交一次社区搜索任务（任务栏可见，带日志）。搜索会按目标版本的 MC 版本和加载器过滤：
    /// Modrinth 免 key 必搜，填了 CurseForge key 则合并两边结果。
    /// 同签名不重复提交：TG 对换过 Source 的下拉设 Text 会补发 ValueChanged，
    /// 排序下拉每次切分类都换 Source，不拦会重复提交同一组参数。force=true 供刷新按钮使用。
    /// </summary>
    private void StartCommunitySearch(bool force = false)
    {
        if (CurrentAddonType() is null)
            return;

        var sig = $"{_category}|{_searchField.Text}|{_verField.Text}|{_loaderDrop.Text}|{_catDrop.Text}|{_sortDrop.Text}";
        if (!force && sig == _pendingSearchSig)
            return;
        _pendingSearchSig = sig;

        TaskManager.Submit($"{_category} 搜索", SearchCommunityAsync);
    }

    private async Task SearchCommunityAsync(TaskItem task, CancellationToken ct)
    {
        var mySig = _pendingSearchSig;
        try
        {
            await SearchCommunityCoreAsync(task, ct).ConfigureAwait(false);
        }
        finally
        {
            // 清空必须等到任务**结束**, 不能在开头清,
            // 否则第一次任务刚开跑就把签名清了, 紧随其后的重复触发照样过。
            // 只有在签名还是自己那份时才清 (被新搜索取代时保留新的)。
            if (_pendingSearchSig == mySig)
                _pendingSearchSig = "";
        }
    }

    private async Task SearchCommunityCoreAsync(TaskItem task, CancellationToken ct)
    {
        if (CurrentAddonType() is not { } type)
            return;

        var epoch = ++_searchEpoch; // 本次搜索的编号

        CurseForgeApi.ApiKey = Config.CurseForgeApiKey; // 每次搜索读最新 key

        var inst = TargetInstance();
        var rawKeyword = _searchField.Text?.Trim() ?? "";

        // 版本: 输入框里填了就用它; 没填则 mod/资源包用当前实例的**基础 MC 版本**,
        // 整合包不绑版本。 必须是基础版本 (1.20.1) 而不是目录名 (1.20.1-forge-test),
        // 否则 Modrinth 按 versions:1.20.1-forge-test 过滤 → 永远 0 条。
        var manualVersion = _verField.Text?.Trim() ?? "";
        var filterVersion = type != AddonType.Modpack;
        var gameVersion = manualVersion.Length > 0
            ? manualVersion
            : filterVersion ? (inst is null ? null : BaseVersionOf(inst)) : null;
        var instLoader = inst is not null ? GameLauncher.DetectLoader(inst) : null;
        // 加载器下拉: 第 0 项"自动"跟当前实例, 第 1 项"不限"= 不过滤
        var loaderIdx = Array.IndexOf(LoaderOptions, _loaderDrop.Text ?? "");
        var loader = loaderIdx switch
        {
            1 => null,          // 不限
            2 => "fabric",
            3 => "forge",
            4 => "neoforge",
            5 => "quilt",
            _ => instLoader,    // 自动
        };
        var tag = CurrentCategoryTag();
        var sort = CurrentCommunitySort();
        // 中文关键词 → 常用词表转英文 (PCL2 有 SQLite 大词表, 我们先放一份高频兜底)
        var (keyword, zhNote) = TranslateChinese(rawKeyword);

        var target = type == AddonType.Modpack
            ? "将创建独立实例"
            : inst is null
                ? "未找到已安装版本"
                : $"{inst.Id}" + (gameVersion is not null && gameVersion != inst.Id ? $" (MC {gameVersion})" : "")
                  + (loader is null ? " (无加载器)" : " / " + loader);

        task.LogLine($"来源: Modrinth{(CurseForgeApi.Available ? " + CurseForge" : "")}");
        task.LogLine($"关键词: {(rawKeyword.Length == 0 ? "(空, 按下载量)" : rawKeyword)}");
        if (zhNote is not null)
            task.LogLine(zhNote);
        task.LogLine($"目标: {target}");
        task.LogLine($"加载器: {_loaderDrop.Text ?? "自动"}{(loader is null ? " (不限)" : " → " + loader)}");
        if (tag is not null)
            task.LogLine($"分类: {_catDrop.Text}");
        task.LogLine($"排序: {_sortDrop.Text ?? "默认"}");
        task.Report(0.15);
        // 立刻清空旧结果: 否则切分类后列表还显示上一个分类的内容, 直到新结果回来
        _communityHits = new List<RemoteProject>();
        // 这整段跑在 Task.Run 的线程池线程上, 所有 UI 操作都必须投递回 UI 线程
        Application.Invoke(() =>
        {
            _resultBox.Title = $"{_category} → {target}";
            SetResultItems(new[] { "搜索中..." });
        });

        var hits = new List<RemoteProject>();
        var errors = new List<string>();
        string? relaxed = null;

        try
        {
            var got = await ModrinthApi.SearchAsync(keyword, type, gameVersion, loader, tag, sort,
                limit: SearchPageSize).ConfigureAwait(false);
            if (epoch != _searchEpoch)
                return; // 已被更新的搜索取代
            hits.AddRange(got);

            // 按版本过滤后一个都没有 → 放宽版本再搜一次 (用户搜的 mod 可能只是没适配当前版本)
            if (hits.Count == 0 && gameVersion is not null && keyword.Length > 0)
            {
                var retry = await ModrinthApi.SearchAsync(keyword, type, null, loader, tag, sort,
                    limit: SearchPageSize).ConfigureAwait(false);
                if (retry.Count > 0)
                {
                    hits.AddRange(retry);
                    relaxed = $"没有适用于 {gameVersion} 的,下面是其他版本";
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add("Modrinth: " + ex.Message);
            task.LogLine("Modrinth 失败: " + ex.Message);
        }
        task.Report(0.55);

        // CurseForge 只有填了 key 才搜 (它的 API 强制要求)
        if (CurseForgeApi.Available)
        {
            try
            {
                var cf = await CurseForgeApi.SearchAsync(keyword, type, gameVersion, loader, tag, sort,
                    CurseForgeApi.ApiKey, limit: SearchPageSize).ConfigureAwait(false);
                // 按标题去重 (同一个 mod 两边都有时保留 Modrinth 的)
                var titles = new HashSet<string>(hits.Select(h => h.Title), StringComparer.OrdinalIgnoreCase);
                hits.AddRange(cf.Where(c => titles.Add(c.Title)));
            }
            catch (Exception ex)
            {
                errors.Add("CurseForge: " + ex.Message);
            }
        }

        if (epoch != _searchEpoch)
            return; // 期间又切了分类, 丢弃这次结果

        _communityHits = hits;
        task.Report(0.9);
        task.LogLine($"命中 {hits.Count} 个");
        task.LogLine($"显示: {string.Join(", ", hits.Take(5).Select(h => h.Title))}"
                     + (hits.Count > 5 ? " ..." : ""));

        Application.Invoke(() =>
        {
            if (epoch != _searchEpoch)
                return;
            if (relaxed is not null)
                _resultBox.Title = $"{_category} → {relaxed}";

            if (hits.Count == 0)
            {
                var msg = errors.Count > 0
                    ? new[] { "搜索失败: " + errors[0], "  (详见任务日志)" }
                    : new[] { Loc.T("dl.noMatch") };
                SetResultItems(msg);
            }
            else
            {
                SetResultItems(hits.Select(h => h.Display).ToList());
            }

            if (errors.Count > 0)
            {
                foreach (var e in errors)
                    LauncherLog.Warn($"[社区资源] {e}");
            }
            _resultBox.SetNeedsDraw();
        });
    }

    /// <summary>
    /// 社区资源的排序项 (和原版的"时间"无关: 服务端排序, 传给 Modrinth index / CF sortField)。
    /// </summary>
    private void UpdateSortOptions()
    {
        var names = CurrentAddonType() is null
            ? new[]
            {
                Loc.T("dl.sort.timeDesc"), Loc.T("dl.sort.timeAsc"),
                Loc.T("dl.sort.nameAsc"), Loc.T("dl.sort.nameDesc"),
            }
            : CommunitySortNames;

        _sortNames = names;
        _sortDrop.Source = new ListWrapper<string>(new ObservableCollection<string>(names));
        _sortDrop.Text = names[Math.Clamp(_sortMode, 0, names.Length - 1)];
    }

    /// <summary>当前社区排序键 (relevance/downloads/follows/newest/updated), 原版页返回 null</summary>
    private string? CurrentCommunitySort()
    {
        if (CurrentAddonType() is null)
            return null;
        var idx = Array.IndexOf(CommunitySortNames, _sortDrop.Text ?? "");
        return idx < 0 ? null : CommunitySortKeys[idx];
    }

    /// <summary>当前分类 Tag ("CF id/Modrinth slug"), "全部" 返回 null</summary>
    private string? CurrentCategoryTag()
    {
        if (CurrentAddonType() is not AddonType.Mod)
            return null;
        var idx = Array.IndexOf(CategoryNames, _catDrop.Text ?? "");
        return idx <= 0 ? null : CategoryTags[idx];
    }

    /// <summary>
    /// 常用 Mod 中文名 → 英文关键词 (学 PCL2 的中文搜索; PCL2 用 SQLite 大词表,
    /// 这里先放一份高频兜底, 命中不了就提示用户用英文名)。
    /// </summary>
    private static readonly (string Zh, string En)[] ZhAliases =
    {
        ("高清修复", "optifine"),
        ("小地图", "journeymap xaeros minimap"),
        ("旅行者地图", "journeymap"),
        ("地图片", "journeymap"),
        ("整理背包", "inventory tweaks"),
        ("一键整理", "inventory tweaks"),
        ("苹果皮", "appleskin"),
        ("钠", "sodium"),
        ("锂", "lithium"),
        ("磷", "phosphor"),
        ("钾", "potassium"),
        ("创世神", "worldedit"),
        ("世界编辑器", "worldedit"),
        ("工业2", "industrial craft"),
        ("工业时代", "industrial craft"),
        ("应用能源2", "applied energistics 2"),
        ("应用能源", "applied energistics"),
        ("热力膨胀", "thermal expansion"),
        ("热力系列", "thermal"),
        ("机械动力", "create"),
        ("神秘时代", "thaumcraft"),
        ("沉浸工程", "immersive engineering"),
        ("林业", "forestry"),
        ("潘马斯", "harvestcraft"),
        ("烹饪农场", "harvestcraft"),
        ("计算机工艺", "computercraft"),
        ("星系", "galacticraft"),
        ("玩家动画", "player animator"),
        ("优化", "optimization"),
        ("性能优化", "optimization"),
    };

    /// <summary>
    /// 中文关键词转换: 整词精确命中优先, 否则取输入中包含的词条合并英文关键词。
    /// 返回 (转换后关键词, 日志备注); 没有中文或未命中时原样返回并给提示。
    /// </summary>
    private static (string Keyword, string? Note) TranslateChinese(string input)
    {
        var text = input.Trim();
        if (text.Length == 0)
            return (text, null);
        if (!text.Any(c => c > 0x7F))
            return (text, null); // 纯英文/数字, 不碰

        // 1) 整词精确命中
        foreach (var (zh, en) in ZhAliases)
        {
            if (string.Equals(text, zh, StringComparison.OrdinalIgnoreCase))
                return (en, Loc.T("dl.zhConverted", en));
        }

        // 2) 包含命中: 合并所有命中的英文关键词
        var hits = ZhAliases.Where(a => text.Contains(a.Zh, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count > 0)
        {
            var en = string.Join(" ", hits.Select(h => h.En));
            return (en, Loc.T("dl.zhConverted", en));
        }

        return (text, Loc.T("dl.zhNoEntry"));
    }

    /// <summary>双击社区项目: 选文件 → 选安装目标 → 下载</summary>
    private void PromptInstallCommunity()
    {
        var sel = _resultList.SelectedItem;
        var idx = sel is null ? -1 : _resultItems.IndexOf(sel);
        if (idx < 0 || idx >= _communityHits.Count)
            return;

        var project = _communityHits[idx];
        var type = CurrentAddonType();
        if (type is null)
            return;

        var inst = TargetInstance();
        if (inst is null)
        {
            MessageBox.ErrorQuery(Application.Instance, Loc.T("common.error"),
                "先装一个游戏版本再来装 mod", Loc.T("common.confirm"));
            return;
        }

        var loader = GameLauncher.DetectLoader(inst);
        if (type == AddonType.Mod && loader is null)
        {
            var r = MessageBox.Query(Application.Instance, "该版本没有 mod 加载器",
                $"{inst.Id} 是原版(没有 Fabric/Forge),装 mod 不会生效。仍然继续?",
                "继续", "取消");
            if (r != 0)
                return;
        }

        // 读文件列表交给任务系统 (任务栏可见, 有日志)
        TaskManager.Submit($"读取 {project.Title}", async (task, ct) =>
        {
            try
            {
                task.LogLine($"{project.Title} ({project.Source})");
                task.LogLine($"按加载器过滤: {loader ?? "不限"}");
                task.Report(0.2);

                // 详情页要看到**所有**版本, 所以不按 MC 版本过滤 (只保留加载器匹配);
                // 之前传了 gameVersion 导致列表里只剩 1 个适配当前版本的
                var files = project.Source == "curseforge"
                    ? await CurseForgeApi.GetFilesAsync(project.Id, null, loader, CurseForgeApi.ApiKey)
                    : await ModrinthApi.GetFilesAsync(project.Id, null, loader);

                // 前置依赖的名字（详情页的「依赖」列表要用；顺带把旧版的 N 次请求改成一次批量）
                var depNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var depIds = files
                    .SelectMany(f => f.RequiredDependencies.Concat(f.OptionalDependencies))
                    .Distinct().Take(40).ToList(); // 上限防个别 mod 依赖几十个
                if (depIds.Count > 0)
                {
                    task.LogLine($"反查 {depIds.Count} 个依赖的名字...");
                    depNames = project.Source == "curseforge"
                        ? await CurseForgeApi.GetProjectTitlesAsync(depIds, CurseForgeApi.ApiKey, ct)
                            .ConfigureAwait(false)
                        : await ModrinthApi.GetProjectTitlesAsync(depIds, ct).ConfigureAwait(false);
                    task.LogLine($"拿到 {depNames.Count} 个名字");
                }

                task.Report(1);
                task.LogLine($"共 {files.Count} 个版本");
                Application.Invoke(() => ShowProjectDetail(project, files, inst, type.Value, depNames));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                task.LogLine("已取消");
                throw;
            }
            catch (Exception ex)
            {
                task.LogLine("失败: " + ex.Message);
                Application.Invoke(() => MessageBox.ErrorQuery(Application.Instance,
                    Loc.T("common.error"), "读取文件列表失败: " + ex.Message, Loc.T("common.confirm")));
            }
        });
    }

    /// <summary>
    /// 项目详情框: 作者 / 下载数 / 简介 / 支持版本 / 前置依赖 + 版本筛选 + 文件列表 + 安装目标。
    /// 学 PCL2: MC 版本筛选按钮、适配当前实例的文件排最前并标注"(所选版本)"。
    /// 概览列表里只放标题, 详细信息都在这里。
    /// </summary>
    private void ShowProjectDetail(RemoteProject project, List<RemoteFile> files,
        MinecraftVersion inst, AddonType type, Dictionary<string, string> depNames)
    {
        // 版本匹配一律用基础 MC 版本 (1.20.1), 不能用目录名 (1.20.1-forge-test)
        var baseVer = BaseVersionOf(inst);

        if (files.Count == 0)
        {
            MessageBox.Query(Application.Instance, project.Title,
                $"没有适用于 {inst.Id} (MC {baseVer}) 的文件" + (project.Source == "curseforge" ? "\n(CurseForge 需要填 API key)" : ""),
                Loc.T("common.confirm"));
            return;
        }

        // ---- 前置依赖汇总 (必选/可选分组; 只有 Modrinth 能解析出名字) ----
        var reqIds = files.SelectMany(f => f.RequiredDependencies).Distinct().ToList();
        var optIds = files.SelectMany(f => f.OptionalDependencies).Except(reqIds).Distinct().ToList();
        string DepLine(string title, List<string> ids) =>
            ids.Count == 0 ? "" :
            $"{title}: " + (project.Source == "curseforge"
                ? $"{ids.Count} 个 {Loc.T("dl.depsUnresolved")}"
                : string.Join(", ", ids.Select(i => depNames.TryGetValue(i, out var n) ? n : i)));

        // ---- 适配当前实例的文件排最前, 并打标注 ----
        var loader = GameLauncher.DetectLoader(inst);
        bool Matches(RemoteFile f) =>
            f.GameVersions.Contains(baseVer) &&
            (loader is null || f.Loaders.Count == 0 || f.Loaders.Contains(loader));
        var ordered = files.OrderByDescending(Matches).ToList(); // OrderByDescending 稳定, 同组内保持 API 顺序(新在前)

        // 全屏再各加 1 行 1 列 (用户要求; 边框会稍微超出屏幕边缘)
        var dialog = new Dialog
        {
            Title = $"{project.Title}  共 {files.Count} 个版本",
            Width = Dim.Fill(-1),
            Height = Dim.Fill(-1),
        };

        // ---- 头部信息 (概览里不显示的东西都放这) ----
        var author = new Label
        {
            Text = $"作者: {(string.IsNullOrWhiteSpace(project.Author) ? "未知" : project.Author)}"
                   + $"    下载: {project.Downloads / 10000} 万"
                   + $"    来源: {project.Source}",
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
        };
        var desc = new Label
        {
            Text = project.Description.Length > 100
                ? project.Description[..100] + "..."
                : project.Description,
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
        };
        var games = project.GameVersions.Count == 0
            ? "未知"
            : string.Join(", ", project.GameVersions.TakeLast(8));
        var versions = new Label
        {
            Text = "支持版本: " + games,
            X = 1,
            Y = 2,
            Width = Dim.Fill(2),
        };

        var reqLine = DepLine(Loc.T("dl.depsRequired"), reqIds);
        var optLine = DepLine(Loc.T("dl.depsOptional"), optIds);
        var deps = new Label
        {
            Text = reqIds.Count + optIds.Count == 0
                ? Loc.T("dl.depsNone")
                : string.Join("    ", new[] { reqLine, optLine }.Where(s => s.Length > 0)),
            X = 1,
            Y = 3,
            Width = Dim.Fill(2),
        };

        // ---- 版本筛选行 (学 PCL2 的筛选按钮; TUI 用下拉) ----
        var verOptions = new List<string> { Loc.T("dl.fileAllVersions") };
        verOptions.AddRange(files
            .SelectMany(f => f.GameVersions)
            .Where(v => v.Length > 0 && char.IsDigit(v[0]))
            .Distinct() //  encounter 顺序 ≈ 新的在前 (API 按发布日期倒序)
            .Take(30));
        var filterLbl = new Label { Text = Loc.T("dl.mcVersion"), X = 1, Y = 5 };
        var filterDrop = new DropDownList
        {
            X = Pos.Right(filterLbl) + 1,
            Y = 5,
            Width = 14,
            Source = new ListWrapper<string>(new ObservableCollection<string>(verOptions)),
        };
        // 默认选中实例版本所在的筛选 (PCL2 的自动勾选); 没有就"全部"
        var defaultFilter = verOptions.Contains(baseVer) ? baseVer : verOptions[0];
        filterDrop.Text = defaultFilter;

        // ---- 文件列表 (按筛选过滤, 适配项置顶) ----
        // 别用 Application.Driver.Rows 算绝对高度：对话框是在 Application.Run 之前构造的，
        // 那时候 Driver 还是初始的 80x25，算出来会偏小。
        // 用 Pos.AnchorEnd 定位依赖框（它和底部按钮共用同一个基准），
        // 「选择版本」再用 Dim.Fill 让出下面要用的行数。
        const int depH = 7;
        const int bottomReserved = 4 + depH + 1; // 按钮2 + 空1 + 目标1 的 4 行, 再给依赖框和 1 行间隙

        var filesBox = new FrameView
        {
            Title = "选择版本",
            X = 0,
            Y = 7,
            Width = Dim.Fill(),
            Height = Dim.Fill(bottomReserved),
            BorderStyle = LineStyle.Single,
        };
        var allItems = ordered
            .Select(f => (Text: Matches(f) ? f.Display + "  " + Loc.T("dl.fileSelected") : f.Display, File: f))
            .ToList();
        var items = new ObservableCollection<string>(); // 当前显示的文本, 与 shownFiles 一一对应
        var shownFiles = new List<RemoteFile>();
        var list = new ListView<string>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        // ---- 依赖框（跟着上面选中的那个版本走）----
        var depBox = new FrameView
        {
            Title = " 依赖 ",
            X = 0,
            Y = Pos.AnchorEnd(bottomReserved),
            Width = Dim.Fill(),
            Height = depH,
            BorderStyle = LineStyle.Single,
        };
        var depEntries = new List<(string Id, bool Required)>();
        var depTexts = new List<string>();
        var depList = new ListView<string>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        depBox.Add(depList);

        void RefreshDeps()
        {
            depEntries.Clear();
            var i = list.SelectedItem is { } sel ? items.IndexOf(sel) : -1;
            if (i >= 0 && i < shownFiles.Count)
            {
                var f = shownFiles[i];
                // 同一个前置经常既在必选又在可选里出现, 按项目 id 去重, 必选优先,
                // 所以不管它在几个版本里被引用, 列表里只出现一个名字
                foreach (var id in f.RequiredDependencies)
                    if (!depEntries.Any(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                        depEntries.Add((id, true));
                foreach (var id in f.OptionalDependencies)
                    if (!depEntries.Any(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                        depEntries.Add((id, false));
            }

            depTexts = depEntries
                .Select(d =>
                {
                    var name = depNames.TryGetValue(d.Id, out var n) && !string.IsNullOrWhiteSpace(n)
                        ? n
                        : d.Id; // 名字没反查到就退回 id, 至少能看出有几个
                    return $"{name}    [{(d.Required ? "必需" : "可选")}]";
                })
                .ToList();
            if (depTexts.Count == 0)
                depTexts.Add("(这个版本没有前置依赖)");
            depList.SetSource(new ObservableCollection<string>(depTexts));
        }

        void RebuildList(string sel)
        {
            var shown = sel == verOptions[0]
                ? allItems
                : allItems.Where(x => x.File.GameVersions.Contains(sel)).ToList();
            shownFiles.Clear();
            shownFiles.AddRange(shown.Select(x => x.File));
            items = new ObservableCollection<string>(shown.Select(x => x.Text));
            // 必须整体替换 Source, 不能 Clear + 逐项 Add:
            // TG 2.5 的 ListView 每 Add 一项都重算 MaxItemLength (O(n²)) 并触发一次全屏重绘,
            // 版本多的模组 (几百个文件) 会明显卡, 和当年原版 920 项列表卡 4.6 秒同一个坑
            list.SetSource(items);
            if (items.Count > 0)
                list.SelectedItem = items[0];
            RefreshDeps();
        }

        // 换选中版本 → 依赖列表跟着变（ValueChanged 在换选中行时触发）
        list.ValueChanged += (_, _) => RefreshDeps();

        RebuildList(defaultFilter);
        filesBox.Add(list);

        // 双击依赖 → 进入那个依赖的详情页（不关当前对话框，回来还在原位置）
        depList.Accepted += (_, _) =>
        {
            if (depList.SelectedItem is not { } sel)
                return;
            var i = depTexts.IndexOf(sel);
            if (i < 0 || i >= depEntries.Count)
                return; // 占位行
            OpenDependencyDetail(depEntries[i].Id, project.Source, inst);
        };

        // 筛选变化 → 重建列表 (保持"适配置顶"顺序)
        filterDrop.ValueChanged += (_, _) => RebuildList(filterDrop.Text ?? "");

        var target = new Label
        {
            Text = $"安装到: {InstallTargetDir(inst, type)}",
            X = 1,
            Y = Pos.AnchorEnd(4),
            Width = Dim.Fill(2),
        };
        var saveAs = new Button { Text = Loc.T("dl.saveAs"), X = Pos.Center() - 17, Y = Pos.AnchorEnd(2) };
        var ok = new Button { Text = "安装", X = Pos.Center() - 6, Y = Pos.AnchorEnd(2), IsDefault = true };
        var cancel = new Button { Text = Loc.T("common.cancel"), X = Pos.Center() + 4, Y = Pos.AnchorEnd(2) };

        // 另存为: 只下载选中文件到指定位置, 不安装、不带前置 (学 PCL2 的 Save_Click)
        saveAs.Accepting += (_, _) =>
        {
            var sel = list.SelectedItem;
            var i = sel is null ? -1 : items.IndexOf(sel);
            if (i < 0 || i >= shownFiles.Count)
                return;
            var f = shownFiles[i];

            // TG 自带的保存文件对话框, 预填默认目录 + 带中文名前缀的文件名
            var sd = new SaveDialog
            {
                Path = Path.Combine(InstallTargetDir(inst, type), DecoratedFileName(project, f, type == AddonType.Mod)),
                Title = Loc.T("dl.saveAs"),
            };
            Application.Run(sd);
            var picked = sd.Canceled ? null : sd.Path;
            sd.Dispose();
            if (string.IsNullOrEmpty(picked))
                return;

            var destDir = Path.GetDirectoryName(picked);
            var destName = Path.GetFileName(picked);
            if (string.IsNullOrEmpty(destDir) || string.IsNullOrEmpty(destName))
                return;
            var dest = Path.Combine(destDir, destName);

            // 目标已存在同名文件先确认 (SaveDialog 本身不拦)
            if (File.Exists(dest))
            {
                var r = MessageBox.Query(Application.Instance, Loc.T("dl.saveAs"),
                    Loc.T("dl.saveOverwrite", destName), Loc.T("dl.install"), Loc.T("common.cancel"));
                if (r != 0)
                    return;
            }

            TaskManager.Submit($"另存为 {project.Title}", async (task, ct) =>
            {
                try
                {
                    task.LogLine($"{f.DisplayName} → {dest}");
                    Directory.CreateDirectory(destDir);
                    var path = await DownloadToAsync(f, destDir, task, ct, dest).ConfigureAwait(false);
                    task.LogLine($"完成: {Path.GetFileName(path)}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    task.LogLine("已取消");
                    throw;
                }
                catch (Exception ex)
                {
                    LauncherLog.Error($"另存为 {project.Title} 失败: {ex}");
                    foreach (var line in Net.Describe(ex).Split('\n'))
                        task.LogLine(line.TrimEnd());
                }
            });
        };

        var chosen = -1;
        ok.Accepting += (_, _) =>
        {
            var sel = list.SelectedItem;
            chosen = sel is null ? 0 : Math.Max(0, items.IndexOf(sel));
            dialog.RequestStop();
        };
        cancel.Accepting += (_, _) => dialog.RequestStop();

        dialog.Add(author, desc, versions, deps, filterLbl, filterDrop, filesBox, depBox, target, saveAs, ok, cancel);
        Application.Run(dialog);
        dialog.Dispose();

        if (chosen < 0 || chosen >= shownFiles.Count)
            return;

        // 列表可能被筛选过, 用并行文件列表按下标回查 (避免重名文本撞车)
        var picked = shownFiles[chosen];

        // 文件列表不再按版本过滤, 所以这里要自己确认一下适配性
        var mcVersions = picked.GameVersions.Where(v => v.Length > 0 && char.IsDigit(v[0])).ToList();
        if (mcVersions.Count > 0 && !mcVersions.Contains(baseVer))
        {
            var r = MessageBox.Query(Application.Instance, "版本不匹配",
                $"这个文件支持 {string.Join(", ", mcVersions.TakeLast(5))},不包含 {baseVer}。\n仍然安装?",
                "继续", "取消");
            if (r != 0)
                return;
        }

        StartCommunityInstall(project, picked, inst, type);
    }

    /// <summary>
    /// 从「依赖」列表点进某个前置的详情页。
    /// 走的是和搜出来点进去同一套流程（读版本列表 + 反查依赖名），只是不自动安装。
    /// </summary>
    private void OpenDependencyDetail(string depId, string source, MinecraftVersion inst)
    {
        var loader = GameLauncher.DetectLoader(inst);

        TaskManager.Submit($"读取依赖 {depId}", async (task, ct) =>
        {
            try
            {
                task.Report(0.15);
                string title = depId, slug = depId;
                List<RemoteFile> files;

                if (source == "curseforge")
                {
                    var (t, s) = await CurseForgeApi.GetProjectAsync(depId, CurseForgeApi.ApiKey, ct)
                        .ConfigureAwait(false);
                    (title, slug) = (t, s);
                    files = await CurseForgeApi.GetFilesAsync(depId, null, loader, CurseForgeApi.ApiKey, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    var (t, s) = await ModrinthApi.GetProjectAsync(depId, ct).ConfigureAwait(false);
                    (title, slug) = (t, s);
                    files = await ModrinthApi.GetFilesAsync(depId, null, loader, ct).ConfigureAwait(false);
                }

                task.LogLine($"{title}:{files.Count} 个版本");

                // 依赖的依赖也要能显示名字（否则点两层进去就是一串 id）
                var depNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var subIds = files
                    .SelectMany(f => f.RequiredDependencies.Concat(f.OptionalDependencies))
                    .Distinct().Take(40).ToList();
                if (subIds.Count > 0)
                {
                    depNames = source == "curseforge"
                        ? await CurseForgeApi.GetProjectTitlesAsync(subIds, CurseForgeApi.ApiKey, ct)
                            .ConfigureAwait(false)
                        : await ModrinthApi.GetProjectTitlesAsync(subIds, ct).ConfigureAwait(false);
                }

                task.Report(1);

                if (files.Count == 0)
                {
                    Application.Invoke(() => MessageBox.Query(Application.Instance, "依赖",
                        $"[{title}]没有适用于 {inst.Id} 的文件。", "好"));
                    return;
                }

                var proj = new RemoteProject
                {
                    Id = depId,
                    Title = title,
                    Slug = slug,
                    Source = source,
                    Description = "(从前置依赖点进来的)",
                };

                // 嵌套模态：外层详情框还在，回来之后位置不变
                Application.Invoke(() => ShowProjectDetail(proj, files, inst, AddonType.Mod, depNames));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                task.LogLine("失败: " + ex.Message);
                Application.Invoke(() => MessageBox.ErrorQuery(Application.Instance, "依赖",
                    "读取依赖详情失败: " + ex.Message, "好"));
            }
        });
    }

    /// <summary>
    /// 安装目标目录: mod→mods/, 资源包→resourcepacks/, 光影→shaderpacks/, 整合包→modpacks/。
    /// mods 目录必须走 <see cref="VersionSettings.InstalledModsDir"/>,
    /// 版本独立设置里可以按版本改隔离模式，这里跟着它才不会装错地方。
    /// </summary>
    private string InstallTargetDir(MinecraftVersion inst, AddonType type)
    {
        var modsDir = VersionSettings.InstalledModsDir(Config, inst);
        var gameDir = Path.GetDirectoryName(modsDir) ?? Config.MinecraftPath;

        return type switch
        {
            AddonType.Mod => modsDir,
            AddonType.ResourcePack => Path.Combine(gameDir, "resourcepacks"),
            AddonType.Shader => Path.Combine(gameDir, "shaderpacks"),
            AddonType.Modpack => Path.Combine(Config.MinecraftPath, "modpacks"),
            // 数据包放全局 datapacks/（1.13+ 支持），单个世界的在 saves/<世界>/datapacks 里
            AddonType.DataPack => Path.Combine(gameDir, "datapacks"),
            _ => gameDir,
        };
    }

    /// <summary>按配置给文件名加中文名前缀 (学 PCL2): 0=不加 1=【名】 2=[名] 3=名- 4=-名</summary>
    private string DecoratedFileName(RemoteProject project, RemoteFile file, bool decorate = true)
    {
        var name = string.IsNullOrWhiteSpace(file.FileName) ? $"{project.Slug}.jar" : file.FileName;
        if (!decorate)
            return name; // 资源包/光影不加前缀 (设置项是"Mod 中文名前缀", 只对 mod 生效)
        var title = (project.Title ?? "").Trim();
        // 标题里可能带文件名非法字符, 剔掉再用
        title = new string(title.Where(c => !"\\/:*?\"<>|".Contains(c)).ToArray()).Trim();
        if (title.Length == 0)
            return name;
        return Config.ModNameStyle switch
        {
            1 => $"[{title}]{name}",
            2 => $"[{title}] {name}",
            3 => $"{title}-{name}",
            4 => $"-{title}{name}",
            _ => name,
        };
    }

    /// <summary>
    /// 整合包安装（学 PCL）：先下到 modpacks/ 再解析（不读 index.json 不知道它要什么版本），
    /// 然后确认实例名 → 校验基础原版 + 加载器 → 解压 overrides → 下载文件清单。
    /// </summary>
    private void StartModpackInstall(RemoteProject project, RemoteFile file)
    {
        var modpacksDir = Path.Combine(Config.MinecraftPath, "modpacks");
        var fileName = string.IsNullOrWhiteSpace(file.FileName)
            ? $"{project.Slug}.mrpack"
            : file.FileName;
        var archive = Path.Combine(modpacksDir, fileName);

        TaskManager.Submit($"安装整合包 {project.Title}", async (task, ct) =>
        {
            try
            {
                Directory.CreateDirectory(modpacksDir);
                task.LogLine($"下载整合包 → {archive}");
                await DownloadToAsync(file, modpacksDir, task, ct, archive).ConfigureAwait(false);

                task.LogLine("解析整合包...");
                var pack = ModpackInstaller.Read(archive);
                task.LogLine(pack.Summary);

                // 解析完才知道要什么版本，所以实例名在这里问
                var tcs = new TaskCompletionSource<string?>();
                Application.Invoke(() => tcs.SetResult(PromptModpackInstanceName(pack)));
                var instanceName = await tcs.Task.ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(instanceName))
                {
                    task.LogLine("已取消(整合包文件留在 modpacks 目录)");
                    return;
                }

                await ModpackInstaller.InstallAsync(archive, pack, Config.MinecraftPath, instanceName,
                    Config.DownloadSource, task, ct).ConfigureAwait(false);

                Application.Invoke(() =>
                {
                    InvalidateVersionCache();
                    Config.SelectedVersion = instanceName;
                    Config.Save();
                    MessageBox.Query(Application.Instance, "安装整合包",
                        $"{pack.Name} 安装完成。\n\n版本名:{instanceName}\n已设为当前版本,可直接在启动页启动。",
                        Loc.T("common.confirm"));
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                task.LogLine("已取消");
                throw;
            }
            catch (Exception ex)
            {
                LauncherLog.Error($"安装整合包 {project.Title} 失败: {ex}");
                foreach (var line in Net.Describe(ex).Split('\n'))
                    task.LogLine(line.TrimEnd());
                Application.Invoke(() => MessageBox.ErrorQuery(Application.Instance,
                    Loc.T("common.error"), Loc.T("dl.installFailed", Net.Brief(ex)), Loc.T("common.confirm")));
                throw;
            }
        });
    }

    /// <summary>整合包安装前确认：显示解析结果 + 让用户起版本名</summary>
    private string? PromptModpackInstanceName(ModpackInfo pack)
    {
        var dlg = new Dialog
        {
            Title = "安装整合包",
            Width = Math.Min(90, Math.Max(60, pack.Name.Length + 24)),
            Height = 16,
            BorderStyle = LineStyle.Single,
        };

        var defaultName = new string(
            pack.Name.Where(c => !"\\/:*?\"<>|".Contains(c)).ToArray()).Trim();
        if (defaultName.Length == 0)
            defaultName = "整合包";
        if (defaultName.Length > 40)
            defaultName = defaultName[..40];

        dlg.Add(new Label { Text = $"整合包    {pack.Name} {pack.Version}", X = 1, Y = 1, Width = Dim.Fill(2) });
        dlg.Add(new Label { Text = $"游戏版本  MC {pack.McVersion}", X = 1, Y = 2, Width = Dim.Fill(2) });
        dlg.Add(new Label { Text = $"加载器    {pack.LoaderDisplay}", X = 1, Y = 3, Width = Dim.Fill(2) });
        dlg.Add(new Label { Text = $"文件数    {pack.Files.Count} 个 + overrides", X = 1, Y = 4, Width = Dim.Fill(2) });
        dlg.Add(new Label
        {
            Text = "前两项必须本地已有,缺的话先到[下载]里装好再回来。",
            X = 1,
            Y = 6,
            Width = Dim.Fill(2),
        });

        dlg.Add(new Label { Text = "版本名", X = 1, Y = 8 });
        var nameField = new TextField { Text = defaultName, X = 10, Y = 8, Width = Dim.Fill(2) };

        var ok = new Button { Text = "开始安装", X = 1, Y = 11, IsDefault = true };
        var cancel = new Button { Text = "取消", X = Pos.Right(ok) + 2, Y = 11 };

        string? result = null;
        ok.Accepting += (_, _) =>
        {
            var nm = (nameField.Text ?? "").Trim();
            if (nm.Length == 0 || nm.Any(c => "\\/:*?\"<>|".Contains(c)))
            {
                // 不关对话框，让用户改
                return;
            }
            result = nm;
            dlg.RequestStop();
        };
        cancel.Accepting += (_, _) => dlg.RequestStop();

        dlg.Add(nameField, ok, cancel);
        nameField.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();
        return result;
    }

    /// <summary>
    /// 安装前确认流 (学 PCL2): 保存对话框 (文件名可改) → 防重名 → 必选前置三选一 → 提交任务。
    /// 整合包走另一条路（要先下载才能解析里面依赖什么），已在上面分流。
    /// </summary>
    private void StartCommunityInstall(RemoteProject project, RemoteFile file,
        MinecraftVersion inst, AddonType type)
    {
        if (type == AddonType.Modpack)
        {
            StartModpackInstall(project, file);
            return;
        }
        var targetDir = InstallTargetDir(inst, type);
        var fileName = DecoratedFileName(project, file, type == AddonType.Mod);

        // ---- 1) 保存对话框: 预填文件名 (可改) + 目标目录 ----
        var dlg = new Dialog
        {
            Title = Loc.T("dl.saveTitle"),
            Width = 64,
            Height = 10,
            BorderStyle = LineStyle.Single,
        };
        var dirLbl = new Label
        {
            Text = $"{Loc.T("dl.saveTarget")}: {targetDir}",
            X = 1, Y = 1, Width = Dim.Fill(2),
        };
        var nameLbl = new Label { Text = Loc.T("dl.saveName"), X = 1, Y = 3 };
        var nameBox = new TextField { Text = fileName, X = Pos.Right(nameLbl) + 1, Y = 3, Width = Dim.Fill(2) };
        var ok = new Button { Text = Loc.T("dl.install"), X = Pos.Center() - 9, Y = Pos.AnchorEnd(2), IsDefault = true };
        var cancel = new Button { Text = Loc.T("common.cancel"), X = Pos.Center() + 3, Y = Pos.AnchorEnd(2) };
        var confirmed = false;
        ok.Accepting += (_, _) =>
        {
            var name = (nameBox.Text ?? "").Trim();
            if (name.Length == 0 || name.Any(c => "\\/:*?\"<>|".Contains(c)))
            {
                MessageBox.ErrorQuery(Application.Instance, Loc.T("common.error"),
                    Loc.T("dl.saveBadName"), Loc.T("common.confirm"));
                return; // 不关对话框, 让用户改
            }
            fileName = name;
            confirmed = true;
            dlg.RequestStop();
        };
        cancel.Accepting += (_, _) => dlg.RequestStop();
        dlg.Add(dirLbl, nameLbl, nameBox, ok, cancel);
        Application.Run(dlg);
        dlg.Dispose();
        if (!confirmed)
            return;

        var dest = Path.Combine(targetDir, fileName);

        // ---- 2) 防重名: 已存在同名文件先问 (学 PCL2 的 FileNameSet 校验) ----
        if (File.Exists(dest))
        {
            var r = MessageBox.Query(Application.Instance, Loc.T("dl.saveTitle"),
                Loc.T("dl.saveOverwrite", fileName), Loc.T("dl.install"), Loc.T("common.cancel"));
            if (r != 0)
                return;
        }

        // ---- 3) 必选前置三选一 (Modrinth 才解析得了; PCL2 同款确认框) ----
        var withDeps = false;
        if (type == AddonType.Mod && file.RequiredDependencies.Count > 0)
        {
            var r = MessageBox.Query(Application.Instance, Loc.T("dl.depsAskTitle"),
                Loc.T("dl.depsAsk", file.RequiredDependencies.Count),
                Loc.T("dl.installWithDeps"), Loc.T("dl.installWithoutDeps"), Loc.T("common.cancel"));
            if (r == 2)
                return; // 取消
            withDeps = r == 0;
        }

        TaskManager.Submit($"安装 {project.Title}", async (task, ct) =>
        {
            try
            {
                task.LogLine($"{project.Title} → {dest}");
                Directory.CreateDirectory(targetDir);

                // 1) 主文件 (用对话框里确认的文件名)
                var path = await DownloadToAsync(file, targetDir, task, ct, dest).ConfigureAwait(false);

                // 2) 必选依赖 (用户选了"带前置安装"才装; 之前是静默自动装)
                if (withDeps)
                {
                    await InstallDependenciesAsync(file, targetDir, inst,
                        GameLauncher.DetectLoader(inst), task, ct).ConfigureAwait(false);
                }

                task.LogLine($"完成: {Path.GetFileName(path)}");
                if (type == AddonType.Modpack)
                    task.LogLine("整合包完整安装流程尚未实现:文件已保存,但不能直接启动");
                Application.Invoke(() =>
                {
                    if (type == AddonType.Modpack)
                    {
                        // 整合包目前只落盘, 完整安装 (解压 overrides / 写版本 json / 装前置)
                        // 还没实现, 必须说清楚, 不能让用户以为装好了就能玩
                        MessageBox.Query(Application.Instance, Loc.T("dl.install"),
                            Loc.T("dl.modpackNotReady", path), Loc.T("common.confirm"));
                        return;
                    }
                    MessageBox.Query(Application.Instance, Loc.T("dl.install"),
                        $"{project.Title} 安装完成\n{path}", Loc.T("common.confirm"));
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                task.LogLine("已取消");
                throw;
            }
            catch (Exception ex)
            {
                LauncherLog.Error($"安装 {project.Title} 失败: {ex}");
                foreach (var line in Net.Describe(ex).Split('\n'))
                    task.LogLine(line.TrimEnd());
                Application.Invoke(() => MessageBox.ErrorQuery(Application.Instance,
                    Loc.T("common.error"), Loc.T("dl.installFailed", Net.Brief(ex)), Loc.T("common.confirm")));
                throw;
            }
        });
    }

    /// <summary>递归装必选依赖 (最多 3 层, 防止循环)</summary>
    private async Task InstallDependenciesAsync(RemoteFile parent, string targetDir,
        MinecraftVersion inst, string? loader, TaskItem task, CancellationToken ct)
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, int Depth)>();
        foreach (var d in parent.RequiredDependencies)
            queue.Enqueue((d, 1));

        // 版本 + 加载器都要过滤: 只按版本过滤会拿到依赖的 Forge 版塞进 Fabric 实例
        var baseVer = BaseVersionOf(inst);

        while (queue.Count > 0)
        {
            var (id, depth) = queue.Dequeue();
            if (depth > 3 || !done.Add(id))
                continue;

            try
            {
                var (title, _) = await ModrinthApi.GetProjectAsync(id, ct).ConfigureAwait(false);
                // 依赖也按基础 MC 版本挑 (inst.Id 是目录名, 拿它过滤永远 0 条)
                var files = await ModrinthApi.GetFilesAsync(id, baseVer, loader, ct).ConfigureAwait(false);
                if (files.Count == 0)
                {
                    task.LogLine($"[依赖] {title}: 没找到可用版本,跳过");
                    continue;
                }

                task.LogLine($"[依赖] {title}");
                await DownloadToAsync(files[0], targetDir, task, ct).ConfigureAwait(false);
                foreach (var d in files[0].RequiredDependencies)
                    queue.Enqueue((d, depth + 1));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                task.LogLine($"[依赖] {id} 失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 下载单个文件 (借鉴 HMCL): 多候选源顺序尝试 (镜像 → 官方, 或反之) + 每个源重试 2 次 +
    /// 流式 SHA1 校验, 全部失败才放弃。dest 显式指定时用它, 否则用 API 文件名。
    /// </summary>
    private async Task<string> DownloadToAsync(RemoteFile file, string dir,
        TaskItem task, CancellationToken ct, string? dest = null)
    {
        var name = string.IsNullOrWhiteSpace(file.FileName)
            ? $"download-{Guid.NewGuid():N}.jar"
            : file.FileName;
        var target = dest ?? Path.Combine(dir, name);
        var tmp = target + ".tmp";

        task.LogLine($"下载 {name} ({file.Size / 1024.0 / 1024.0:0.0} MB)");

        var lastError = "";
        foreach (var url in CandidateUrls(file.Url))
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using var http = Net.CreateClient(TimeSpan.FromMinutes(10), Config.IgnoreSslErrors);
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();

                    using var sha1 = SHA1.Create();
                    long total = 0;
                    await using (var fs = File.Create(tmp))
                    {
                        var buffer = new byte[81920];
                        int n;
                        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            sha1.TransformBlock(buffer, 0, n, null, 0); // 边下边算, 不额外读一遍
                            await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                            total += n;
                        }
                    }

                    // SHA1 校验 (HMCL 的 IntegrityCheck 思路): API 给了 sha1 就必须一致
                    if (!string.IsNullOrEmpty(file.Sha1))
                    {
                        sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        var actual = Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
                        if (!string.Equals(actual, file.Sha1, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(
                                $"SHA1 不匹配 (期望 {file.Sha1[..Math.Min(8, file.Sha1.Length)]}..., 实际 {actual[..8]}...)");
                    }

                    File.Move(tmp, target, overwrite: true);
                    task.LogLine($"已保存 {total / 1024.0 / 1024.0:0.0} MB → {Path.GetFileName(target)}");
                    return target;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    task.LogLine($"  第 {attempt} 次失败: {ex.Message}");
                }
            }
        }
        throw new IOException($"下载失败: {name} (镜像和官方源都试过了) - {lastError}");
    }

    /// <summary>
    /// 候选 URL (学 PCL2 的 mcimirror 方案): 只对 Modrinth CDN / Forge CDN 换镜像,
    /// 按下载源设置决定镜像优先还是官方优先。
    /// </summary>
    private IEnumerable<string> CandidateUrls(string url)
    {
        var mirror = url
            .Replace("https://cdn.modrinth.com", "https://mod.mcimirror.top", StringComparison.OrdinalIgnoreCase)
            .Replace("https://edge.forgecdn.net", "https://mod.mcimirror.top", StringComparison.OrdinalIgnoreCase);
        if (mirror == url)
        {
            yield return url;
            yield break;
        }
        if (Config.DownloadSource is "auto" or "bmclapi")
        {
            yield return mirror;
            yield return url;
        }
        else
        {
            yield return url;
            yield return mirror;
        }
    }
}

