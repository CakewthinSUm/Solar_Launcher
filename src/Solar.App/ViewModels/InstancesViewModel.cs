using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Services;
using Solar.Core.Instances;

namespace Solar.App.ViewModels;

/// <summary>
/// 实例列表页。新建实例不在这里做——统一走 <see cref="InstanceCreateViewModel"/>
/// 的创建页，那边能选版本、加载器与附加组件并一次装完。
/// </summary>
public sealed partial class InstancesViewModel : ViewModelBase
{
    private readonly IInstanceRepository _repository;
    private readonly IVersionScanner _scanner;
    private readonly IDialogService _dialogs;
    private readonly ILogger<InstancesViewModel> _logger;

    [ObservableProperty]
    private GameInstance? _selectedInstance;

    public InstancesViewModel(
        IInstanceRepository repository,
        IVersionScanner scanner,
        IDialogService dialogs,
        ILogger<InstancesViewModel> logger)
    {
        _repository = repository;
        _scanner = scanner;
        _dialogs = dialogs;
        _logger = logger;
    }

    public ObservableCollection<GameInstance> Instances { get; } = new();

    public override Task OnNavigatedToAsync(object? parameter) => ScanAsync();

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
