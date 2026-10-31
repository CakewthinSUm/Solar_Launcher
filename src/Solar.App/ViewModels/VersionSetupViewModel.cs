using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Services;
using Solar.Core.Paths;
using Solar.Core.Versions;

namespace Solar.App.ViewModels;

/// <summary>
/// 单个版本的设置页：版本隔离、窗口标题，以及「自定义」里的版本 id 与图标。
/// 页面上的改动会自动保存，不需要再点保存按钮。导航参数是版本 Id。
/// </summary>
public sealed partial class VersionSetupViewModel : ViewModelBase
{
    /// <summary>输入停顿这么久之后才写盘，免得逐字符输入时每敲一下都去改一次文件夹名。</summary>
    private static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly IVersionRepository _repository;
    private readonly MinecraftPaths _gameDirectories;
    private readonly IDialogService _dialogs;
    private readonly ILogger<VersionSetupViewModel> _logger;

    /// <summary>保存与载入串行执行，避免改名和写设置互相穿插。</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <summary>载入或改名后同步界面时不要再触发一次自动保存。</summary>
    private bool _suppressAutoSave;

    /// <summary>防抖中的自动保存；新的改动会取消上一次。</summary>
    private CancellationTokenSource? _autoSaveCts;

    private GameVersion? _instance;

    /// <summary>用户新挑的图标源文件；自动保存时才复制进版本文件夹。</summary>
    private string? _pendingIconSource;

    /// <summary>图标被换过或清掉了；没动过就不去碰磁盘上的图标文件。</summary>
    private bool _iconChanged;

    private static readonly string[] IconExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico"];

    /// <summary>
    /// 页面被 <c>NavigationCacheMode.Required</c> 缓存复用，切换版本时页面参数变化不会重建页面，
    /// 所以由页面在 <c>OnNavigatedTo</c> 里显式写入，再触发 <see cref="OnNavigatedToAsync"/> 加载。
    /// </summary>
    public string? VersionId { get; set; }

    [ObservableProperty]
    private string _VersionName = "";

    [ObservableProperty]
    private string _VersionSummary = "";

    /// <summary>「自定义」里的版本 id：就是版本文件夹名，自动保存时改名。</summary>
    [ObservableProperty]
    private string _versionIdInput = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveGameDirectory))]
    [NotifyPropertyChangedFor(nameof(IsolationHint))]
    private bool _versionIsolation;

    [ObservableProperty]
    private string _customWindowTitle = "";

    /// <summary>当前显示的图标路径：可能是已保存的图标，也可能是还没保存的新图片。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private string? _iconPath;

    public bool HasIcon => !string.IsNullOrEmpty(IconPath);

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

    /// <summary>
    /// 该版本实际会用的游戏目录。跟随开关当前的值而不是已保存的值，
    /// 这样拨动开关时下方的路径立刻变化，不会看起来像开关没反应。
    /// </summary>
    public string EffectiveGameDirectory => _instance is null
        ? ""
        : _gameDirectories.ResolveGameDirectory(_instance.Id, VersionIsolation);

    public string IsolationHint => VersionIsolation
        ? "游戏目录就是该版本的文件夹，存档与模组都在里面。"
        : "与其它未开启隔离的版本共用同一份存档、模组与配置，多个带 Mod 的版本可能互相冲突。";

    /// <summary>开关这类一次性改动立刻写盘，不必等防抖。</summary>
    partial void OnVersionIsolationChanged(bool value) => ScheduleAutoSave(immediate: true);

    partial void OnCustomWindowTitleChanged(string value) => ScheduleAutoSave();

    partial void OnVersionIdInputChanged(string value) => ScheduleAutoSave();

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        // 上一个版本的改动可能还在防抖等待里，先写盘再换掉 _instance
        await FlushPendingSaveAsync();

        var versionId = parameter as string ?? VersionId;
        if (string.IsNullOrEmpty(versionId))
        {
            // 没有目标版本时别让后续输入还写回上一个版本
            _instance = null;
            ErrorMessage = "没有指定要设置的版本。";
            return;
        }

        await _saveGate.WaitAsync();
        try
        {
            _suppressAutoSave = true;
            _instance = null;

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
            VersionIdInput = _instance.Id;
            VersionIsolation = _instance.VersionIsolation;
            CustomWindowTitle = _instance.CustomWindowTitle ?? "";
            IconPath = _instance.IconPath;

            _pendingIconSource = null;
            _iconChanged = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载版本设置失败");
            ErrorMessage = ex.Message;
        }
        finally
        {
            _suppressAutoSave = false;
            IsBusy = false;
            _saveGate.Release();
        }
    }

    /// <summary>离开页面时把还在防抖等待里的改动立刻写掉。</summary>
    public async Task FlushPendingSaveAsync()
    {
        if (_autoSaveCts is null)
        {
            return;
        }

        _autoSaveCts.Cancel();
        _autoSaveCts = null;
        await SaveAsync();
    }

    /// <summary>挑一张本地图片作为版本图标；挑完立刻保存。</summary>
    [RelayCommand]
    private async Task PickIconAsync()
    {
        if (App.MainWindow is not { } window)
        {
            return;
        }

        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window.AppWindow.Id)
            {
                CommitButtonText = "选择",
            };

            foreach (var extension in IconExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            var file = await picker.PickSingleFileAsync();
            if (string.IsNullOrEmpty(file?.Path))
            {
                return;
            }

            _pendingIconSource = file.Path;
            _iconChanged = true;
            IconPath = file.Path;
            ScheduleAutoSave(immediate: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "选择版本图标失败");
            await _dialogs.ShowMessageAsync("无法打开文件选择器", "请稍后重试。\n\n" + ex.Message);
        }
    }

    /// <summary>恢复默认图标，立刻保存。</summary>
    [RelayCommand]
    private void ClearIcon()
    {
        _pendingIconSource = null;
        _iconChanged = true;
        IconPath = null;
        ScheduleAutoSave(immediate: true);
    }

    /// <summary>
    /// 安排一次自动保存：开关、图标这类一次性改动立刻写盘，
    /// 输入框里的文字则等用户停手后再写，避免每敲一个字符就改一次文件夹名。
    /// </summary>
    private void ScheduleAutoSave(bool immediate = false)
    {
        if (_suppressAutoSave || _instance is null)
        {
            return;
        }

        _autoSaveCts?.Cancel();
        _autoSaveCts = null;

        if (immediate)
        {
            _ = SaveAsync();
            return;
        }

        var cts = new CancellationTokenSource();
        _autoSaveCts = cts;
        _ = SaveAfterDelayAsync(cts);
    }

    private async Task SaveAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(AutoSaveDelay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_autoSaveCts, cts))
            {
                _autoSaveCts = null;
            }

            cts.Dispose();
        }

        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            await SaveCoreAsync();
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task SaveCoreAsync()
    {
        var instance = _instance;
        if (instance is null)
        {
            return;
        }

        var targetId = VersionIdInput.Trim();
        if (targetId.Length == 0)
        {
            ErrorMessage = "版本 id 不能为空。";
            return;
        }

        // id 就是版本文件夹名，先挡掉 Windows 不允许的字符，
        // 免得 BuildId 悄悄替换成下划线、用户以为名字没生效
        if (targetId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || targetId.EndsWith('.'))
        {
            ErrorMessage = "版本 id 不能包含 \\ / : * ? \" < > | 等字符，也不能以点结尾。";
            return;
        }

        var wasIsolated = instance.VersionIsolation;

        try
        {
            IsBusy = true;
            ErrorMessage = null;

            // 改了 id 就先改名：版本文件夹、版本 JSON 与 jar 一起改，
            // 之后的隔离复制与图标都落在新文件夹里
            var renamed = !string.Equals(targetId, instance.Id, StringComparison.Ordinal);
            if (renamed)
            {
                instance = await _repository.RenameAsync(instance.Id, targetId);
                _instance = instance;
                VersionId = instance.Id;

                // 回写规范化后的 id 不该再触发一次保存
                _suppressAutoSave = true;
                try
                {
                    VersionIdInput = instance.Id;
                    VersionName = instance.Name;
                    VersionSummary = $"{instance.MinecraftVersion} · {instance.DisplayLoader}";
                }
                finally
                {
                    _suppressAutoSave = false;
                }

                OnPropertyChanged(nameof(EffectiveGameDirectory));
            }

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
                        _gameDirectories.GetVersionDirectory(instance.Id)));
                }
            }

            instance.VersionIsolation = VersionIsolation;
            // 留空表示不自定义，恢复默认标题
            instance.CustomWindowTitle = string.IsNullOrWhiteSpace(CustomWindowTitle) ? null : CustomWindowTitle.Trim();

            if (_iconChanged)
            {
                await _repository.SetIconAsync(instance, _pendingIconSource);
                _pendingIconSource = null;
                _iconChanged = false;
                IconPath = instance.IconPath;
            }

            await _repository.SaveSettingsAsync(instance);

            StatusMessage = copyResult is { } summary
                ? $"已自动保存，并复制了 {summary.CopiedFiles} 个文件（跳过已存在的 {summary.SkippedFiles} 个）。"
                : renamed
                    ? $"已自动保存，版本 id 改为“{instance.Id}”。"
                    : "已自动保存";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "自动保存版本设置失败");
            // 自动保存在后台进行，用页面上的错误条提示，不弹对话框打断输入
            ErrorMessage = "自动保存失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
