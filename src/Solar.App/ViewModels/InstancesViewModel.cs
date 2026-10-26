using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Services;
using Solar.Core.Instances;

namespace Solar.App.ViewModels;

/// <summary>下拉框选项：值 + 显示名。</summary>
public sealed record ModLoaderOption(ModLoader Value, string Display);

public sealed partial class InstancesViewModel : ViewModelBase
{
    private readonly IInstanceRepository _repository;
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

    public InstancesViewModel(
        IInstanceRepository repository,
        IDialogService dialogs,
        ILogger<InstancesViewModel> logger)
    {
        _repository = repository;
        _dialogs = dialogs;
        _logger = logger;
        _newInstanceLoader = Loaders[0];
    }

    public ObservableCollection<GameInstance> Instances { get; } = new();

    public IReadOnlyList<ModLoaderOption> Loaders { get; } =
    [
        new(ModLoader.Vanilla, "原版"),
        new(ModLoader.Forge, "Forge"),
        new(ModLoader.NeoForge, "NeoForge"),
        new(ModLoader.Fabric, "Fabric"),
        new(ModLoader.Quilt, "Quilt"),
    ];

    public override Task OnNavigatedToAsync(object? parameter) => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;

            var items = await _repository.GetAllAsync();
            Instances.Clear();
            foreach (var item in items)
            {
                Instances.Add(item);
            }

            SelectedInstance = Instances.FirstOrDefault();
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

    [RelayCommand]
    private async Task CreateAsync()
    {
        var name = string.IsNullOrWhiteSpace(NewInstanceName) ? "新实例" : NewInstanceName.Trim();
        var version = string.IsNullOrWhiteSpace(NewInstanceVersion) ? "1.21.4" : NewInstanceVersion.Trim();

        var instance = new GameInstance
        {
            Name = name,
            MinecraftVersion = version,
            Loader = NewInstanceLoader.Value,
        };

        try
        {
            await _repository.SaveAsync(instance);
            NewInstanceName = "";
            await RefreshAsync();
            SelectedInstance = Instances.FirstOrDefault(i => i.Id == instance.Id);
            StatusMessage = $"已创建实例“{instance.Name}”";
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

        if (!await _dialogs.ShowConfirmAsync("删除实例", $"确定要删除“{instance.Name}”吗？该实例的游戏目录会被一并删除。", "删除", "取消"))
        {
            return;
        }

        try
        {
            await _repository.DeleteAsync(instance.Id);
            await RefreshAsync();
            StatusMessage = $"已删除实例“{instance.Name}”";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除实例失败");
            await _dialogs.ShowMessageAsync("删除失败", ex.Message);
        }
    }
}
