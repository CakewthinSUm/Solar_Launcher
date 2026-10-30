using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Formatting;
using Solar.Core.Instances;
using Solar.Core.Minecraft;
using Solar.Core.Mods;

namespace Solar.App.ViewModels;

/// <summary>
/// 「下载」下五个内容分区的展示信息。
/// <para>
/// 它们共用一套搜索/详情/安装流程，区别只在项目类型、筛选条件与安装落点，
/// 所以只用一个页面和一个视图模型，由侧边栏的导航参数切换分区。
/// </para>
/// </summary>
public sealed record AddonSectionInfo(
    DownloadSection Section,
    ModProjectType Type,
    string Key,
    string Title,
    string Description,
    string SearchPlaceholder,
    string EmptyHint)
{
    public static IReadOnlyList<AddonSectionInfo> All { get; } =
    [
        new(DownloadSection.Mod, ModProjectType.Mod, "mod",
            "Mod", "从 Modrinth 与 CurseForge 搜索，直接装进实例的 mods 目录",
            "搜索 Mod（英文名或原名，例如 sodium）",
            "没有找到匹配的 Mod。搜索只认英文名或原名，试试更短的关键词。"),
        new(DownloadSection.Shaders, ModProjectType.Shader, "shaders",
            "光影", "安装到实例的 shaderpacks 目录；需要 Iris 或 OptiFine 才能生效",
            "搜索光影（英文名或原名，例如 complementary）",
            "没有找到匹配的光影。搜索只认英文名或原名，试试更短的关键词。"),
        new(DownloadSection.Modpacks, ModProjectType.Modpack, "modpacks",
            "整合包", "一键安装整合包：自动创建独立实例，并把包里的文件全部下好",
            "搜索整合包（英文名或原名，例如 fabulously optimized）",
            "没有找到匹配的整合包。搜索只认英文名或原名，试试更短的关键词。"),
        new(DownloadSection.ResourcePacks, ModProjectType.ResourcePack, "resourcepacks",
            "资源包", "安装到实例的 resourcepacks 目录，替换贴图、字体与音效",
            "搜索资源包（英文名或原名，例如 fresh animations）",
            "没有找到匹配的资源包。搜索只认英文名或原名，试试更短的关键词。"),
        new(DownloadSection.DataPacks, ModProjectType.DataPack, "datapacks",
            "数据包", "安装到某个存档的 datapacks 目录，扩展原版玩法",
            "搜索数据包（英文名或原名，例如 veinminer）",
            "没有找到匹配的数据包。搜索只认英文名或原名，试试更短的关键词。"),
    ];

    public static AddonSectionInfo FromKey(string? key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}

/// <summary>搜索结果里的一项。模板需要的文本在这里一次算好，XAML 里不能调静态方法。</summary>
public sealed partial class AddonCardViewModel(ModProject project) : ObservableObject
{
    public ModProject Project { get; } = project;

    public string Title => Project.Title;

    public string Summary => string.IsNullOrWhiteSpace(Project.Summary) ? "（没有简介）" : Project.Summary;

    public string IconUrl => Project.IconUrl ?? "";

    public string Initial => ModDisplay.Initial(Project.Title);

    public string DownloadsText => ModDisplay.Downloads(Project.Downloads);

    public string UpdatedText => ModDisplay.RelativeTime(Project.UpdatedAt);

    public string LoadersText => ModDisplay.Loaders(Project.Loaders);

    /// <summary>光影 / 资源包 / 数据包 / 整合包没有加载器，那一行不显示。</summary>
    public bool HasLoaders => Project.ProjectType.UsesLoader() && Project.Loaders.Count > 0;

    public string SourceBadge => ModDisplay.SourceBadge(Project);

    public bool HasSourceBadge => SourceBadge.Length > 0;

    /// <summary>
    /// 图标加载失败时置 false，模板换成首字母方块。
    /// Modrinth 的图标可能是 WebP，Win10 不一定装了系统解码器。
    /// </summary>
    [ObservableProperty]
    private bool _hasIcon = true;

    public void MarkIconFailed() => HasIcon = false;
}

/// <summary>从列表页进入详情页时携带的参数：先用列表里的项目信息把头部填满，再去拉正文。</summary>
public sealed record AddonProjectRequest(ModProject Project, GameInstance? Instance);

/// <summary>
/// 「下载 → Mod / 光影 / 整合包 / 资源包 / 数据包」列表页。
/// <para>
/// 筛选条件来自选中的实例：它的游戏版本与加载器直接作为服务端过滤条件，
/// 这样搜出来的每一条都能装。整合包例外——它会新建实例，所以按用户选的
/// 游戏版本过滤，而不是绑定到已有实例。
/// </para>
/// </summary>
public sealed partial class AddonDownloadViewModel : ViewModelBase
{
    private const int PageSize = 40;
    private const string AllVersions = "全部版本";

    /// <summary>搜索框的防抖间隔。打字时每敲一个字母就发请求，两个源都会限流。</summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(350);

    private readonly IInstanceRepository _repository;
    private readonly IModCatalogService _catalog;
    private readonly IVersionManifestService _manifestService;
    private readonly ILogger<AddonDownloadViewModel> _logger;

    /// <summary>当前正在进行的搜索。新搜索开始时取消它，避免旧结果覆盖新结果。</summary>
    private CancellationTokenSource? _searchCts;

    /// <summary>已经加载的条数，作为下一次「加载更多」的 offset。</summary>
    private int _offset;

    /// <summary>初始化时赋值会触发属性回调，这里统一压住，避免一次导航发三次搜索。</summary>
    private bool _suppressReload;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModpack))]
    [NotifyPropertyChangedFor(nameof(NeedsInstance))]
    [NotifyPropertyChangedFor(nameof(SectionTitle))]
    [NotifyPropertyChangedFor(nameof(SectionDescription))]
    [NotifyPropertyChangedFor(nameof(SearchPlaceholder))]
    [NotifyPropertyChangedFor(nameof(EmptyHint))]
    [NotifyPropertyChangedFor(nameof(ShowInstanceHint))]
    private AddonSectionInfo _section = AddonSectionInfo.All[0];

    [ObservableProperty]
    private GameInstance? _selectedInstance;

    [ObservableProperty]
    private string _selectedGameVersion = AllVersions;

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>0 相关度、1 下载量、2 更新时间、3 最新发布。</summary>
    [ObservableProperty]
    private int _sortIndex;

    /// <summary>0 全部、1 Modrinth、2 CurseForge。</summary>
    [ObservableProperty]
    private int _sourceIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInlineBusy))]
    [NotifyPropertyChangedFor(nameof(ShowCenterBusy))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isSearching;

    [ObservableProperty]
    private bool _canLoadMore;

    [ObservableProperty]
    private string _totalText = "";

    /// <summary>部分源不可用时的提示，例如「CurseForge 暂时不可用」。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDegradedHint))]
    private string? _degradedHint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInlineBusy))]
    [NotifyPropertyChangedFor(nameof(ShowCenterBusy))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _hasResults;

    public AddonDownloadViewModel(
        IInstanceRepository repository,
        IModCatalogService catalog,
        IVersionManifestService manifestService,
        ILogger<AddonDownloadViewModel> logger)
    {
        _repository = repository;
        _catalog = catalog;
        _manifestService = manifestService;
        _logger = logger;
    }

    public ObservableCollection<GameInstance> Instances { get; } = new();

    /// <summary>整合包分区可选的目标游戏版本；第一个是「全部版本」。</summary>
    public ObservableCollection<string> GameVersions { get; } = new();

    public ObservableCollection<AddonCardViewModel> Results { get; } = new();

    public bool IsModpack => Section.Type.CreatesInstance();

    /// <summary>整合包不需要选实例；其余类型都要选一个安装目标。</summary>
    public bool NeedsInstance => !IsModpack;

    public string SectionTitle => Section.Title;

    public string SectionDescription => Section.Description;

    public string SearchPlaceholder => Section.SearchPlaceholder;

    public bool HasDegradedHint => !string.IsNullOrWhiteSpace(DegradedHint);

    public bool HasNoInstances => Instances.Count == 0;

    public bool ShowInstanceHint => NeedsInstance && HasNoInstances;

    public string EmptyHint => NeedsInstance && HasNoInstances
        ? "还没有实例。先在「管理」创建一个，搜索才能按版本与加载器过滤。"
        : Section.EmptyHint;

    /// <summary>
    /// 列表已有内容时，进度环保持在「加载更多」旁的原位，
    /// 避免内容区在搜索时来回跳。列表为空才用中央大环。
    /// </summary>
    public bool ShowInlineBusy => IsSearching && HasResults;

    /// <summary>列表为空时的搜索加载态：在内容区正中显示大进度环。</summary>
    public bool ShowCenterBusy => IsSearching && !HasResults;

    /// <summary>「没有结果」只在搜索结束且确实没有内容时出现，搜索中不闪出来。</summary>
    public bool ShowEmptyState => !HasResults && !IsSearching;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var section = AddonSectionInfo.FromKey(parameter as string);

        _suppressReload = true;

        try
        {
            IsBusy = true;
            ErrorMessage = null;

            Section = section;

            // 切换分区时先清掉上一个分区的结果，避免搜索返回前短暂显示错的内容
            Results.Clear();
            HasResults = false;
            TotalText = "";
            CanLoadMore = false;

            var all = await _repository.GetAllAsync();
            var previousId = SelectedInstance?.Id;

            Instances.Clear();
            foreach (var instance in all)
            {
                Instances.Add(instance);
            }

            OnPropertyChanged(nameof(HasNoInstances));
            OnPropertyChanged(nameof(ShowInstanceHint));
            OnPropertyChanged(nameof(EmptyHint));

            // 列表在别的页面被改过（新建 / 删除实例），尽量选回原来那个
            SelectedInstance = Instances.FirstOrDefault(i => i.Id == previousId) ?? Instances.FirstOrDefault();

            if (IsModpack)
            {
                await LoadGameVersionsAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载下载页失败");
            ErrorMessage = ex.Message;
        }
        finally
        {
            _suppressReload = false;
            IsBusy = false;
        }

        // 首次进入就给出默认内容（热门条目），不必让用户先打字
        await SearchAsync(reset: true);
    }

    private async Task LoadGameVersionsAsync()
    {
        if (GameVersions.Count > 0)
        {
            SelectedGameVersion = AllVersions;
            return;
        }

        GameVersions.Add(AllVersions);

        try
        {
            var manifest = await _manifestService.GetManifestAsync();

            // 只列正式版：快照几乎没有整合包，列出来只会让下拉框变长
            foreach (var version in manifest.Versions
                         .Where(v => string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase))
                         .Take(80))
            {
                GameVersions.Add(version.Id);
            }
        }
        catch (Exception ex)
        {
            // 版本清单拉不到不影响搜索，只是少了按版本过滤的下拉项
            _logger.LogWarning(ex, "加载游戏版本列表失败");
        }

        SelectedGameVersion = AllVersions;
    }

    partial void OnSelectedInstanceChanged(GameInstance? value)
    {
        OnPropertyChanged(nameof(ShowInstanceHint));
        OnPropertyChanged(nameof(EmptyHint));

        if (!_suppressReload)
        {
            _ = ReloadAsync();
        }
    }

    partial void OnSelectedGameVersionChanged(string value)
    {
        if (!_suppressReload)
        {
            _ = ReloadAsync();
        }
    }

    partial void OnSearchTextChanged(string value) => _ = DebouncedSearchAsync();

    partial void OnSortIndexChanged(int value) => _ = ReloadAsync();

    partial void OnSourceIndexChanged(int value) => _ = ReloadAsync();

    [RelayCommand]
    private Task LoadMoreAsync() => SearchAsync(reset: false);

    /// <summary>
    /// 发一次搜索。<paramref name="reset"/> 为 true 时从头开始，否则在当前结果后追加。
    /// </summary>
    private async Task SearchAsync(bool reset)
    {
        // 取消上一次搜索：快速改条件时旧请求可能后返回，把新结果盖掉
        _searchCts?.Cancel();
        _searchCts?.Dispose();

        var cts = new CancellationTokenSource();
        _searchCts = cts;

        if (reset)
        {
            _offset = 0;
        }

        try
        {
            IsSearching = true;
            ErrorMessage = null;
            DegradedHint = null;

            var page = await _catalog.SearchAsync(BuildQuery(), cts.Token);

            if (cts.Token.IsCancellationRequested)
            {
                return;
            }

            if (reset)
            {
                Results.Clear();
            }

            var added = 0;
            foreach (var project in page.Items)
            {
                // 双源合并时两个源各自按 offset 取一页，翻页会拿到重复项目
                if (Results.Any(c => c.Project.Source == project.Source && c.Project.Id == project.Id))
                {
                    continue;
                }

                Results.Add(new AddonCardViewModel(project));
                added++;
            }

            // offset 按「这一页取回多少条」推进，而不是按新增条数：
            // 双源合并后重排，重复项会被去重掉，按新增数推进会一直停在同一个窗口。
            _offset += page.Items.Count;
            HasResults = Results.Count > 0;

            // 这一页全是重复项时说明已经到底，再点「加载更多」只会拿到同样的内容
            CanLoadMore = page.Items.Count >= PageSize && (added > 0 || reset);
            TotalText = Results.Count == 0
                ? ""
                : $"已显示 {Results.Count} 项" + (page.TotalHits > 0 ? $"，共约 {page.TotalHits:N0} 项" : "");

            if (page.FailedSources.Count > 0)
            {
                DegradedHint = string.Join("、", page.FailedSources.Select(SourceName))
                               + " 暂时不可用，当前结果不完整。";
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 被新搜索取代，什么都不用做
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜索 {Type} 失败", Section.Title);
            ErrorMessage = ex.Message;
            HasResults = Results.Count > 0;
            CanLoadMore = false;
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearching = false;
            }
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => SearchAsync(reset: true);

    private async Task DebouncedSearchAsync()
    {
        try
        {
            await Task.Delay(DebounceDelay);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        await SearchAsync(reset: true);
    }

    private Task ReloadAsync() => SearchAsync(reset: true);

    private ModSearchQuery BuildQuery() => new()
    {
        Type = Section.Type,
        Text = SearchText.Trim(),

        // 整合包按用户选的目标版本过滤；其余类型跟着目标实例走
        GameVersion = IsModpack
            ? (string.Equals(SelectedGameVersion, AllVersions, StringComparison.Ordinal) ? null : SelectedGameVersion)
            : SelectedInstance?.MinecraftVersion,

        Loader = Section.Type.UsesLoader() ? SelectedInstance?.Loader : null,
        Sort = SortIndex switch
        {
            1 => ModSort.Downloads,
            2 => ModSort.Updated,
            3 => ModSort.Newest,
            _ => ModSort.Relevance,
        },
        Source = SourceIndex switch
        {
            1 => ModSource.Modrinth,
            2 => ModSource.CurseForge,
            _ => null,
        },
        Offset = _offset,
        Limit = PageSize,
    };

    private static string SourceName(ModSource source) =>
        source == ModSource.Modrinth ? "Modrinth" : "CurseForge";
}
