using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Services;
using Solar.Core.Instances;

namespace Solar.App.ViewModels;

/// <summary>
/// 单个实例的设置页。目前只有「版本隔离」，后续的 Java 路径、内存覆盖也放这里。
/// 导航参数是实例 Id。
/// </summary>
public sealed partial class InstanceSetupViewModel : ViewModelBase
{
    private readonly IInstanceRepository _repository;
    private readonly InstanceGameDirectory _gameDirectories;
    private readonly IDialogService _dialogs;
    private readonly ILogger<InstanceSetupViewModel> _logger;

    private GameInstance? _instance;

    /// <summary>
    /// 页面被 <c>NavigationCacheMode.Required</c> 缓存复用，切换实例时页面参数变化不会重建页面，
    /// 所以由页面在 <c>OnNavigatedTo</c> 里显式写入，再触发 <see cref="OnNavigatedToAsync"/> 加载。
    /// </summary>
    public string? InstanceId { get; set; }

    [ObservableProperty]
    private string _instanceName = "";

    [ObservableProperty]
    private string _instanceSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveGameDirectory))]
    [NotifyPropertyChangedFor(nameof(IsolationHint))]
    private bool _versionIsolation;

    [ObservableProperty]
    private string _customWindowTitle = "";

    public InstanceSetupViewModel(
        IInstanceRepository repository,
        InstanceGameDirectory gameDirectories,
        IDialogService dialogs,
        ILogger<InstanceSetupViewModel> logger)
    {
        _repository = repository;
        _gameDirectories = gameDirectories;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>开关的实际效果：开启时是实例自己的目录，关闭时是共享目录。</summary>
    public string EffectiveGameDirectory => _instance is null
        ? ""
        : VersionIsolation
            ? _gameDirectories.GetIsolatedPath(_instance.Id)
            : _gameDirectories.Shared;

    public string IsolationHint => VersionIsolation
        ? "该实例使用独立的存档、模组与配置，不会读到其它实例的内容。"
        : "与其它未开启隔离的实例共用同一份存档、模组与配置，多个带 Mod 的实例可能互相冲突。";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var instanceId = parameter as string ?? InstanceId;
        if (string.IsNullOrEmpty(instanceId))
        {
            ErrorMessage = "没有指定要设置的实例。";
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = null;
            StatusMessage = null;

            _instance = await _repository.GetAsync(instanceId);
            if (_instance is null)
            {
                ErrorMessage = $"找不到实例 {instanceId}，它可能已被删除。";
                return;
            }

            InstanceName = _instance.Name;
            InstanceSummary = $"{_instance.MinecraftVersion} · {_instance.DisplayLoader}";
            VersionIsolation = _instance.VersionIsolation;
            CustomWindowTitle = _instance.CustomWindowTitle ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载实例设置失败");
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

            // 关闭隔离不搬文件：实例目录原样留着，下次再开启还在。
            // 开启时才问一次要不要把共享目录的现有内容带过来。
            CopySummary? copyResult = null;
            if (!wasIsolated && VersionIsolation)
            {
                if (await _dialogs.ShowConfirmAsync(
                        "开启版本隔离",
                        "是否把共享游戏目录里的 Mod、存档与配置复制到该实例？\n" +
                        "复制不会改动共享目录，已存在的同名文件也不会被覆盖。\n" +
                        "选择「不复制」则该实例从空目录开始。",
                        "复制",
                        "不复制"))
                {
                    // 可能有上千个文件，放到后台线程避免卡住界面
                    copyResult = await Task.Run(() => _gameDirectories.CopySharedContent(_instance.Id));
                }
            }

            _instance.VersionIsolation = VersionIsolation;
            // 留空表示不自定义，恢复默认标题
            _instance.CustomWindowTitle = string.IsNullOrWhiteSpace(CustomWindowTitle) ? null : CustomWindowTitle.Trim();
            await _repository.SaveAsync(_instance);

            StatusMessage = copyResult is { } summary
                ? $"已保存，并复制了 {summary.CopiedFiles} 个文件（跳过已存在的 {summary.SkippedFiles} 个）。"
                : "设置已保存";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存实例设置失败");
            await _dialogs.ShowMessageAsync("保存失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
