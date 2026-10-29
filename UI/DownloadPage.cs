using System.Collections.ObjectModel;
using LargeHardLiteLauncher.Core;
using Terminal.Gui;

namespace LargeHardLiteLauncher.UI;

/// <summary>
/// 下载页：顶部分类下拉框 + 搜索行 + 类型下拉框 + 结果 groupbox（全宽）。
/// 原版游戏类别已接 Mojang manifest（官方源 + BMCLAPI 镜像，auto 自动切换），其余类别占位。
/// </summary>
public partial class DownloadPage : PageBase
{
    private static readonly string[] Categories =
    {
        Loc.T("dl.cat.vanilla"), "Mod", Loc.T("dl.cat.modpack"),
        Loc.T("dl.cat.resourcepack"), Loc.T("dl.cat.shader"), Loc.T("dl.cat.datapack"),
    };

    private static readonly string[] VersionFilters = { Loc.T("dl.filter.all"), Loc.T("dl.filter.release"), Loc.T("dl.filter.snapshot"), Loc.T("dl.filter.old") };

    private readonly FrameView _resultBox;
    private readonly ListView<string> _resultList;
    private ObservableCollection<string> _resultItems = new();
    private readonly DropDownList _typeDrop;
    private readonly DropDownList _sortDrop;
    private readonly Label _typeLbl;
    private readonly Label _sortLbl;
    /// <summary>社区资源的 MC 版本输入框 (空 = 用当前实例版本)</summary>
    private readonly Label _verLbl;
    private readonly TextField _verField;
    /// <summary>加载器筛选 (只对 Mod/整合包有意义)</summary>
    private readonly Label _loaderLbl;
    private readonly DropDownList _loaderDrop;
    /// <summary>加载器选项: 第 0 项"自动"= 跟当前实例走</summary>
    private static string[] LoaderOptions => new[]
    {
        Loc.T("dl.loaderAuto"), Loc.T("dl.loaderAny"),
        "Fabric", "Forge", "NeoForge", "Quilt",
    };
    /// <summary>分类筛选 (只对 Mod 有意义): Tag 为 "CurseForgeId/ModrinthId" (学 PCL2)</summary>
    private readonly Label _catLbl;
    private readonly DropDownList _catDrop;
    /// <summary>分类选项显示名 (第 0 项 = 全部); 与 CategoryTags 一一对应</summary>
    private static string[] CategoryNames => new[]
    {
        Loc.T("dl.catFilterAll"),
        Loc.T("dl.cat.worldgen"), Loc.T("dl.cat.technology"), Loc.T("dl.cat.magic"),
        Loc.T("dl.cat.adventure"), Loc.T("dl.cat.decoration"), Loc.T("dl.cat.mobs"),
        Loc.T("dl.cat.utility"), Loc.T("dl.cat.equipment"), Loc.T("dl.cat.storage"),
        Loc.T("dl.cat.food"), Loc.T("dl.cat.farming"), Loc.T("dl.cat.transportation"),
        Loc.T("dl.cat.redstone"), Loc.T("dl.cat.automation"), Loc.T("dl.cat.energy"),
        Loc.T("dl.cat.mechanics"), Loc.T("dl.cat.optimization"), Loc.T("dl.cat.library"),
        Loc.T("dl.cat.server"),
    };
    /// <summary>分类 Tag: "CF分类id/Modrinth分类slug"; 空串 = 只在一边有的分类不过滤另一边</summary>
    private static readonly string[] CategoryTags =
    {
        "",
        "406/worldgen", "412/technology", "419/magic", "422/adventure", "424/decoration",
        "411/mobs", "5191/utility", "434/equipment", "420/storage", "436/food",
        "416/farming", "414/transportation", "4558/redstone", "4843/automation", "417/energy",
        "/game-mechanics", "6814/optimization", "421/library", "435/server",
    };
    /// <summary>社区资源服务端排序名 (与 CommunitySortKeys 一一对应; 原版页仍用本地排序)</summary>
    private static string[] CommunitySortNames => new[]
    {
        Loc.T("dl.sortDefault"), Loc.T("dl.sortDownloads"), Loc.T("dl.sortFollows"),
        Loc.T("dl.sortNewest"), Loc.T("dl.sortUpdated"),
    };
    /// <summary>传给 Modrinth index / CurseForge sortField 的键</summary>
    private static readonly string[] CommunitySortKeys =
        { "relevance", "downloads", "follows", "newest", "updated" };
    /// <summary>上次预填用的版本 id。换实例/版本时值变了 → 重新预填；没变 → 框里内容归用户掌控</summary>
    private string _verPrefillFrom = "";
    private readonly Button _refreshBtn;
    /// <summary>社区资源当前排序名 (本地排序用)</summary>
    private string[] _sortNames = Array.Empty<string>();
    private readonly TextField _searchField;

    private VersionManifest? _manifest;
    private List<VersionEntry> _all = new();
    private List<VersionEntry> _visibleEntries = new(); // 当前列表里显示的版本, 与 _resultItems 一一对应
    /// <summary>版本类型筛选索引 (0=全部 1=正式 2=快照 3=旧版)。
    /// 默认 1=正式版: "全部" 会把快照排在最新正式版前面。存索引不存文案,
    /// 按显示文本判断在切英文界面时会全部失效。</summary>
    private int _filterIndex = 1;
    private int _sortMode; // 0=时间倒序 1=时间正序 2=字典序A-Z 3=字典序Z-A
    private string _category = "原版游戏";
    /// <summary>左侧分类索引: 0=原版 1=Mod 2=整合包 3=数据包 4=资源包 5=光影</summary>
    private int _categoryIndex;

    public DownloadPage(LauncherConfig config) : base(config, $" {Loc.T("tab.download")} ")
    {
        // ===== 左侧类别 groupbox (18%) =====
        var leftBox = new FrameView
        {
            Title = Loc.T("dl.category"),
            X = 0,
            Y = 0,
            Width = Dim.Percent(18),
            Height = Dim.Fill(), // 贴齐内容区底边
            BorderStyle = LineStyle.Single,
        };
        var catList = new ListView<string>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        catList.SetSource(new ObservableCollection<string>(Categories));
        catList.ValueChanged += (_, _) =>
        {
            var item = catList.Value;
            if (!string.IsNullOrEmpty(item))
                SwitchCategory(item);
        };
        leftBox.Add(catList);

        // ===== 行1: 名称搜索 =====
        var nameLbl = new Label { Text = Loc.T("dl.name"), X = Pos.Percent(18) + 1, Y = 1 };
        _searchField = new TextField { X = Pos.Right(nameLbl) + 1, Y = 1, Width = Dim.Fill(12) };
        var searchBtn = new Button { Text = Loc.T("dl.search"), X = Pos.Right(_searchField) + 1, Y = 1, NoPadding = true };
        searchBtn.Accepting += (_, _) => ApplyFilter();
        _searchField.Accepting += (_, _) => ApplyFilter(); // 回车也能搜

        // ===== 行2: 类型下拉框 + 刷新 =====
        _typeLbl = new Label { Text = Loc.T("dl.type"), X = Pos.Percent(18) + 1, Y = 2 };
        var typeLbl = _typeLbl;
        _typeDrop = new DropDownList
        {
            X = Pos.Right(typeLbl) + 1,
            Y = 2,
            Width = 16,
            Source = new ListWrapper<string>(new ObservableCollection<string>(VersionFilters)),
        };
        _typeDrop.Text = VersionFilters[1]; // 默认"正式版" (快照混在里面会把最新正式版顶下去)
        _typeDrop.ValueChanged += (_, _) =>
        {
            // 类型 (全部/正式/快照/旧版) 只对原版列表有意义
            if (CurrentAddonType() is not null)
                return;

            var f = _typeDrop.Text;
            if (!string.IsNullOrEmpty(f))
            {
                var i = Array.IndexOf(VersionFilters, f);
                _filterIndex = i < 0 ? 0 : i;
                ApplyFilter();
            }
        };
        // 社区资源的"版本"输入框: 和类型下拉共用同一块位置, 靠 Visible 互斥
        _verLbl = new Label
        {
            Text = Loc.T("dl.mcVersion"),
            X = Pos.Percent(18) + 1,
            Y = 2,
            Visible = false,
        };
        _verField = new TextField
        {
            // 必须和类型下拉**同一个位置** (类型标签右边), 不是类型下拉的右边,
            // 写 Pos.Right(_typeDrop) 会跑到排序标签上, 和它重合
            X = Pos.Right(_typeLbl) + 1,
            Y = 2,
            Width = 16,
            Visible = false,
        };
        _verField.Accepting += (_, _) => ApplyFilter(); // 回车即搜索

        // 排序下拉框 (类型后面)
        _sortLbl = new Label { Text = Loc.T("dl.sort"), X = Pos.Right(_typeDrop) + 2, Y = 2 };
        var sortLbl = _sortLbl;
        var sortNames = new[]
        {
            Loc.T("dl.sort.timeDesc"), Loc.T("dl.sort.timeAsc"),
            Loc.T("dl.sort.nameAsc"), Loc.T("dl.sort.nameDesc"),
        };
        _sortNames = sortNames;
        _sortDrop = new DropDownList
        {
            X = Pos.Right(sortLbl) + 1,
            Y = 2,
            Width = 10, // 用户要求 -4: 最长项"最新发布"4 个汉字刚好放下, 后面的控件经 Pos.Right 自动跟移
            Source = new ListWrapper<string>(new ObservableCollection<string>(sortNames)),
        };
        _sortDrop.Text = sortNames[0];
        _sortDrop.ValueChanged += (_, _) =>
        {
            var idx = Array.IndexOf(_sortNames, _sortDrop.Text);
            if (idx < 0 || idx == _sortMode)
                return; // 换 Source 时也会触发, 索引没变就跳过

            _sortMode = idx;
            if (CurrentAddonType() is not null)
                // 社区资源: 排序是服务端参数 (Modrinth index / CF sortField), 改了重新联网搜
                StartCommunitySearch();
            else
                ApplyFilter();
        };

        // 分类下拉: 排序右边, 只对 Mod 显示 (整合包的分类体系不一样, 暂不接)
        _catLbl = new Label
        {
            Text = Loc.T("dl.catFilter"),
            X = Pos.Right(_sortDrop) + 2,
            Y = 2,
            Visible = false,
        };
        _catDrop = new DropDownList
        {
            X = Pos.Right(_catLbl) + 1,
            Y = 2,
            Width = 12,
            Source = new ListWrapper<string>(new ObservableCollection<string>(CategoryNames)),
            Visible = false,
        };
        _catDrop.Text = CategoryNames[0];
        _catDrop.ValueChanged += (_, _) =>
        {
            // 改了分类就重新联网搜一次 (和加载器一样)
            if (CurrentAddonType() is not null)
                StartCommunitySearch();
        };

        // 加载器下拉: 分类右边, 只对 Mod/整合包显示
        // 必须放在 _sortDrop/_catDrop 构造之后: Pos.Right(null) 会直接抛 ArgumentNullException
        _loaderLbl = new Label
        {
            Text = Loc.T("dl.loader"),
            X = Pos.Right(_catDrop) + 2,
            Y = 2,
            Visible = false,
        };
        _loaderDrop = new DropDownList
        {
            X = Pos.Right(_loaderLbl) + 1,
            Y = 2,
            Width = 12,
            Source = new ListWrapper<string>(new ObservableCollection<string>(LoaderOptions)),
            Visible = false,
        };
        _loaderDrop.Text = LoaderOptions[0];
        _loaderDrop.ValueChanged += (_, _) =>
        {
            // 改了加载器就重新联网搜一次 (用户要求)
            if (CurrentAddonType() is not null)
                StartCommunitySearch();
        };

        // 初始态是原版页: 刷新按钮紧挨排序下拉 (社区页有加载器时 SwitchCategory 会重定位)
        _refreshBtn = new Button { Text = Loc.T("dl.refresh"), X = Pos.Right(_sortDrop) + 2, Y = 2, NoPadding = true };
        var refreshBtn = _refreshBtn;
        refreshBtn.Accepting += (_, _) =>
        {
            // 原版 → 重新拉 manifest; 社区资源 → 重新搜一次
            if (CurrentAddonType() is null)
            {
                if (_categoryIndex == 0)
                    LoadVersionsAsync(force: true);
                else
                    SetResultItems(new[] { Loc.T("dl.pending", _category) });
            }
            else
            {
                StartCommunitySearch();
            }
        };

        // ===== 结果 groupbox =====
        _resultBox = new FrameView
        {
            Title = _category,
            X = Pos.Percent(18) + 1,
            Y = 4,
            Width = Dim.Fill(2),
            Height = Dim.Fill(), // 贴齐内容区底边
            BorderStyle = LineStyle.Single,
        };
        _resultItems.Add(Loc.T("dl.autoLoad"));
        _resultList = new ListView<string>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        _resultList.Accepted += (_, _) =>
        {
            // 原版走原来的安装流程, 社区资源走各自的安装流程
            if (CurrentAddonType() is null)
                PromptInstall();
            else
                PromptInstallCommunity();
        };
        _resultList.SetSource(_resultItems);
        _resultBox.Add(_resultList);

        Add(leftBox, nameLbl, _searchField, searchBtn, typeLbl, _typeDrop,
            _verLbl, _verField, sortLbl, _sortDrop, _catLbl, _catDrop,
            _loaderLbl, _loaderDrop, refreshBtn, _resultBox);
    }

    private void SwitchCategory(string cat)
    {
        _category = cat;
        _categoryIndex = Math.Max(0, Array.IndexOf(Categories, cat));
        _resultBox.Title = cat;
        // 每个分类的搜索词互不影响: 切过去就清空, 免得原版的关键词把 mod 搜索带偏
        _searchField.Text = "";
        // 切分类时排序重置为第一项 (学 PCL2 的 ResetFilter), 避免沿用上个分类的排序
        _sortMode = 0;

        if (CurrentAddonType() is null)
        {
            // 原版: 恢复类型下拉和刷新, 隐藏社区用的版本框
            _typeLbl.Visible = true;
            _typeDrop.Visible = true;
            _refreshBtn.Visible = true;
            _verLbl.Visible = false;
            _verField.Visible = false;
            _loaderLbl.Visible = false;
            _loaderDrop.Visible = false;
            _catLbl.Visible = false;
            _catDrop.Visible = false;
            // 没有加载器下拉挡着, 刷新按钮紧挨排序下拉, 不留缝
            _refreshBtn.X = Pos.Right(_sortDrop) + 2;
            UpdateSortOptions();

            if (_categoryIndex == 0)
            {
                ApplyFilter(); // 原版游戏列表
            }
            else
            {
                SetResultItems(new[] { Loc.T("dl.pending", cat) }); // 数据包暂不支持
            }
            return;
        }

        // 类型 (正式/快照) 和刷新列表只对原版有意义, 社区资源页换成"版本"输入框
        _typeLbl.Visible = false;
        _typeDrop.Visible = false;
        _refreshBtn.Visible = false;
        _verLbl.Visible = true;
        _verField.Visible = true;
        // 资源包/光影没有加载器概念 (与原版同类: 光影包是独立的); 分类只对 Mod 接
        var needLoader = CurrentAddonType() is AddonType.Mod or AddonType.Modpack;
        var needCat = CurrentAddonType() is AddonType.Mod;
        _catLbl.Visible = needCat;
        _catDrop.Visible = needCat;
        _loaderLbl.Visible = needLoader;
        _loaderDrop.Visible = needLoader;
        // 行2 动态排版, 隐藏的控件不占缝: 加载器锚到分类(Mod)或排序(整合包); 刷新锚到加载器(有)或排序(无)
        _loaderLbl.X = needCat ? Pos.Right(_catDrop) + 2 : Pos.Right(_sortDrop) + 2;
        _loaderDrop.X = Pos.Right(_loaderLbl) + 1;
        _refreshBtn.X = needLoader ? Pos.Right(_loaderDrop) + 2 : Pos.Right(_sortDrop) + 2;
        PrefillVersionBox();
        UpdateSortOptions(); // 社区资源的排序项换成服务端排序

        // 社区资源: 后台搜索 (Modrinth 免 key, CurseForge 看有没有填 key)
        _resultBox.Title = $"{cat} → 搜索中...";
        StartCommunitySearch();
    }

    /// <summary>
    /// 把当前实例的**基础 MC 版本**填进版本框 (学 PCL2 的 targetVersion 预填)。
    /// - 换实例/版本 (Config.SelectedVersion 变了) → 重新预填，否则用户切了版本框里还是老的
    /// - 同一版本 → 不动，用户手动改过/清空的不打扰
    /// -  填的是基础版本 (1.20.1) 不是目录名 (1.20.1-forge-test)，后者拿去搜永远 0 条
    /// 返回 true 表示框里的值真的变了。
    /// </summary>
    private bool PrefillVersionBox()
    {
        var sel = Config.SelectedVersion;
        if (string.IsNullOrEmpty(sel))
            sel = TargetInstance()?.Id ?? "";
        if (sel.Length == 0 || sel == _verPrefillFrom)
            return false;
        _verPrefillFrom = sel;

        // 这里必须**精确匹配** sel, 不能用 TargetInstance(),
        // 后者找不到时会兜底返回第一个版本, 拿它算出来的基础版本是错的
        var all = AllVersions();
        var inst = all.FirstOrDefault(v => string.Equals(v.Id, sel, StringComparison.OrdinalIgnoreCase));
        var v = inst is not null
            ? VersionHelper.ResolveBaseVersion(inst, all)
            : VersionHelper.ResolveBaseVersion(sel, all);
        if (string.IsNullOrEmpty(v) || v == _verField.Text)
            return false;
        _verField.Text = v;
        return true;
    }

    /// <summary>双击版本 → 安装页 (选文件夹 / 版本名 / 加载器; 见 DownloadLoaderInstall.cs)</summary>
    private void PromptInstall()
    {
        var sel = _resultList.SelectedItem;
        var idx = sel is null ? -1 : _resultItems.IndexOf(sel);
        if (idx < 0 || idx >= _visibleEntries.Count)
            return;

        PromptInstallWithLoader(_visibleEntries[idx]);
    }

    /// <summary>
    /// 一次性替换列表内容。
    /// TG 2.5 的 ListView 每次 Add 都全量重算 MaxItemLength (O(n^2)),
    /// 920 项增量灌入要 4.6 秒; 整体替换 Source 只算一次 (Source setter 里 SetContentSize)。
    /// </summary>
    private void SetResultItems(IEnumerable<string> items)
    {
        _resultItems = new ObservableCollection<string>(items);
        _resultList.SetSource(_resultItems);
        // clamp 选中索引, 防 OnActivated 越界 (同 TaskPanel)
        if (_resultItems.Count > 0)
            _resultList.SelectedItem = _resultItems[0];
    }

    public override void OnPageEnter()
    {
        // 切进来先把当前页重排一遍: 页面是构造时建的, 窗口尺寸/任务栏宽度可能已经变了,
        // 不重排会出现控件停留在旧位置的现象
        SetNeedsLayout();
        Layout();
        SetNeedsDraw();

        // 社区页: 用户在启动页换了版本 → 版本框跟着更新并重搜一次
        // (不这么做的话，切回去还是拿老版本在搜)
        if (CurrentAddonType() is not null && PrefillVersionBox())
            StartCommunitySearch();

        LoadVersionsAsync(force: false);
    }

    /// <summary>加载版本列表: 全部丢后台任务, UI 线程只负责提交 (切页零卡顿)</summary>
    private void LoadVersionsAsync(bool force)
    {
        // 已有同名任务在跑就不重复提交
        if (TaskManager.Slots().Any(t => t is { IsDone: false } && t.Name == Loc.T("task.checkVersions")))
            return;

        SetResultItems(new[] { Loc.T("task.loading") });

        TaskManager.Submit(Loc.T("task.checkVersions"), async (item, ct) =>
        {
            // 异常/取消一路往上抛, 在此 (调用方) 捕获并按类型显示
            try
            {
                // 阶段1: 后台读缓存, 有就先显示
                if (!force)
                {
                    var cache = MojangApi.LoadCache();
                    if (cache is not null && cache.Versions.Count > 0)
                    {
                        item.Report(0.3);
                        Application.Invoke(() =>
                        {
                            _manifest = cache;
                            _all = cache.Versions;
                            ApplyFilter();
                        });
                        item.LogLine(Loc.T("task.cacheRead", cache.Versions.Count));
                    }
                }

                // 阶段2: 后台联网刷新
                item.Report(0.6);
                var manifest = await MojangApi.FetchAsync(Config.DownloadSource, Config.IgnoreSslErrors, ct);
                item.Report(0.95);

                Application.Invoke(() =>
                {
                    _manifest = manifest;
                    _all = manifest.Versions;
                    ApplyFilter();
                });
                item.LogLine(Loc.T("task.netRefresh", manifest.Versions.Count, manifest.Latest.Release));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 被用户取消 (任务栏点结束): 显示后继续上抛, TaskManager 标记 Cancelled
                Application.Invoke(() =>
                {
                    if (_manifest is null)
                        SetResultItems(new[] { Loc.T("task.cancelledByUser") });
                });
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                or IOException or InvalidDataException)
            {
                // 网络异常 (HttpRequestException / HttpClient 超时 TaskCanceledException 等)
                Application.Invoke(() =>
                {
                    if (_manifest is null)
                        SetResultItems(new[] { Loc.T("task.networkError") });
                });
                throw;
            }
        });
    }

    /// <summary>按名称关键字 + 类型过滤</summary>
    private void ApplyFilter()
    {
        // 社区资源分类: 搜索按钮改成走 Modrinth/CurseForge
        if (CurrentAddonType() is not null)
        {
            StartCommunitySearch();
            return;
        }

        if (_manifest is null)
            return;

        var list = MojangApi.Filter(_all, _filterIndex);
        // 名称关键字过滤 (不区分大小写)
        var keyword = _searchField.Text?.Trim() ?? "";
        if (keyword.Length > 0)
            list = list.FindAll(v => v.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        if (list.Count == 0)
        {
            SetResultItems(new[] { Loc.T("dl.noMatch") });
            return;
        }
        // 排序: 0=时间倒序(manifest 新在前) 1=时间正序 2=字典序 A-Z 3=字典序 Z-A
        var sorted = _sortMode switch
        {
            1 => Enumerable.Reverse(list).ToList(),
            2 => list.OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            3 => list.OrderByDescending(v => v.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => list,
        };
        // 本地已安装的基础版本 (装了 1.20.1-forge 也算 1.20.1 已装), 列表里标出来,
        // 否则用户对着一堆版本号根本不知道哪个装过 (PCL 有这标识)
        var installed = AllVersions();
        var installedBase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in installed)
            installedBase.Add(VersionHelper.ResolveBaseVersion(v, installed));

        var displays = new List<string>(sorted.Count);
        foreach (var v in sorted)
            displays.Add(installedBase.Contains(v.Id)
                ? $"{v.Display}  {Loc.T("dl.installedTag")}"
                : v.Display);
        _visibleEntries = sorted;
        SetResultItems(displays);
    }
}
