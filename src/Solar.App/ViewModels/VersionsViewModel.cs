using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Services;
using Solar.Core.Versions;

namespace Solar.App.ViewModels;

/// <summary>
/// 版本列表页。新建版本不在这里做——统一走 <see cref="VersionCreateViewModel"/>
/// 的创建页，那边能选版本、加载器与附加组件并一次装完。
/// </summary>
public sealed partial class VersionsViewModel : ViewModelBase
{
    private readonly IVersionRepository _repository;
    private readonly IDialogService _dialogs;
    private readonly ILogger<VersionsViewModel> _logger;

    [ObservableProperty]
    private GameVersion? _SelectedVersion;

    public VersionsViewModel(
        IVersionRepository repository,
        IDialogService dialogs,
        ILogger<VersionsViewModel> logger)
    {
        _repository = repository;
        _dialogs = dialogs;
        _logger = logger;
    }

    public ObservableCollection<GameVersion> Versions { get; } = new();

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
            _logger.LogError(ex, "加载版本失败");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 重新扫描 MC 文件夹里的版本。版本列表本身就是扫描结果，没有导入这一步。
    /// </summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            StatusMessage = null;

            await ReloadAsync();
            StatusMessage = $"共 {Versions.Count} 个版本";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "扫描 MC 文件夹失败");
            await _dialogs.ShowMessageAsync("扫描失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>重新读取版本列表，保留当前选中的那个。</summary>
    private async Task ReloadAsync()
    {
        var previousId = SelectedVersion?.Id;

        var items = await _repository.GetAllAsync();
        Versions.Clear();
        foreach (var item in items)
        {
            Versions.Add(item);
        }

        SelectedVersion = Versions.FirstOrDefault(i => i.Id == previousId)
                           ?? Versions.FirstOrDefault();
    }

    [RelayCommand]
    private async Task DeleteAsync(GameVersion? instance)
    {
        if (instance is null)
        {
            return;
        }

        // 与 PCL 一致：删除版本就是删除整个版本文件夹。
        // 关闭隔离的版本，存档与模组在共享 MC 文件夹里，不受影响。
        var detail = instance.VersionIsolation
            ? "会删除整个版本文件夹，其中的存档、模组与配置都会一起消失。"
            : "会删除版本文件夹；存档与模组在共享的 MC 文件夹里，不会被删除。";

        if (!await _dialogs.ShowConfirmAsync("删除版本", $"确定要删除“{instance.Name}”吗？\n{detail}", "删除", "取消"))
        {
            return;
        }

        try
        {
            await _repository.DeleteAsync(instance.Id);
            await ReloadAsync();
            StatusMessage = $"已删除版本“{instance.Name}”";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除版本失败");
            await _dialogs.ShowMessageAsync("删除失败", ex.Message);
        }
    }

}
