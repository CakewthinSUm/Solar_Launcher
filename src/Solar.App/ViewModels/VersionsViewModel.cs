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

    /// <summary>扫描、收藏与删除串行执行，避免写设置和重新扫描互相穿插。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>扫描结果的扁平列表；重新分组与收藏都基于它，避免重复扫描磁盘。</summary>
    private readonly List<GameVersion> _allVersions = new();

    public VersionsViewModel(
        IVersionRepository repository,
        IDialogService dialogs,
        ILogger<VersionsViewModel> logger)
    {
        _repository = repository;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>
    /// 三个分组对象在页面生命周期内保持不变，只更新内容。
    /// 这样收藏一个版本时，只有来源组和目标组会重建行，展开状态和滚动位置都不会丢。
    /// </summary>
    public IReadOnlyList<VersionGroupViewModel> Groups { get; } =
    [
        new VersionGroupViewModel(VersionGroupKind.Favorites),
        new VersionGroupViewModel(VersionGroupKind.Modable),
        new VersionGroupViewModel(VersionGroupKind.Regular),
    ];

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

            await ReloadAsync();
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

    private async Task ReloadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await ReloadCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReloadCoreAsync()
    {
        var items = await _repository.GetAllAsync();

        _allVersions.Clear();
        _allVersions.AddRange(items);

        RebuildGroups();
    }

    /// <summary>把最新的扫描结果分配到三个固定分组里；空组由分组自己收起。</summary>
    private void RebuildGroups()
    {
        var groups = VersionGrouping.Group(_allVersions);

        foreach (var group in Groups)
        {
            var versions = groups.FirstOrDefault(g => g.Kind == group.Kind)?.Versions
                           ?? Array.Empty<GameVersion>();
            group.Apply(versions);
        }
    }

    /// <summary>
    /// 列表里的心形按钮：切换收藏并立即写盘。
    /// 先移动条目再保存，保存失败就移回去并弹窗提示。
    /// </summary>
    [RelayCommand]
    private async Task ToggleFavoriteAsync(GameVersion? instance)
    {
        if (instance is null)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            var original = instance.IsFavorite;
            instance.IsFavorite = !original;

            RebuildGroups();

            try
            {
                await _repository.SaveSettingsAsync(instance);
            }
            catch (Exception ex)
            {
                instance.IsFavorite = original;
                RebuildGroups();

                _logger.LogError(ex, "保存版本收藏状态失败");
                await _dialogs.ShowMessageAsync("收藏失败", ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
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

        await _gate.WaitAsync();
        try
        {
            await _repository.DeleteAsync(instance.Id);
            await ReloadCoreAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除版本失败");
            await _dialogs.ShowMessageAsync("删除失败", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

}
