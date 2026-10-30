using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Services;
using Solar.Core.Paths;
using Solar.Core.Versions;

namespace Solar.App.ViewModels;

/// <summary>
/// 单个版本的设置页。目前只有「版本隔离」，后续的 Java 路径、内存覆盖也放这里。
/// 导航参数是版本 Id。
/// </summary>
public sealed partial class VersionSetupViewModel : ViewModelBase
{
    private readonly IVersionRepository _repository;
    private readonly MinecraftPaths _gameDirectories;
    private readonly IDialogService _dialogs;
    private readonly ILogger<VersionSetupViewModel> _logger;

    private GameVersion? _instance;

    /// <summary>
    /// 页面被 <c>NavigationCacheMode.Required</c> 缓存复用，切换版本时页面参数变化不会重建页面，
    /// 所以由页面在 <c>OnNavigatedTo</c> 里显式写入，再触发 <see cref="OnNavigatedToAsync"/> 加载。
    /// </summary>
    public string? VersionId { get; set; }

    [ObservableProperty]
    private string _VersionName = "";

    [ObservableProperty]
    private string _VersionSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveGameDirectory))]
    [NotifyPropertyChangedFor(nameof(IsolationHint))]
    private bool _versionIsolation;

    [ObservableProperty]
    private string _customWindowTitle = "";

    public VersionSetupViewModel(
        IVersionRepository repository,
        MinecraftPaths gameDirectories,
        IDialogService dialogs,
        ILogger<VersionSetupViewModel> logger)
    {
        _repository = repository;
        _gameDirectories = gameDirectories;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>该版本实际会用的游戏目录（覆盖优先，其次是隔离目录，最后是共享目录）。</summary>
    public string EffectiveGameDirectory => _instance is null
        ? ""
        : _gameDirectories.ResolveGameDirectory(_instance.Id, _instance.VersionIsolation);

    public string IsolationHint => VersionIsolation
        ? "游戏目录就是该版本的文件夹，存档与模组都在里面。"
        : "与其它未开启隔离的版本共用同一份存档、模组与配置，多个带 Mod 的版本可能互相冲突。";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var versionId = parameter as string ?? VersionId;
        if (string.IsNullOrEmpty(versionId))
        {
            ErrorMessage = "没有指定要设置的版本。";
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = null;
            StatusMessage = null;

            _instance = await _repository.GetAsync(versionId);
            if (_instance is null)
            {
                ErrorMessage = $"找不到版本 {versionId}，它可能已被删除。";
                return;
            }

            VersionName = _instance.Name;
            VersionSummary = $"{_instance.MinecraftVersion} · {_instance.DisplayLoader}";
            VersionIsolation = _instance.VersionIsolation;
            CustomWindowTitle = _instance.CustomWindowTitle ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载版本设置失败");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_instance is null)
        {
            return;
        }

        var wasIsolated = _instance.VersionIsolation;

        try
        {
            IsBusy = true;
            ErrorMessage = null;
            StatusMessage = null;

            // 关闭隔离不搬文件：版本文件夹原样留着，下次再开启还在。
            // 开启时才问一次要不要把共享目录的现有内容带过来。
            CopySummary? copyResult = null;
            if (!wasIsolated && VersionIsolation)
            {
                if (await _dialogs.ShowConfirmAsync(
                        "开启版本隔离",
                        "是否把 MC 文件夹根下的 Mod、存档与配置复制到该版本文件夹？\n" +
                        "复制不会改动共享目录，已存在的同名文件也不会被覆盖。\n" +
                        "选择「不复制」则该版本从版本文件夹的现有内容开始。",
                        "复制",
                        "不复制"))
                {
                    // 可能有上千个文件，放到后台线程避免卡住界面
                    copyResult = await Task.Run(() => VersionContent.CopySharedContent(
                        _gameDirectories.MinecraftRoot,
                        _gameDirectories.GetVersionDirectory(_instance.Id)));
                }
            }

            _instance.VersionIsolation = VersionIsolation;
            // 留空表示不自定义，恢复默认标题
            _instance.CustomWindowTitle = string.IsNullOrWhiteSpace(CustomWindowTitle) ? null : CustomWindowTitle.Trim();
            await _repository.SaveSettingsAsync(_instance);

            StatusMessage = copyResult is { } summary
                ? $"已保存，并复制了 {summary.CopiedFiles} 个文件（跳过已存在的 {summary.SkippedFiles} 个）。"
                : "设置已保存";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存版本设置失败");
            await _dialogs.ShowMessageAsync("保存失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
