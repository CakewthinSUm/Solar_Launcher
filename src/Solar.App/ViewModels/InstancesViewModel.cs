using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Services;
using Solar.Core.Instances;
using Solar.Core.Loaders;

namespace Solar.App.ViewModels;

/// <summary>下拉框选项：值 + 显示名。</summary>
public sealed record ModLoaderOption(ModLoader Value, string Display);

public sealed partial class InstancesViewModel : ViewModelBase
{
    private readonly IInstanceRepository _repository;
    private readonly IVersionScanner _scanner;
    private readonly ILoaderVersionService _loaderVersions;
    private readonly IDialogService _dialogs;
    private readonly ILogger<InstancesViewModel> _logger;

    [ObservableProperty]
    private GameInstance? _selectedInstance;

    [ObservableProperty]
    private string _newInstanceName = "";

    [ObservableProperty]
    private string _newInstanceVersion = "1.21.4";

    [ObservableProperty]
    private ModLoaderOption _newInstanceLoader;

    [ObservableProperty]
    private LoaderVersion? _selectedLoaderVersion;

    [ObservableProperty]
    private bool _isLoadingLoaderVersions;

    [ObservableProperty]
    private string _loaderVersionHint = "";

    public InstancesViewModel(
        IInstanceRepository repository,
        IVersionScanner scanner,
        ILoaderVersionService loaderVersions,
        IDialogService dialogs,
        ILogger<InstancesViewModel> logger)
    {
        _repository = repository;
        _scanner = scanner;
        _loaderVersions = loaderVersions;
        _dialogs = dialogs;
        _logger = logger;
        _newInstanceLoader = Loaders[0];
    }

    public ObservableCollection<GameInstance> Instances { get; } = new();

    public ObservableCollection<LoaderVersion> LoaderVersions { get; } = new();

    public IReadOnlyList<ModLoaderOption> Loaders { get; } =
    [
        new(ModLoader.Vanilla, "原版"),
        new(ModLoader.Fabric, "Fabric"),
        new(ModLoader.NeoForge, "NeoForge"),
        new(ModLoader.Forge, "Forge"),
        new(ModLoader.Quilt, "Quilt"),
    ];

    /// <summary>原版不需要加载器版本，选择框应隐藏。</summary>
    public bool NeedsLoaderVersion => NewInstanceLoader.Value != ModLoader.Vanilla;

    public override Task OnNavigatedToAsync(object? parameter) => ScanAsync();

    partial void OnNewInstanceLoaderChanged(ModLoaderOption value)
    {
        OnPropertyChanged(nameof(NeedsLoaderVersion));
        _ = RefreshLoaderVersionsAsync();
    }

    partial void OnNewInstanceVersionChanged(string value)
    {
        if (NeedsLoaderVersion)
        {
            _ = RefreshLoaderVersionsAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载实例失败");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 扫描游戏目录（.minecraft）里已有的版本并导入。
    /// 通常在进入实例页时自动跑一次，也可以手动点「扫描游戏目录」。
    /// </summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            StatusMessage = null;

            var imported = await _scanner.ImportAsync();
            await ReloadAsync();

            StatusMessage = imported.Count > 0
                ? $"已从游戏目录导入 {imported.Count} 个版本"
                : "游戏目录里没有新的版本";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "扫描游戏目录失败");
            await _dialogs.ShowMessageAsync("扫描失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>重新读取实例列表，保留当前选中的那个。</summary>
    private async Task ReloadAsync()
    {
        var previousId = SelectedInstance?.Id;

        var items = await _repository.GetAllAsync();
        Instances.Clear();
        foreach (var item in items)
        {
            Instances.Add(item);
        }

        SelectedInstance = Instances.FirstOrDefault(i => i.Id == previousId)
                           ?? Instances.FirstOrDefault();
    }

    /// <summary>按当前选中的加载器与游戏版本拉取可选加载器版本。</summary>
    [RelayCommand]
    private async Task RefreshLoaderVersionsAsync()
    {
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;

        if (!NeedsLoaderVersion)
        {
            LoaderVersionHint = "";
            return;
        }

        var loader = NewInstanceLoader.Value;
        var minecraftVersion = string.IsNullOrWhiteSpace(NewInstanceVersion)
            ? "1.21.4"
            : NewInstanceVersion.Trim();

        try
        {
            IsLoadingLoaderVersions = true;
            LoaderVersionHint = "正在获取可用版本…";

            var versions = await _loaderVersions.GetVersionsAsync(loader, minecraftVersion);
            foreach (var version in versions)
            {
                LoaderVersions.Add(version);
            }

            SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
            LoaderVersionHint = versions.Count == 0
                ? $"{loader} 没有支持 Minecraft {minecraftVersion} 的版本"
                : $"共 {versions.Count} 个版本";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取加载器版本失败");
            LoaderVersionHint = "获取版本列表失败，请检查网络后重试";
        }
        finally
        {
            IsLoadingLoaderVersions = false;
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var name = string.IsNullOrWhiteSpace(NewInstanceName) ? "新实例" : NewInstanceName.Trim();
        var version = string.IsNullOrWhiteSpace(NewInstanceVersion) ? "1.21.4" : NewInstanceVersion.Trim();
        var loader = NewInstanceLoader.Value;

        // 选了加载器就必须有版本，否则首次启动会失败
        if (loader != ModLoader.Vanilla && SelectedLoaderVersion is null)
        {
            ErrorMessage = $"请先为 {NewInstanceLoader.Display} 选择一个版本。";
            return;
        }

        var instance = new GameInstance
        {
            Name = name,
            MinecraftVersion = version,
            Loader = loader,
            LoaderVersion = loader == ModLoader.Vanilla ? null : SelectedLoaderVersion?.Version,
            LoaderInstalled = loader == ModLoader.Vanilla,
        };

        try
        {
            await _repository.SaveAsync(instance);
            NewInstanceName = "";
            await ReloadAsync();
            SelectedInstance = Instances.FirstOrDefault(i => i.Id == instance.Id);
            StatusMessage = $"已创建实例“{instance.Name}”" +
                            (loader == ModLoader.Vanilla ? "" : $"，首次启动时会安装 {instance.DisplayLoader}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建实例失败");
            await _dialogs.ShowMessageAsync("创建失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(GameInstance? instance)
    {
        if (instance is null)
        {
            return;
        }

        // 未隔离的实例不拥有游戏目录，删它只清掉记录与 natives；
        // 隔离实例的游戏目录在实例文件夹里，会跟着一起删。
        var detail = instance.VersionIsolation
            ? "该实例已开启版本隔离，它的存档与模组会被一并删除。"
            : "存档与模组在共享的游戏目录里，不会被删除。";

        if (!await _dialogs.ShowConfirmAsync("删除实例", $"确定要删除“{instance.Name}”吗？\n{detail}", "删除", "取消"))
        {
            return;
        }

        try
        {
            await _repository.DeleteAsync(instance.Id);
            await ReloadAsync();
            StatusMessage = $"已删除实例“{instance.Name}”";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除实例失败");
            await _dialogs.ShowMessageAsync("删除失败", ex.Message);
        }
    }
}
