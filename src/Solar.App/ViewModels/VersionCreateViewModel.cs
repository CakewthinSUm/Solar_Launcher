using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Services;
using Solar.Core.Addons;
using Solar.Core.Versions;
using Solar.Core.Java;
using Solar.Core.Loaders;
using Solar.Core.Minecraft;
using Solar.Core.Models;
using Solar.Core.Paths;
using Solar.Core.Settings;

namespace Solar.App.ViewModels;

/// <summary>
/// 创建版本页：从下载页点一个版本进来，选组件，一次装完。
/// <para>
/// 组件列表是懒加载的：进页面先只拉与当前 MC 版本相关的列表，
/// 用户选了 Fabric 才去拉 Fabric API，选了 OptiFine 才拉 OptiFabric 之类。
/// </para>
/// </summary>
public sealed partial class VersionCreateViewModel : ViewModelBase
{
    private readonly IVersionManifestService _manifest;
    private readonly ILoaderVersionService _loaderVersions;
    private readonly IOptiFineService _optiFine;
    private readonly ILiteLoaderService _liteLoader;
    private readonly IAddonCatalog _addons;
    private readonly IVersionCreationService _creation;
    private readonly IJavaDetector _javaDetector;
    private readonly MinecraftPaths _gameDirectories;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly TaskbarProgressService _taskbar;
    private readonly ILogger<VersionCreateViewModel> _logger;

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _createCts;
    private bool _suppressRefresh;
    private bool _isolationTouched;

    /// <summary>导航参数；页面缓存复用，每次导航由页面写入。</summary>
    public VersionSummary? Version { get; set; }

    /// <summary>页面顶部的版本下拉；从版本页直接新建时用它挑版本。</summary>
    public ObservableCollection<VersionSummary> Versions { get; } = new();

    [ObservableProperty]
    private VersionSummary? _selectedVersion;

    [ObservableProperty]
    private string _minecraftVersion = "";

    [ObservableProperty]
    private string _versionSubtitle = "";

    [ObservableProperty]
    private string _VersionName = "";

    [ObservableProperty]
    private bool _versionIsolation;

    [ObservableProperty]
    private string _isolationHint = "";

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private double _createProgress;

    [ObservableProperty]
    private string _createStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTips))]
    [NotifyPropertyChangedFor(nameof(HasWarningTip))]
    private string? _warningTip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTips))]
    [NotifyPropertyChangedFor(nameof(HasInfoTip))]
    private string? _infoTip;

    public VersionCreateViewModel(
        IVersionManifestService manifest,
        ILoaderVersionService loaderVersions,
        IOptiFineService optiFine,
        ILiteLoaderService liteLoader,
        IAddonCatalog addons,
        IVersionCreationService creation,
        IJavaDetector javaDetector,
        MinecraftPaths gameDirectories,
        ISettingsService settings,
        IDialogService dialogs,
        TaskbarProgressService taskbar,
        ILogger<VersionCreateViewModel> logger)
    {
        _manifest = manifest;
        _loaderVersions = loaderVersions;
        _optiFine = optiFine;
        _liteLoader = liteLoader;
        _addons = addons;
        _creation = creation;
        _javaDetector = javaDetector;
        _gameDirectories = gameDirectories;
        _settings = settings;
        _dialogs = dialogs;
        _taskbar = taskbar;
        _logger = logger;

        ForgeCard = new ComponentCardViewModel(ComponentKind.Forge, "Forge", "大多数整合包与老牌 Mod 使用的加载器");
        NeoForgeCard = new ComponentCardViewModel(ComponentKind.NeoForge, "NeoForge", "Forge 的分支，1.20.1 起的现代版本");
        FabricCard = new ComponentCardViewModel(ComponentKind.Fabric, "Fabric", "轻量、更新快，性能优化 Mod 多选它");
        QuiltCard = new ComponentCardViewModel(ComponentKind.Quilt, "Quilt", "Fabric 的分支，生态较小");
        FabricApiCard = new ComponentCardViewModel(ComponentKind.FabricApi, "Fabric API", "大多数 Fabric Mod 的前置，强烈建议安装");
        OptiFineCard = new ComponentCardViewModel(ComponentKind.OptiFine, "OptiFine", "光影与高清材质；带加载器时作为 Mod 安装");
        OptiFabricCard = new ComponentCardViewModel(ComponentKind.OptiFabric, "OptiFabric", "让 OptiFine 能在 Fabric 上运行");
        LiteLoaderCard = new ComponentCardViewModel(
            ComponentKind.LiteLoader,
            "LiteLoader",
            "1.12.2 及更早的轻量加载器；依赖 launchwrapper，只能用 Java 8 启动");
    }

    public ObservableCollection<ComponentCardViewModel> Cards { get; } = new();

    public ComponentCardViewModel ForgeCard { get; }
    public ComponentCardViewModel NeoForgeCard { get; }
    public ComponentCardViewModel FabricCard { get; }
    public ComponentCardViewModel QuiltCard { get; }
    public ComponentCardViewModel FabricApiCard { get; }
    public ComponentCardViewModel OptiFineCard { get; }
    public ComponentCardViewModel OptiFabricCard { get; }
    public ComponentCardViewModel LiteLoaderCard { get; }

    public bool HasTips => WarningTip is not null || InfoTip is not null;

    public bool HasWarningTip => !string.IsNullOrWhiteSpace(WarningTip);

    public bool HasInfoTip => !string.IsNullOrWhiteSpace(InfoTip);

    /// <summary>当前选中的组件汇总，安装流程直接用这个。</summary>
    public ComponentSelection Selection { get; private set; } = new();

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is VersionSummary summary)
        {
            Version = summary;
        }
        else if (parameter is string id && !string.IsNullOrWhiteSpace(id))
        {
            Version = new VersionSummary { Id = id, Type = "release" };
        }

        if (Version is not null)
        {
            MinecraftVersion = Version.Id;
            VersionSubtitle = BuildSubtitle(Version);

            // 下拉框停在当前版本上，用户也能在页面里换。
            // 先把传进来的这个放进去，列表还没拉过时也不至于显示空白。
            if (!Versions.Any(v => v.Id == Version.Id))
            {
                Versions.Insert(0, Version);
            }

            SelectedVersion = Version;
            await EnsureVersionListAsync();
            await LoadAsync();
            return;
        }

        // 没有带版本进来（从版本页点「新建版本」）：先让用户挑一个版本
        await EnsureVersionListAsync();
        if (SelectedVersion is { } first)
        {
            Version = first;
            MinecraftVersion = first.Id;
            VersionSubtitle = BuildSubtitle(first);
            await LoadAsync();
        }
    }

    /// <summary>
    /// 保证版本下拉里有完整列表。从下载页带版本进来时也要补上，
    /// 否则用户没法在页面里换版本。
    /// </summary>
    private async Task EnsureVersionListAsync()
    {
        if (Versions.Count > 1)
        {
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = null;

            var manifest = await _manifest.GetManifestAsync();
            foreach (var version in manifest.Versions.Where(v => v.Type == "release").Take(200))
            {
                if (Versions.All(v => v.Id != version.Id))
                {
                    Versions.Add(version);
                }
            }

            SelectedVersion ??= Versions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载版本列表失败");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>页面顶部的版本下拉换了之后，按新版本重新拉组件列表。</summary>
    partial void OnSelectedVersionChanged(VersionSummary? value)
    {
        if (value is null || value.Id == MinecraftVersion)
        {
            return;
        }

        Version = value;
        MinecraftVersion = value.Id;
        VersionSubtitle = BuildSubtitle(value);

        // 换了版本，之前选的组件可能不再适用，清空后重来
        foreach (var card in Cards)
        {
            card.Clear();
            card.Options.Clear();
            card.LoadError = null;
        }

        Selection = new ComponentSelection();
        VersionName = "";

        _ = LoadAsync();
    }

    private static string BuildSubtitle(VersionSummary? version, string? fallback = null)
    {
        if (version is null)
        {
            return fallback ?? "选择要安装的组件";
        }

        var type = version.Type switch
        {
            "release" => "正式版",
            "snapshot" => "快照",
            "old_beta" => "远古 Beta",
            "old_alpha" => "远古 Alpha",
            _ => version.Type ?? "",
        };

        return version.ReleaseTime is { } time
            ? $"{type} · 发布于 {time.LocalDateTime:yyyy-MM-dd}"
            : type;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (string.IsNullOrEmpty(MinecraftVersion))
        {
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        try
        {
            IsBusy = true;
            ErrorMessage = null;

            RebuildCards();
            ApplyIsolationDefault();

            // 四个加载器列表互不依赖，并行拉；OptiFine 与 LiteLoader 也一起。
            // Fabric API / OptiFabric 等选中对应加载器后再拉。
            // Java 探测要起子进程，最慢，也放进来并行跑。
            await Task.WhenAll(
                LoadLoaderVersionsAsync(ForgeCard, ModLoader.Forge, ct),
                LoadLoaderVersionsAsync(NeoForgeCard, ModLoader.NeoForge, ct),
                LoadLoaderVersionsAsync(FabricCard, ModLoader.Fabric, ct),
                LoadLoaderVersionsAsync(QuiltCard, ModLoader.Quilt, ct),
                LoadOptiFineAsync(ct),
                LoadLiteLoaderAsync(ct),
                DetectJavaAsync(ct));

            ct.ThrowIfCancellationRequested();
            Refresh();
        }
        catch (OperationCanceledException)
        {
            // 页面被再次导航进来，旧请求作废
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载创建页组件失败");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>卡片顺序与 PCL 一致：原版 → 四个加载器 → Fabric API → OptiFine → OptiFabric → LiteLoader。</summary>
    private void RebuildCards()
    {
        if (Cards.Count > 0)
        {
            return;
        }

        foreach (var card in new[]
                 {
                     ForgeCard, NeoForgeCard, FabricCard, QuiltCard,
                     FabricApiCard, OptiFineCard, OptiFabricCard, LiteLoaderCard,
                 })
        {
            card.PropertyChanged += OnCardPropertyChanged;
            Cards.Add(card);
        }
    }

    /// <summary>
    /// 卡片上的选择一变就重新汇总：清掉冲突项、刷新提示、按需拉依赖它的列表。
    /// 用属性变化事件而不是 XAML 的 SelectionChanged，是因为「清除」按钮
    /// 也会改 <c>Selected</c>，两条路径都要走到同一段逻辑。
    /// </summary>
    private async void OnCardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ComponentCardViewModel.Selected)
            || sender is not ComponentCardViewModel card
            || _suppressRefresh)
        {
            return;
        }

        await OnCardSelectionChangedAsync(card);
    }

    private void ApplyIsolationDefault()
    {
        if (_isolationTouched)
        {
            return;
        }

        // 程序写入要绕过「用户动过开关」的判定，否则默认值会把自己标记成用户选择
        _suppressRefresh = true;
        _applyingIsolation = true;
        try
        {
            // 对齐 PCL：按全局档位 + 版本特征（可装 Mod / 非正式版）给出建议值
            VersionIsolation = VersionIsolationPolicy.ShouldIsolate(
                _settings.Current.DefaultIsolationScope,
                modable: Selection.NeedsIsolation,
                unstable: VersionIsolationPolicy.IsUnstable(Version?.Type, MinecraftVersion));
        }
        finally
        {
            _applyingIsolation = false;
            _suppressRefresh = false;
        }

        UpdateIsolationHint();
    }

    private bool _applyingIsolation;

    /// <summary>
    /// 程序正在写入隔离开关的默认值。页面据此忽略这一次 Toggled 事件——
    /// 否则默认值一设下去就被当成「用户动过了」。
    /// </summary>
    public bool IsApplyingIsolation => _applyingIsolation;

    private async Task LoadLoaderVersionsAsync(
        ComponentCardViewModel card,
        ModLoader loader,
        CancellationToken ct)
    {
        try
        {
            card.IsLoading = true;
            card.LoadError = null;

            var versions = await _loaderVersions.GetVersionsAsync(loader, MinecraftVersion, ct);
            ct.ThrowIfCancellationRequested();

            card.Options.Clear();
            foreach (var version in versions)
            {
                card.Options.Add(version);
            }

            if (versions.Count == 0)
            {
                card.LoadError = $"{loader} 没有支持 Minecraft {MinecraftVersion} 的版本";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取 {Loader} 版本失败", loader);
            card.LoadError = "获取版本列表失败，请检查网络";
        }
        finally
        {
            card.IsLoading = false;
        }
    }

    private async Task LoadOptiFineAsync(CancellationToken ct)
    {
        try
        {
            OptiFineCard.IsLoading = true;
            OptiFineCard.LoadError = null;

            var builds = await _optiFine.GetBuildsAsync(MinecraftVersion, ct);
            ct.ThrowIfCancellationRequested();

            OptiFineCard.Options.Clear();
            foreach (var build in builds)
            {
                OptiFineCard.Options.Add(build);
            }

            if (builds.Count == 0)
            {
                OptiFineCard.LoadError = "该版本没有可用的 OptiFine";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取 OptiFine 版本失败");
            OptiFineCard.LoadError = "获取列表失败，请检查网络";
        }
        finally
        {
            OptiFineCard.IsLoading = false;
        }
    }

    private async Task LoadLiteLoaderAsync(CancellationToken ct)
    {
        try
        {
            LiteLoaderCard.IsLoading = true;
            LiteLoaderCard.LoadError = null;

            var builds = await _liteLoader.GetBuildsAsync(MinecraftVersion, ct);
            ct.ThrowIfCancellationRequested();

            LiteLoaderCard.Options.Clear();
            foreach (var build in builds)
            {
                LiteLoaderCard.Options.Add(build);
            }

            if (builds.Count == 0)
            {
                LiteLoaderCard.LoadError = "该版本没有可用的 LiteLoader";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取 LiteLoader 版本失败");
            LiteLoaderCard.LoadError = "获取列表失败，请检查网络";
        }
        finally
        {
            LiteLoaderCard.IsLoading = false;
        }
    }

    /// <summary>
    /// 探测本机有没有 Java 8。LiteLoader 的 launchwrapper 在 Java 9+ 上会直接崩，
    /// 所以没有 Java 8 时那张卡片要禁用，而不是让用户装完再撞墙。
    /// </summary>
    private async Task DetectJavaAsync(CancellationToken ct)
    {
        try
        {
            var runtimes = await _javaDetector.DetectAsync(ct);
            ct.ThrowIfCancellationRequested();

            _hasJava8 = runtimes.Any(r => r.MajorVersion == 8);
            _java8Probed = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 探测失败按「没有 Java 8」处理：宁可让用户去装，也不要装完启动崩
            _logger.LogWarning(ex, "探测 Java 失败");
            _hasJava8 = false;
            _java8Probed = true;
        }
    }

    /// <summary>本机是否有 Java 8；还没探测完时按有处理，避免卡片闪一下禁用状态。</summary>
    private bool HasJava8 => !_java8Probed || _hasJava8;

    private bool _hasJava8;
    private bool _java8Probed;

    private async Task LoadFabricApiAsync(CancellationToken ct)
    {
        if (FabricApiCard.Options.Count > 0)
        {
            return;
        }

        var loader = Selection.Loader == ModLoader.Quilt ? ModLoader.Quilt : ModLoader.Fabric;

        try
        {
            FabricApiCard.IsLoading = true;
            FabricApiCard.LoadError = null;

            var versions = await _addons.GetFabricApiAsync(MinecraftVersion, loader, ct);
            ct.ThrowIfCancellationRequested();

            FabricApiCard.Options.Clear();
            foreach (var version in versions)
            {
                FabricApiCard.Options.Add(version);
            }

            if (versions.Count == 0)
            {
                FabricApiCard.LoadError = "该版本没有可用的 Fabric API";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取 Fabric API 版本失败");
            FabricApiCard.LoadError = "获取列表失败，请检查网络";
        }
        finally
        {
            FabricApiCard.IsLoading = false;
        }
    }

    private async Task LoadOptiFabricAsync(CancellationToken ct)
    {
        if (OptiFabricCard.Options.Count > 0)
        {
            return;
        }

        try
        {
            OptiFabricCard.IsLoading = true;
            OptiFabricCard.LoadError = null;

            var versions = await _addons.GetOptiFabricAsync(MinecraftVersion, ct);
            ct.ThrowIfCancellationRequested();

            OptiFabricCard.Options.Clear();
            foreach (var version in versions)
            {
                OptiFabricCard.Options.Add(version);
            }

            if (versions.Count == 0)
            {
                OptiFabricCard.LoadError = "该版本没有可用的 OptiFabric";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取 OptiFabric 版本失败");
            OptiFabricCard.LoadError = "获取列表失败，请检查网络";
        }
        finally
        {
            OptiFabricCard.IsLoading = false;
        }
    }

    partial void OnVersionIsolationChanged(bool value)
    {
        UpdateIsolationHint();
    }

    private void UpdateIsolationHint()
    {
        IsolationHint = VersionIsolation
            ? "游戏目录就是该版本的文件夹，存档与模组都在里面。"
            : "与其它未开启隔离的版本共用同一份存档、模组与配置；带 Mod 的版本可能互相冲突。";
    }

    /// <summary>
    /// 任一卡片的选择变化后调用：重建 <see cref="Selection"/>、清掉冲突项、
    /// 刷新各卡片可用性与提示，并重新生成默认版本名。
    /// </summary>
    private void Refresh()
    {
        if (_suppressRefresh)
        {
            return;
        }

        _suppressRefresh = true;
        try
        {
            var selection = new ComponentSelection
            {
                Loader = GetLoader(),
                LoaderVersion = GetLoaderVersion(),
                OptiFine = OptiFineCard.Selected as OptiFineBuild,
                LiteLoader = LiteLoaderCard.Selected as LiteLoaderBuild,
                FabricApi = FabricApiCard.Selected as AddonModVersion,
                OptiFabric = OptiFabricCard.Selected as AddonModVersion,
            };

            var normalized = ComponentRules.Normalize(selection, MinecraftVersion, HasJava8);
            if (!Equals(normalized, selection))
            {
                // 冲突项被清掉后要同步回界面，否则卡片上还显示着已失效的选择
                if (normalized.OptiFine is null)
                {
                    OptiFineCard.Selected = null;
                }

                if (normalized.LiteLoader is null)
                {
                    LiteLoaderCard.Selected = null;
                }

                if (normalized.FabricApi is null)
                {
                    FabricApiCard.Selected = null;
                }

                if (normalized.OptiFabric is null)
                {
                    OptiFabricCard.Selected = null;
                }
            }

            Selection = normalized;

            UpdateCardStates();
            UpdateTips();

            if (string.IsNullOrWhiteSpace(VersionName) || _autoName)
            {
                VersionName = Selection.BuildDefaultName(MinecraftVersion);
                _autoName = true;
            }

            if (!_isolationTouched)
            {
                // 所有档位都实时跟随默认值：切了加载器或版本后，建议值可能已经变了
                ApplyIsolationDefault();
            }
        }
        finally
        {
            _suppressRefresh = false;
        }
    }

    private bool _autoName = true;

    private ModLoader GetLoader()
    {
        if (ForgeCard.HasSelection)
        {
            return ModLoader.Forge;
        }

        if (NeoForgeCard.HasSelection)
        {
            return ModLoader.NeoForge;
        }

        if (FabricCard.HasSelection)
        {
            return ModLoader.Fabric;
        }

        if (QuiltCard.HasSelection)
        {
            return ModLoader.Quilt;
        }

        return ModLoader.Vanilla;
    }

    private string? GetLoaderVersion() => GetLoader() switch
    {
        ModLoader.Forge => (ForgeCard.Selected as LoaderVersion)?.Version,
        ModLoader.NeoForge => (NeoForgeCard.Selected as LoaderVersion)?.Version,
        ModLoader.Fabric => (FabricCard.Selected as LoaderVersion)?.Version,
        ModLoader.Quilt => (QuiltCard.Selected as LoaderVersion)?.Version,
        _ => null,
    };

    private void UpdateCardStates()
    {
        foreach (var card in Cards)
        {
            card.UnavailableReason = ComponentRules.GetUnavailableReason(
                card.Kind, Selection, MinecraftVersion, HasJava8);
        }
    }

    private void UpdateTips()
    {
        WarningTip = Selection switch
        {
            { Loader: ModLoader.Fabric or ModLoader.Quilt, FabricApi: null } =>
                "不安装 Fabric API 时，大多数 Fabric Mod 都无法使用。",
            { Loader: ModLoader.Fabric, OptiFine: not null, OptiFabric: null } =>
                "在 Fabric 上使用 OptiFine 需要同时安装 OptiFabric。",
            { Loader: not ModLoader.Vanilla } when !VersionIsolation =>
                "当前未开启版本隔离，这个版本的 Mod 会与其它共用游戏目录的版本混在一起。",
            _ => null,
        };

        InfoTip = Selection.OptiFineAsMod
            ? "已选择模组加载器，OptiFine 将作为 Mod 放进版本的 mods 目录。"
            : null;
    }

    partial void OnVersionNameChanged(string value)
    {
        // 用户手动改过名字后就不再自动覆盖
        if (!_suppressRefresh && value != Selection.BuildDefaultName(MinecraftVersion))
        {
            _autoName = false;
        }
    }

    /// <summary>
    /// 由页面在隔离开关变化时调用，避免默认值把它覆盖回去。
    /// 程序自己写默认值的那一次会被忽略。
    /// </summary>
    public void MarkIsolationTouched()
    {
        if (!_applyingIsolation)
        {
            _isolationTouched = true;
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (IsCreating || string.IsNullOrEmpty(MinecraftVersion))
        {
            return;
        }

        var loader = Selection.Loader;
        if (loader != ModLoader.Vanilla && Selection.LoaderVersion is null)
        {
            ErrorMessage = $"请先为 {loader} 选择一个版本。";
            return;
        }

        _createCts?.Dispose();
        _createCts = new CancellationTokenSource();
        var ct = _createCts.Token;

        try
        {
            IsCreating = true;
            CreateProgress = 0;
            ErrorMessage = null;
            StatusMessage = null;

            var progress = new Progress<VersionCreateProgress>(p =>
            {
                CreateStatus = p.Message;
                if (p.Ratio > 0)
                {
                    CreateProgress = p.Ratio;
                }

                // 创建版本大部分时间都花在下载上，进度也同步给任务栏
                _taskbar.Report(p.Ratio > 0 ? p.Ratio : null);
            });

            var request = new VersionCreateRequest(
                VersionName,
                MinecraftVersion,
                Selection,
                VersionIsolation);

            var instance = await _creation.CreateAsync(request, progress, ct);

            CreateStatus = "安装完成";
            CreateProgress = 1;
            _taskbar.Flash();
            StatusMessage = $"已创建版本“{instance.Name}”";

            // 由页面负责导航：Frame 在页面手里，服务层的导航器没有接上 Frame
            VersionCreated?.Invoke(this, instance);
        }
        catch (OperationCanceledException)
        {
            CreateStatus = "已取消";
            StatusMessage = "已创建版本但尚未装完，首次启动时会继续安装。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建版本失败");
            ErrorMessage = ex.Message;
            CreateStatus = "安装失败";
            await _dialogs.ShowMessageAsync(
                "安装失败",
                $"{ex.Message}\n\n版本记录已保留，首次启动时会继续安装未完成的部分。");
        }
        finally
        {
            IsCreating = false;
            _taskbar.Reset();
            _createCts?.Dispose();
            _createCts = null;
        }
    }

    [RelayCommand]
    private void CancelCreate() => _createCts?.Cancel();

    /// <summary>卡片「清除」按钮：只清这一张卡。</summary>
    [RelayCommand]
    private void ClearCard(ComponentCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        card.Clear();
        Refresh();
    }

    /// <summary>卡片选择变化后由页面调用。</summary>
    public async Task OnCardSelectionChangedAsync(ComponentCardViewModel card)
    {
        Refresh();

        // Fabric API / OptiFabric 的列表依赖前面的选择，选中后再拉
        if (card == FabricCard || card == QuiltCard)
        {
            if (Selection.Loader is ModLoader.Fabric or ModLoader.Quilt)
            {
                await LoadFabricApiAsync(CancellationToken.None);
            }
        }

        if (card == OptiFineCard || card == FabricCard)
        {
            if (Selection.Loader == ModLoader.Fabric && Selection.OptiFine is not null)
            {
                await LoadOptiFabricAsync(CancellationToken.None);
            }
        }

        Refresh();
    }

    /// <summary>页面用：当前版本实际会用的游戏目录，帮用户理解隔离开关。</summary>
    public string EffectiveGameDirectory
    {
        get
        {
            var preview = new GameVersion
            {
                Id = "preview",
                MinecraftVersion = MinecraftVersion,
                VersionIsolation = VersionIsolation,
            };

            return _gameDirectories.ResolveGameDirectory(preview.Id, preview.VersionIsolation);
        }
    }

    /// <summary>创建成功后由页面导航到版本列表。</summary>
    public event EventHandler<GameVersion>? VersionCreated;

    /// <summary>返回上一页；页面自己处理导航。</summary>
    public event EventHandler? BackRequested;

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>供页面在离开时取消进行中的请求。</summary>
    public void CancelPending()
    {
        _loadCts?.Cancel();
        _createCts?.Cancel();
    }
}
