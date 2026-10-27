using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Formatting;
using Solar.Core.Instances;
using Solar.Core.Mods;
using Solar.Core.Text;

namespace Solar.App.ViewModels;

/// <summary>
/// 模组详情页：头部信息、正文、截图、兼容版本列表。
/// <para>
/// 进入页面时先用列表页传来的 <see cref="ModProject"/> 把头部填满（标题、作者、图标立刻可见），
/// 再并行去拉完整详情与版本列表。Modrinth 的详情接口不带作者字段，
/// 完全依赖详情接口的话作者会闪一下再消失。
/// </para>
/// </summary>
public sealed partial class ModProjectViewModel : ViewModelBase
{
    private readonly IModCatalogService _catalog;
    private readonly ILogger<ModProjectViewModel> _logger;

    private GameInstance? _instance;
    private ModProject? _project;

    /// <summary>版本列表请求的序号，防止「只看兼容」快速来回切换时旧结果覆盖新结果。</summary>
    private int _versionRequest;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string? _iconUrl;

    [ObservableProperty]
    private string _initial = "?";

    [ObservableProperty]
    private bool _hasIcon;

    [ObservableProperty]
    private string _metaText = "";

    [ObservableProperty]
    private string _loadersText = "";

    [ObservableProperty]
    private string _sourceText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WebsiteUri))]
    private string? _websiteUrl;

    /// <summary><see cref="HyperlinkButton.NavigateUri"/> 要的是 Uri，x:Bind 不会自动转换字符串。</summary>
    public Uri? WebsiteUri =>
        Uri.TryCreate(WebsiteUrl, UriKind.Absolute, out var uri) ? uri : null;

    [ObservableProperty]
    private bool _showAllVersions;

    [ObservableProperty]
    private bool _isLoadingVersions;

    [ObservableProperty]
    private string _versionHint = "";

    /// <summary>正文解析结果。CurseForge 的镜像没有正文接口，这里会是空的。</summary>
    [ObservableProperty]
    private IReadOnlyList<MarkdownBlock> _bodyBlocks = [];

    [ObservableProperty]
    private bool _hasBody;

    /// <summary>截图 URL 列表。与正文里的图片分开：正文图片一律丢弃，避免解码失败留白。</summary>
    public ObservableCollection<string> Gallery { get; } = new();

    public bool HasGallery => Gallery.Count > 0;

    public ObservableCollection<ModVersionItemViewModel> Versions { get; } = new();

    [ObservableProperty]
    private ModVersionItemViewModel? _selectedVersion;

    public bool HasVersions => Versions.Count > 0;

    /// <summary>没有选中实例时不能安装，页面要说明原因。</summary>
    public bool CanInstall => _instance is not null && SelectedVersion is not null;

    /// <summary>没选实例时没有「兼容」这个概念，切换开关没有意义，直接禁用。</summary>
    public bool CanFilterVersions => _instance is not null;

    /// <summary>实例与原版 / 共享目录相关的提示。为空表示没有需要注意的地方。</summary>
    public string? CompatibilityWarning { get; private set; }

    public bool HasCompatibilityWarning => !string.IsNullOrWhiteSpace(CompatibilityWarning);

    public string InstallHint => _instance is null
        ? "先回到列表页选一个目标实例，才能安装。"
        : SelectedVersion is null
            ? "先选一个版本。"
            : "";

    public ModProjectViewModel(IModCatalogService catalog, ILogger<ModProjectViewModel> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public override Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is not ModProjectRequest request)
        {
            ErrorMessage = "没有要显示的 Mod。";
            return Task.CompletedTask;
        }

        _instance = request.Instance;
        ApplyProject(request.Project);

        // 头部已经有内容了，详情与版本在后台补齐，不阻塞页面
        _ = RefreshAsync();
        return Task.CompletedTask;
    }

    partial void OnShowAllVersionsChanged(bool value) => _ = LoadVersionsAsync();

    partial void OnSelectedVersionChanged(ModVersionItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(InstallHint));
    }

    /// <summary>页面在用户点「安装」时调用：先由对话框服务展示清单，这里只负责准备数据。</summary>
    public (ModProject Project, ModVersion Version, GameInstance Instance)? GetInstallRequest() =>
        _project is not null && SelectedVersion is not null && _instance is not null
            ? (_project, SelectedVersion.Version, _instance)
            : null;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_project is not { } project)
        {
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = null;

            await Task.WhenAll(RefreshDetailAsync(project), LoadVersionsAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 Mod 详情失败");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshDetailAsync(ModProject project)
    {
        try
        {
            var detail = await _catalog.GetProjectAsync(project.Source, project.Id);

            // 详情接口的字段更全（正文、截图），但它可能缺作者，所以保留列表页那份作为兜底
            ApplyProject(project with
            {
                Body = detail.Body ?? project.Body,
                Gallery = detail.Gallery.Count > 0 ? detail.Gallery : project.Gallery,
                Author = detail.Author ?? project.Author,
                WebsiteUrl = detail.WebsiteUrl ?? project.WebsiteUrl,
                IconUrl = detail.IconUrl ?? project.IconUrl,
                Summary = string.IsNullOrWhiteSpace(detail.Summary) ? project.Summary : detail.Summary,
                Downloads = detail.Downloads > 0 ? detail.Downloads : project.Downloads,
                UpdatedAt = detail.UpdatedAt ?? project.UpdatedAt,
            });
        }
        catch (Exception ex)
        {
            // 正文拉不到不影响安装，只是少一块内容
            _logger.LogWarning(ex, "加载 {Title} 的详情失败", project.Title);
        }
    }

    private async Task LoadVersionsAsync()
    {
        if (_project is not { } project)
        {
            return;
        }

        var request = ++_versionRequest;

        try
        {
            IsLoadingVersions = true;
            VersionHint = "正在加载版本…";

            var versions = await _catalog.GetVersionsAsync(
                project.Source,
                project.Id,
                new ModVersionQuery
                {
                    GameVersion = _instance?.MinecraftVersion,
                    Loader = _instance?.Loader,

                    // 没选实例时没有筛选条件，服务端不传 loaders/game_versions 就等于全量；
                    // 这是「浏览」状态，不是「只看兼容」，所以不要再额外套一层
                    IncludeIncompatible = ShowAllVersions || _instance is null,
                    Limit = 100,
                });

            // 期间用户又切换了开关，这次结果已经过时
            if (request != _versionRequest)
            {
                return;
            }

            var previousId = SelectedVersion?.Version.Id;

            Versions.Clear();
            foreach (var version in versions)
            {
                Versions.Add(new ModVersionItemViewModel(
                    version,
                    ModLoaderMapping.Matches(version, _instance?.MinecraftVersion, _instance?.Loader)));
            }

            OnPropertyChanged(nameof(HasVersions));

            // 尽量选回用户原来选中的版本；否则默认最新（列表已按发布时间倒序，取第一个兼容的）
            SelectedVersion = Versions.FirstOrDefault(v => v.Version.Id == previousId)
                              ?? Versions.FirstOrDefault(v => v.Compatible)
                              ?? Versions.FirstOrDefault();

            VersionHint = Versions.Count == 0
                ? "这个项目没有可下载的文件。"
                : _instance is null
                    ? $"共 {Versions.Count} 个版本（未选实例，全部列出）"
                    : $"共 {Versions.Count} 个版本";
        }
        catch (Exception ex)
        {
            if (request == _versionRequest)
            {
                _logger.LogError(ex, "加载 {Title} 的版本列表失败", project.Title);
                VersionHint = "版本列表加载失败：" + ex.Message;
            }
        }
        finally
        {
            if (request == _versionRequest)
            {
                IsLoadingVersions = false;
            }
        }
    }

    private void ApplyProject(ModProject project)
    {
        _project = project;

        Title = project.Title;
        Summary = project.Summary;
        IconUrl = project.IconUrl;
        HasIcon = !string.IsNullOrWhiteSpace(project.IconUrl);
        Initial = ModDisplay.Initial(project.Title);
        LoadersText = ModDisplay.Loaders(project.Loaders);
        SourceText = SourceName(project.Source) + (project.AlsoOn.Count > 0 ? "（另一源也有收录）" : "");
        WebsiteUrl = project.WebsiteUrl;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(project.Author))
        {
            parts.Add(project.Author!);
        }

        parts.Add($"{ModDisplay.Downloads(project.Downloads)} 次下载");
        if (project.UpdatedAt is { } updated)
        {
            parts.Add($"更新于 {ModDisplay.RelativeTime(updated)}");
        }

        MetaText = string.Join(" · ", parts);

        BodyBlocks = MarkdownLite.Parse(project.Body);
        HasBody = BodyBlocks.Count > 0;

        Gallery.Clear();
        foreach (var image in project.Gallery.Take(12))
        {
            Gallery.Add(image);
        }

        OnPropertyChanged(nameof(HasGallery));
        UpdateCompatibilityWarning(project);
    }

    private void UpdateCompatibilityWarning(ModProject project)
    {
        string? warning = null;

        if (_instance is null)
        {
            warning = "还没有选目标实例。回到列表页选一个实例，才能安装。";
        }
        else if (_instance.Loader == ModLoader.Vanilla)
        {
            warning = "这个实例是原版，没有装加载器。Mod 放进去也不会生效，除非之后加上 Fabric / Forge。";
        }
        else if (project.Loaders.Count > 0 && !project.Loaders.Contains(_instance.Loader))
        {
            warning = $"这个 Mod 标注的加载器是 {ModDisplay.Loaders(project.Loaders)}，"
                      + $"与实例的 {ModDisplay.LoaderName(_instance.Loader)} 不一致。";
        }
        else if (!_instance.VersionIsolation)
        {
            warning = "实例没有开启版本隔离，Mod 会装进共享的游戏目录，影响所有未隔离的实例。";
        }

        CompatibilityWarning = warning;
        OnPropertyChanged(nameof(HasCompatibilityWarning));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(InstallHint));
    }

    private string InstanceLabel => _instance is null
        ? "这个实例"
        : $"{_instance.MinecraftVersion} {ModDisplay.LoaderName(_instance.Loader)}".Trim();

    private static string SourceName(ModSource source) =>
        source == ModSource.Modrinth ? "Modrinth" : "CurseForge";
}
