using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Formatting;
using Solar.Core.Instances;
using Solar.Core.Mods;

namespace Solar.App.ViewModels;

/// <summary>
/// 搜索结果里的一项。模板需要的文本在这里一次算好：
/// XAML 的 <c>x:Bind</c> 不能调静态方法，也不该在模板里做格式化。
/// </summary>
public sealed partial class ModCardViewModel(ModProject project) : ObservableObject
{
    public ModProject Project { get; } = project;

    public string Title => Project.Title;

    public string Summary => string.IsNullOrWhiteSpace(Project.Summary) ? "（没有简介）" : Project.Summary;

    public string IconUrl => Project.IconUrl ?? "";

    public string Initial => ModDisplay.Initial(Project.Title);

    public string DownloadsText => ModDisplay.Downloads(Project.Downloads);

    public string UpdatedText => ModDisplay.RelativeTime(Project.UpdatedAt);

    public string LoadersText => ModDisplay.Loaders(Project.Loaders);

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
public sealed record ModProjectRequest(ModProject Project, GameInstance? Instance);

/// <summary>
/// 「下载 → Mod」列表页。
/// <para>
/// 筛选条件来自选中的实例：它的游戏版本与加载器直接作为服务端过滤条件，
/// 这样搜出来的每一条都能装。没选实例时也能浏览，只是不能安装。
/// </para>
/// </summary>
public sealed partial class ModDownloadViewModel : ViewModelBase
{
    private const int PageSize = 40;

    /// <summary>搜索框的防抖间隔。打字时每敲一个字母就发请求，两个源都会限流。</summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(350);

    private readonly IInstanceRepository _repository;
    private readonly IModCatalogService _catalog;
    private readonly ILogger<ModDownloadViewModel> _logger;

    /// <summary>当前正在进行的搜索。新搜索开始时取消它，避免旧结果覆盖新结果。</summary>
    private CancellationTokenSource? _searchCts;

    /// <summary>已经加载的条数，作为下一次「加载更多」的 offset。</summary>
    private int _offset;

    [ObservableProperty]
    private GameInstance? _selectedInstance;

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>0 相关度、1 下载量、2 更新时间、3 最新发布。</summary>
    [ObservableProperty]
    private int _sortIndex;

    /// <summary>0 全部、1 Modrinth、2 CurseForge。</summary>
    [ObservableProperty]
    private int _sourceIndex;

    [ObservableProperty]
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
    private bool _hasResults;

    public ModDownloadViewModel(
        IInstanceRepository repository,
        IModCatalogService catalog,
        ILogger<ModDownloadViewModel> logger)
    {
        _repository = repository;
        _catalog = catalog;
        _logger = logger;
    }

    public ObservableCollection<GameInstance> Instances { get; } = new();

    public ObservableCollection<ModCardViewModel> Results { get; } = new();

    public bool HasDegradedHint => !string.IsNullOrWhiteSpace(DegradedHint);

    public bool HasNoInstances => Instances.Count == 0;

    /// <summary>没有实例时不能安装，页面上要说明原因。</summary>
    public bool CanInstall => SelectedInstance is not null;

    public string EmptyHint => HasNoInstances
        ? "还没有实例。先在「管理 → 实例」创建一个，搜索才能按版本与加载器过滤。"
        : "没有找到匹配的 Mod。搜索只认英文名或原名，试试更短的关键词。";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;

            var all = await _repository.GetAllAsync();

            var previousId = SelectedInstance?.Id;
            Instances.Clear();
            foreach (var instance in all)
            {
                Instances.Add(instance);
            }

            OnPropertyChanged(nameof(HasNoInstances));

            // 列表在别的页面被改过（新建 / 删除实例），尽量选回原来那个
            SelectedInstance = Instances.FirstOrDefault(i => i.Id == previousId) ?? Instances.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载实例列表失败");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        // 首次进入就给出默认内容（热门 Mod），不必让用户先打字
        if (Results.Count == 0)
        {
            await SearchAsync(reset: true);
        }
    }

    partial void OnSelectedInstanceChanged(GameInstance? value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(EmptyHint));

        // 换实例等于换筛选条件，必须重查；但不能在属性变更回调里同步 await
        _ = ReloadAsync();
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

                Results.Add(new ModCardViewModel(project));
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
            _logger.LogError(ex, "搜索 Mod 失败");
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
        Text = SearchText.Trim(),
        GameVersion = SelectedInstance?.MinecraftVersion,
        Loader = SelectedInstance?.Loader,
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
