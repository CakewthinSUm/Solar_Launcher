using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Formatting;
using Solar.Core.Versions;
using Solar.Core.Mods;
using Solar.Core.Text;

namespace Solar.App.ViewModels;

/// <summary>
/// 内容详情页：头部信息、正文、截图、兼容版本列表。
/// <para>
/// 进入页面时先用列表页传来的 <see cref="ModProject"/> 把头部填满（标题、作者、图标立刻可见），
/// 再并行去拉完整详情与版本列表。Modrinth 的详情接口不带作者字段，
/// 完全依赖详情接口的话作者会闪一下再消失。
/// </para>
/// </summary>
public sealed partial class AddonProjectViewModel : ViewModelBase
{
    private readonly IModCatalogService _catalog;
    private readonly IModInstaller _installer;
    private readonly ILogger<AddonProjectViewModel> _logger;

    private GameVersion? _instance;
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
    private bool _hasLoaders;

    [ObservableProperty]
    private string _sourceText = "";

    [ObservableProperty]
    private string _typeLabel = "";

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

    public ModProjectType Type => _project?.ProjectType ?? ModProjectType.Mod;

    public bool IsModpack => Type.CreatesVersion();

    /// <summary>整合包会新建版本，不需要先选目标版本。</summary>
    public bool NeedsVersion => !IsModpack;

    public string InstallText => IsModpack ? "安装整合包" : "安装";

    /// <summary>没有选中版本时不能安装，页面要说明原因。</summary>
    public bool CanInstall => SelectedVersion is not null
                              && (IsModpack || (_instance is not null && CanInstallToVersion));

    /// <summary>数据包必须有存档；其余类型只要有版本即可。</summary>
    private bool CanInstallToVersion =>
        _instance is not null
        && (Type != ModProjectType.DataPack || _installer.GetWorlds(null).Count > 0);

    /// <summary>没选版本时没有「兼容」这个概念，切换开关没有意义，直接禁用。</summary>
    public bool CanFilterVersions => _instance is not null && !IsModpack;

    /// <summary>版本与原版 / 共享目录相关的提示。为空表示没有需要注意的地方。</summary>
    public string? CompatibilityWarning { get; private set; }

    public bool HasCompatibilityWarning => !string.IsNullOrWhiteSpace(CompatibilityWarning);

    public string InstallHint
    {
        get
        {
            if (NeedsVersion && _instance is null)
            {
                return "先回到列表页选一个目标版本，才能安装。";
            }

            if (SelectedVersion is null)
            {
                return "先选一个版本。";
            }

            if (Type == ModProjectType.DataPack && _instance is not null && !CanInstallToVersion)
            {
                return "这个版本还没有存档。先在游戏里创建一个世界，数据包只能装进具体存档。";
            }

            return "";
        }
    }

    public AddonProjectViewModel(
        IModCatalogService catalog,
        IModInstaller installer,
        ILogger<AddonProjectViewModel> logger)
    {
        _catalog = catalog;
        _installer = installer;
        _logger = logger;
    }

    public override Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is not AddonProjectRequest request)
        {
            ErrorMessage = "没有要显示的内容。";
            return Task.CompletedTask;
        }

        _instance = request.GameVersion;
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
    public (ModProject Project, ModVersion Version, GameVersion GameVersion)? GetInstallRequest() =>
        _project is not null && SelectedVersion is not null && _instance is not null
            ? (_project, SelectedVersion.Version, _instance)
            : null;

    /// <summary>整合包安装需要创建一个新版本，不依赖目标版本。</summary>
    public (ModProject Project, ModVersion Version)? GetModpackRequest() =>
        _project is { } project && project.ProjectType.CreatesVersion() && SelectedVersion is not null
            ? (project, SelectedVersion.Version)
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
            _logger.LogError(ex, "加载内容详情失败");
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
                ProjectType = detail.ProjectType == ModProjectType.Mod ? project.ProjectType : detail.ProjectType,
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
        var type = project.ProjectType;

        try
        {
            IsLoadingVersions = true;
            VersionHint = "正在加载版本…";

            var versions = await _catalog.GetVersionsAsync(
                project.Source,
                project.Id,
                new ModVersionQuery
                {
                    Type = type,
                    GameVersion = type.CreatesVersion() ? null : _instance?.MinecraftVersion,
                    Loader = type.UsesLoader() ? _instance?.Loader : null,

                    // 没选版本时没有筛选条件，服务端不传 loaders/game_versions 就等于全量；
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
            var gameVersion = type.CreatesVersion() ? null : _instance?.MinecraftVersion;
            var loader = type.UsesLoader() ? _instance?.Loader : null;

            Versions.Clear();
            foreach (var version in versions)
            {
                Versions.Add(new ModVersionItemViewModel(
                    version,
                    ModLoaderMapping.Matches(version, gameVersion, loader)));
            }

            OnPropertyChanged(nameof(HasVersions));

            // 尽量选回用户原来选中的版本；否则默认最新（列表已按发布时间倒序，取第一个兼容的）
            SelectedVersion = Versions.FirstOrDefault(v => v.Version.Id == previousId)
                              ?? Versions.FirstOrDefault(v => v.Compatible)
                              ?? Versions.FirstOrDefault();

            VersionHint = Versions.Count == 0
                ? "这个项目没有可下载的文件。"
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
        HasLoaders = project.ProjectType.UsesLoader() && project.Loaders.Count > 0;
        TypeLabel = project.ProjectType.DisplayName();
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
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(IsModpack));
        OnPropertyChanged(nameof(NeedsVersion));
        OnPropertyChanged(nameof(InstallText));
        OnPropertyChanged(nameof(CanFilterVersions));
        UpdateCompatibilityWarning(project);
    }

    private void UpdateCompatibilityWarning(ModProject project)
    {
        string? warning = null;

        if (project.ProjectType.CreatesVersion())
        {
            // 整合包会创建一个独立版本，不存在“装错版本”的问题
            warning = null;
        }
        else if (_instance is null)
        {
            warning = "还没有选目标版本。回到列表页选一个版本，才能安装。";
        }
        else if (project.ProjectType == ModProjectType.Mod && _instance.Loader == ModLoader.Vanilla)
        {
            warning = "这个版本是原版，没有装加载器。Mod 放进去也不会生效，除非之后加上 Fabric / Forge。";
        }
        else if (project.ProjectType == ModProjectType.Mod
                 && project.Loaders.Count > 0
                 && !project.Loaders.Contains(_instance.Loader))
        {
            warning = $"这个 Mod 标注的加载器是 {ModDisplay.Loaders(project.Loaders)}，"
                      + $"与版本的 {ModDisplay.LoaderName(_instance.Loader)} 不一致。";
        }
        else if (project.ProjectType == ModProjectType.DataPack && _installer.GetWorlds(null).Count == 0)
        {
            warning = "还没有存档。数据包只能装进某个世界的 datapacks 目录，"
                      + "请先在游戏里创建一个世界。";
        }

        CompatibilityWarning = warning;
        OnPropertyChanged(nameof(HasCompatibilityWarning));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(InstallHint));
    }

    private static string SourceName(ModSource source) =>
        source == ModSource.Modrinth ? "Modrinth" : "CurseForge";
}
