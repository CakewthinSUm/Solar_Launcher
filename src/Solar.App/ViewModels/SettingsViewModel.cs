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

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        MaxMemoryMb = s.MaxMemoryMb;
        JavaPath = s.JavaPath ?? "";
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
        var s = _settings.Current;
        s.MaxMemoryMb = Math.Max(512, MaxMemoryMb);
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
