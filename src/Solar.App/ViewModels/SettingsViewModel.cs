using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Services;
using Solar.Core.Java;
using Solar.Core.Paths;
using Solar.Core.Settings;

namespace Solar.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IJavaDetector _javaDetector;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty]
    private int _maxMemoryMb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveMinecraftDirectory))]
    private string _minecraftDirectory = "";

    [ObservableProperty]
    private string _javaPath = "";

    [ObservableProperty]
    private string _microsoftClientId = "";

    [ObservableProperty]
    private string _versionManifestUrl = "";

    [ObservableProperty]
    private int _downloadThreads;

    [ObservableProperty]
    private string _downloadMirrorUrl = "";

    [ObservableProperty]
    private string _detectedJava = "未检测";

    public SettingsViewModel(
        ISettingsService settings,
        IJavaDetector javaDetector,
        IDialogService dialogs,
        AppPaths paths,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _javaDetector = javaDetector;
        _dialogs = dialogs;
        _paths = paths;
        _logger = logger;

        LoadFromSettings();
    }

    public string DataRoot => _paths.Root;

    /// <summary>
    /// 实际会用的目录，直接显示给用户看。
    /// 输入框是逐字符绑定的，用户可能在中途输入非法路径，
    /// 这里不能抛异常，否则绑定会中断甚至闪退。
    /// </summary>
    public string EffectiveMinecraftDirectory
    {
        get
        {
            try
            {
                return _paths.ResolveMinecraftDirectory(MinecraftDirectory);
            }
            catch
            {
                return "路径无效";
            }
        }
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        MaxMemoryMb = s.MaxMemoryMb;
        JavaPath = s.JavaPath ?? "";
        MinecraftDirectory = s.MinecraftDirectory ?? "";
        MicrosoftClientId = s.MicrosoftClientId ?? "";
        VersionManifestUrl = s.VersionManifestUrl;
        DownloadThreads = s.DownloadThreads;
        DownloadMirrorUrl = s.DownloadMirrorUrl ?? "";
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await _settings.LoadAsync();
        LoadFromSettings();
        await DetectJavaAsync();
    }

    /// <summary>
    /// 用系统文件夹选择器挑 .minecraft 目录。
    /// 走 Windows App SDK 的 <c>Microsoft.Windows.Storage.Pickers</c>：
    /// 它接收 WindowId，非打包应用里不需要再做 InitializeWithWindow。
    /// </summary>
    [RelayCommand]
    private async Task BrowseMinecraftDirectoryAsync()
    {
        if (App.MainWindow is not { } window)
        {
            return;
        }

        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(window.AppWindow.Id)
            {
                CommitButtonText = "选择此文件夹",
            };

            var result = await picker.PickSingleFolderAsync();
            if (!string.IsNullOrEmpty(result?.Path))
            {
                MinecraftDirectory = result.Path;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "选择 .minecraft 目录失败");
            await _dialogs.ShowMessageAsync("无法打开文件夹选择器", "请直接在输入框里填写路径。\n\n" + ex.Message);
        }
    }

    /// <summary>恢复默认值（程序目录下的 .minecraft），保存后生效。</summary>
    [RelayCommand]
    private void UseDefaultMinecraftDirectory() => MinecraftDirectory = "";

    /// <summary>
    /// 校验并规范化 .minecraft 路径：指到默认目录时存空值（保持便携），
    /// 目录不存在就建出来，建不出来（无权限、驱动器不存在）则在保存前报错。
    /// </summary>
    private bool TryNormalizeMinecraftDirectory(out string? normalized, out string error)
    {
        normalized = null;
        error = "";

        if (string.IsNullOrWhiteSpace(MinecraftDirectory))
        {
            return true;
        }

        string full;
        try
        {
            full = _paths.ResolveMinecraftDirectory(MinecraftDirectory);
        }
        catch (Exception ex)
        {
            error = "无法解析该路径：" + ex.Message;
            return false;
        }

        if (File.Exists(full))
        {
            error = $"{full} 是一个文件，请选择文件夹。";
            return false;
        }

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex)
        {
            error = $"无法创建目录 {full}：{ex.Message}";
            return false;
        }

        // 与默认目录相同时不落盘，用户把程序换个位置也能跟着走
        normalized = string.Equals(full, _paths.DefaultMinecraft, StringComparison.OrdinalIgnoreCase)
            ? null
            : full;

        return true;
    }

    [RelayCommand]
    private async Task DetectJavaAsync()
    {
        try
        {
            IsBusy = true;
            var runtimes = await _javaDetector.DetectAsync();
            DetectedJava = runtimes.Count == 0
                ? "未检测到 Java，请手动指定 java.exe 路径"
                : string.Join("；", runtimes.Select(r => r.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检测 Java 失败");
            DetectedJava = "检测失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!TryNormalizeMinecraftDirectory(out var minecraftDirectory, out var pathError))
        {
            await _dialogs.ShowMessageAsync("路径无效", pathError);
            return;
        }

        var s = _settings.Current;
        s.MaxMemoryMb = Math.Max(512, MaxMemoryMb);
        s.MinecraftDirectory = minecraftDirectory;
        s.JavaPath = string.IsNullOrWhiteSpace(JavaPath) ? null : JavaPath.Trim();
        s.MicrosoftClientId = string.IsNullOrWhiteSpace(MicrosoftClientId) ? null : MicrosoftClientId.Trim();
        s.VersionManifestUrl = string.IsNullOrWhiteSpace(VersionManifestUrl)
            ? "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"
            : VersionManifestUrl.Trim();
        s.DownloadThreads = Math.Clamp(DownloadThreads, 1, 32);
        s.DownloadMirrorUrl = string.IsNullOrWhiteSpace(DownloadMirrorUrl) ? null : DownloadMirrorUrl.Trim();

        try
        {
            await _settings.SaveAsync();
            StatusMessage = "设置已保存";
            await _dialogs.ShowMessageAsync("已保存", "设置已保存。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存设置失败");
            await _dialogs.ShowMessageAsync("保存失败", ex.Message);
        }
    }
}
