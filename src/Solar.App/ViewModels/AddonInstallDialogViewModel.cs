using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Solar.App.Formatting;
using Solar.App.Services;
using Solar.Core.Download;
using Solar.Core.Instances;
using Solar.Core.Mods;

namespace Solar.App.ViewModels;

/// <summary>安装清单里的一行。前置默认勾选，已经装过的标出来但仍列出，让用户知道为什么只下了两个文件。</summary>
public sealed partial class AddonInstallItemViewModel(ModInstallItem item) : ObservableObject
{
    public ModInstallItem Item { get; } = item;

    public string Title => Item.Project.Title;

    public string VersionText => Item.Version is { } version
        ? $"{version.VersionNumber} · {ModDisplay.FileSize(version.File.Size)}"
        : "找不到兼容版本";

    /// <summary>
    /// 找不到兼容版本的项不允许勾选，装上去只会崩。
    /// 已安装的项本身是可选中的——用户还能勾上「覆盖已存在的同名文件」把它重下一遍，
    /// 所以勾不勾由对话框在覆盖开关变化时决定。
    /// </summary>
    public bool IsSelectable => Item.IsAvailable;

    public string StateText => !Item.IsAvailable
        ? "没有适配当前实例的版本"
        : Item.AlreadyInstalled
            ? WillOverwrite ? "已安装，将被覆盖" : "已安装，将跳过"
            : "";

    public bool HasStateText => StateText.Length > 0;

    /// <summary>用户勾了「覆盖已存在的同名文件」时为 true，只影响已安装项的提示文案。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _willOverwrite;

    /// <summary>
    /// 默认勾选：可安装且还没装过。已安装的项不勾，但用户打开「覆盖」开关后会被自动勾上。
    /// 勾选变化由对话框转给视图模型，这里不直接订阅。
    /// </summary>
    [ObservableProperty]
    private bool _isChecked = item.IsAvailable && !item.AlreadyInstalled;

    /// <summary>打开覆盖开关时，已安装的项也要能参与下载。</summary>
    public void SyncWithOverwrite(bool overwrite)
    {
        if (Item.AlreadyInstalled)
        {
            IsChecked = overwrite && Item.IsAvailable;
        }
    }
}

/// <summary>
/// 安装对话框的视图模型：生成清单、展示进度、执行安装。
/// <para>
/// Mod 会展开必选前置，其余类型只有文件本身；数据包还需要先选一个存档。
/// 目标目录由 <see cref="IModInstaller.GetContentDirectory"/> 统一决定，
/// 保证开启版本隔离的实例写进自己的游戏目录。
/// </para>
/// </summary>
public sealed partial class AddonInstallDialogViewModel : ViewModelBase
{
    private readonly IModCatalogService _catalog;
    private readonly IModInstaller _installer;
    private readonly TaskbarProgressService _taskbar;

    private ModProject? _project;
    private ModVersion? _version;
    private GameInstance? _instance;
    private CancellationTokenSource? _cts;

    /// <summary>选中存档时先改目录再重建清单，避免重建过程中触发第二次重建。</summary>
    private bool _updatingWorld;

    [ObservableProperty]
    private string _targetDirectory = "";

    [ObservableProperty]
    private bool _overwriteExisting;

    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>对话框标题。清单是异步拉的，标题用 OneWay 绑定，必须是可观察属性。</summary>
    [ObservableProperty]
    private string _header = "";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _installText = "安装";

    [ObservableProperty]
    private bool _canInstall;

    [ObservableProperty]
    private string _typeLabel = "";

    /// <summary>数据包的目标存档。其它类型不显示这个下拉框。</summary>
    public ObservableCollection<string> Worlds { get; } = new();

    [ObservableProperty]
    private string? _selectedWorld;

    /// <summary>共享目录 / 原版实例这类会影响别人的情况，在最靠近写文件的地方再提醒一次。</summary>
    public string? TargetWarning { get; private set; }

    public bool HasTargetWarning => !string.IsNullOrWhiteSpace(TargetWarning);

    public bool IsDataPack => Type == ModProjectType.DataPack;

    public bool HasWorlds => Worlds.Count > 0;

    public ObservableCollection<AddonInstallItemViewModel> Items { get; } = new();

    /// <summary>安装完成后的结果，页面据此显示状态。</summary>
    public ModInstallResult? Result { get; private set; }

    public ModProjectType Type => _project?.ProjectType ?? ModProjectType.Mod;

    public AddonInstallDialogViewModel(
        IModCatalogService catalog,
        IModInstaller installer,
        TaskbarProgressService taskbar)
    {
        _catalog = catalog;
        _installer = installer;
        _taskbar = taskbar;
    }

    public async Task LoadAsync(ModProject project, ModVersion version, GameInstance instance)
    {
        _project = project;
        _version = version;
        _instance = instance;

        TypeLabel = project.ProjectType.DisplayName();
        Header = $"安装 {project.Title} {version.VersionNumber}";

        _updatingWorld = true;
        try
        {
            if (project.ProjectType == ModProjectType.DataPack)
            {
                Worlds.Clear();
                foreach (var world in _installer.GetWorlds(instance))
                {
                    Worlds.Add(world);
                }

                SelectedWorld = Worlds.FirstOrDefault();
                OnPropertyChanged(nameof(HasWorlds));
            }
        }
        finally
        {
            _updatingWorld = false;
        }

        UpdateTargetDirectory();
        UpdateTargetWarning();

        await RebuildPlanAsync();
    }

    partial void OnSelectedWorldChanged(string? value)
    {
        UpdateTargetDirectory();

        if (!_updatingWorld)
        {
            _ = RebuildPlanAsync();
        }
    }

    /// <summary>勾选变化后重新计算按钮文案与「实际要下几个文件」。</summary>
    public void RefreshSummary()
    {
        var pending = PendingItems.Count();

        // 数据包没有存档时无论勾了什么都不能装
        var blocked = IsDataPack && string.IsNullOrWhiteSpace(SelectedWorld);

        CanInstall = pending > 0 && !IsInstalling && !blocked;
        InstallText = blocked
            ? "先选择一个存档"
            : pending switch
            {
                0 => (Items.Count > 0 && Items.All(i => i.Item.AlreadyInstalled))
                    ? "文件均已安装"
                    : "没有可安装的文件",
                1 => "安装 1 个文件",
                _ => $"安装 {pending} 个文件",
            };

        // 全部装过时把「覆盖」开关的用途点出来，否则用户会以为对话框坏了
        OverwriteHint = Items.Count > 0 && Items.All(i => i.Item.AlreadyInstalled)
            ? "这个版本的文件已经在实例里了。勾选「覆盖已存在的同名文件」可以重新下载。"
            : "";
        OnPropertyChanged(nameof(OverwriteHint));
        OnPropertyChanged(nameof(HasOverwriteHint));
    }

    public string OverwriteHint { get; private set; } = "";

    public bool HasOverwriteHint => OverwriteHint.Length > 0;

    public async Task<bool> InstallAsync()
    {
        if (_project is null || _instance is null || !CanInstall)
        {
            return false;
        }

        _cts = new CancellationTokenSource();

        try
        {
            IsInstalling = true;
            ErrorMessage = null;
            CanInstall = false;
            ProgressValue = 0;

            var plan = BuildSelectedPlan();
            var total = plan.Pending.Count();

            var progress = new Progress<DownloadProgress>(p =>
            {
                ProgressValue = total == 0 ? 0 : (double)p.CompletedFiles / total;
                ProgressText = string.IsNullOrEmpty(p.CurrentFile)
                    ? $"已完成 {p.CompletedFiles}/{total}"
                    : $"正在安装 {p.CompletedFiles + 1}/{total}：{p.CurrentFile}";

                _taskbar.Report(total == 0 ? null : (double)p.CompletedFiles / total);
            });

            Result = await _installer.InstallAsync(
                _instance,
                Type,
                plan,
                SelectedWorld,
                OverwriteExisting,
                progress,
                _cts.Token);

            ProgressValue = 1;
            ProgressText = $"完成：安装 {Result.Installed} 个，跳过 {Result.Skipped} 个";
            _taskbar.Flash();
            return true;
        }
        catch (OperationCanceledException)
        {
            ProgressText = "已取消";
            ErrorMessage = "安装已取消。已经下完的文件会保留。";
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            ProgressText = "";
            return false;
        }
        finally
        {
            IsInstalling = false;
            _taskbar.Reset();
            RefreshSummary();
        }
    }

    /// <summary>用户点「取消」或关掉对话框：中止正在进行的下载。</summary>
    public void Cancel() => _cts?.Cancel();

    private void UpdateTargetDirectory()
    {
        if (_instance is null || Type.CreatesInstance())
        {
            TargetDirectory = "";
            return;
        }

        try
        {
            TargetDirectory = IsDataPack && string.IsNullOrWhiteSpace(SelectedWorld)
                ? "先选择一个存档"
                : _installer.GetContentDirectory(_instance, Type, SelectedWorld);
        }
        catch (Exception ex)
        {
            TargetDirectory = ex.Message;
        }
    }

    private void UpdateTargetWarning()
    {
        if (_instance is null)
        {
            TargetWarning = null;
        }
        else
        {
            TargetWarning = Type switch
            {
                ModProjectType.Mod => !_instance.VersionIsolation
                    ? "这个实例没有开启版本隔离，文件会装进共享的游戏目录，所有未隔离的实例都会看到它。"
                    : _instance.Loader == ModLoader.Vanilla
                        ? "这个实例是原版，没有加载器。Mod 装进去也不会被加载。"
                        : null,

                ModProjectType.Shader =>
                    "光影需要 Iris 或 OptiFine 才能生效；装好后在游戏的「选项 → 光影」里启用。",

                ModProjectType.ResourcePack =>
                    "资源包装好后，在游戏的「选项 → 资源包」里启用。",

                ModProjectType.DataPack => Worlds.Count == 0
                    ? "这个实例还没有存档。请先在游戏里创建一个世界，再回来安装数据包。"
                    : "数据包只对选中的存档生效。",

                _ => null,
            };
        }

        OnPropertyChanged(nameof(TargetWarning));
        OnPropertyChanged(nameof(HasTargetWarning));
    }

    private async Task RebuildPlanAsync()
    {
        Items.Clear();

        if (_project is not { } project || _version is not { } version || _instance is not { } instance)
        {
            RefreshSummary();
            return;
        }

        // 数据包没有存档时无法判断“是否已安装”，清单仍然列出来，但按钮会被禁用
        var world = IsDataPack ? SelectedWorld : null;

        try
        {
            IsBusy = true;
            ErrorMessage = null;

            var plan = await _catalog.BuildInstallPlanAsync(
                project,
                version,
                instance.MinecraftVersion,
                project.ProjectType.UsesLoader() ? instance.Loader : null);

            foreach (var item in plan.Items)
            {
                // 聚合层不知道目标实例，这里补上「是否已存在」的判断
                var installed = item.Version is { } v
                                && _installer.IsInstalled(instance, project.ProjectType, v.File, world);
                Items.Add(new AddonInstallItemViewModel(item with { AlreadyInstalled = installed })
                {
                    WillOverwrite = OverwriteExisting,
                });
            }
        }
        catch (Exception ex)
        {
            // 前置查不到时仍然允许只装主文件：清单里保留目标版本这一项
            ErrorMessage = "解析前置失败，只会安装选中的文件本身：" + ex.Message;
            Items.Clear();
            Items.Add(new AddonInstallItemViewModel(new ModInstallItem(
                project,
                version,
                ModDependencyKind.Required,
                _installer.IsInstalled(instance, project.ProjectType, version.File, world),
                IsAvailable: true)));
        }
        finally
        {
            IsBusy = false;
            RefreshSummary();
        }
    }

    partial void OnOverwriteExistingChanged(bool value)
    {
        // 已安装的项默认不勾；打开「覆盖」后它们才变成待下载
        foreach (var item in Items)
        {
            item.WillOverwrite = value;
            item.SyncWithOverwrite(value);
        }

        RefreshSummary();
    }

    private IEnumerable<AddonInstallItemViewModel> PendingItems =>
        Items.Where(i => i.IsChecked && i.IsSelectable);

    /// <summary>只把用户勾上的项交给安装器。聚合层给的清单是全集，勾选是这里的事。</summary>
    private ModInstallPlan BuildSelectedPlan() =>
        new(PendingItems.Select(i => i.Item).ToList());
}
