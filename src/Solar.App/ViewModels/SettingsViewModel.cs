using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Services;
using Solar.Core.Java;
using Solar.Core.Memory;
using Solar.Core.Mods;
using Solar.Core.Paths;
using Solar.Core.Settings;

namespace Solar.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IJavaDetector _javaDetector;
    private readonly IMemoryAllocationService _memory;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;

    /// <summary>换源后要清掉磁盘缓存，否则还会拿到旧源的内容。</summary>
    private readonly ModCache _modCache;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>0 = 自动分配，1 = 手动配置。RadioButtons 只认索引。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutomaticMemory))]
    [NotifyPropertyChangedFor(nameof(IsManualMemory))]
    private int _memoryModeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryLimitText))]
    [NotifyPropertyChangedFor(nameof(MemorySliderValue))]
    private int _maxMemoryMb;

    [ObservableProperty]
    private int _memorySliderMaximum = MemoryAllocator.MaximumMb;

    [ObservableProperty]
    private int _memorySliderMinimum = MemoryAllocator.MinimumMb;

    [ObservableProperty]
    private string _systemMemoryText = "正在检测…";

    [ObservableProperty]
    private string _autoMemoryPreview = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveMinecraftDirectory))]
    private string _minecraftDirectory = "";

    [ObservableProperty]
    private string _javaPath = "";

    [ObservableProperty]
    private string _versionManifestUrl = "";

    [ObservableProperty]
    private int _downloadThreads;

    [ObservableProperty]
    private string _downloadMirrorUrl = "";

    [ObservableProperty]
    private string _detectedJava = "未检测";

    /// <summary>0 = 每次询问，1 = 总是开启，2 = 总是关闭。ComboBox 只认索引。</summary>
    [ObservableProperty]
    private int _newInstanceIsolationIndex;

    /// <summary>0 = 不执行任何操作，1 = 最小化，2 = 关闭。ComboBox 只认索引。</summary>
    [ObservableProperty]
    private int _afterLaunchActionIndex;

    /// <summary>0 = 官方优先，1 = 镜像优先，2 = 仅镜像。</summary>
    [ObservableProperty]
    private int _modSourcePreferenceIndex;

    [ObservableProperty]
    private string _modMirrorUrl = "";

    /// <summary>CurseForge 官方 API 的 Key。留空时只能走镜像（镜像自带 Key）。</summary>
    [ObservableProperty]
    private string _curseForgeApiKey = "";

    public SettingsViewModel(
        ISettingsService settings,
        IJavaDetector javaDetector,
        IMemoryAllocationService memory,
        IDialogService dialogs,
        AppPaths paths,
        ModCache modCache,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _javaDetector = javaDetector;
        _memory = memory;
        _dialogs = dialogs;
        _paths = paths;
        _modCache = modCache;
        _logger = logger;

        LoadFromSettings();
        RefreshMemoryInfo();
    }

    public string DataRoot => _paths.Root;

    public bool IsAutomaticMemory => MemoryModeIndex == 0;

    public bool IsManualMemory => MemoryModeIndex == 1;
    /// <summary>滑条旁的数值，按 GB/MB 显示更直观。</summary>
    public string MemoryLimitText => MaxMemoryMb >= 1024
        ? $"{MaxMemoryMb} MB（约 {MaxMemoryMb / 1024.0:0.#} GB）"
        : $"{MaxMemoryMb} MB";

    /// <summary>
    /// Slider 的值是 double，而设置里存的是整数 MB。
    /// x:Bind 是强类型的，不经过转换器就无法在两者之间双向绑定，所以在这里做桥接。
    /// </summary>
    public double MemorySliderValue
    {
        get => MaxMemoryMb;
        set
        {
            // 滑条按 StepFrequency 取值，浮点误差可能让它停在 4095.999…，四舍五入回整数
            var rounded = (int)Math.Round(value);
            if (rounded != MaxMemoryMb)
            {
                MaxMemoryMb = MemoryAllocator.Clamp(rounded);
            }
        }
    }

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

    /// <summary>版本清单的官方地址；输入框留空时保存的就是它。</summary>
    private const string DefaultVersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>界面上的值与已保存的设置是否有出入。</summary>
    public bool HasUnsavedChanges
    {
        get
        {
            var saved = _settings.Current;

            // 判定规则与 TrySaveAsync 写入时的规范化保持一致：
            // 只差空白、大小写，或改回了原值，都不算未保存。
            return (IsManualMemory ? MemoryAllocationMode.Manual : MemoryAllocationMode.Automatic) != saved.MemoryAllocationMode
                   || MemoryAllocator.Clamp(MaxMemoryMb) != saved.MaxMemoryMb
                   || MinecraftDirectoryChanged
                   || NullIfBlank(JavaPath) != saved.JavaPath
                   || NormalizedVersionManifestUrl != saved.VersionManifestUrl
                   || Math.Clamp(DownloadThreads, 1, 32) != saved.DownloadThreads
                   || NullIfBlank(DownloadMirrorUrl) != saved.DownloadMirrorUrl
                   || (VersionIsolationDefault)Math.Clamp(NewInstanceIsolationIndex, 0, 2) != saved.NewInstanceIsolation
                   || (AfterLaunchAction)Math.Clamp(AfterLaunchActionIndex, 0, 2) != saved.AfterLaunchAction
                   || (ModSourcePreference)Math.Clamp(ModSourcePreferenceIndex, 0, 2) != saved.ModSourcePreference
                   || NullIfBlank(ModMirrorUrl) != saved.ModMirrorUrl
                   || NullIfBlank(CurseForgeApiKey) != saved.CurseForgeApiKey;
        }
    }

    /// <summary>
    /// 游戏目录按「实际会用的路径」比：留空和显式填默认目录是一回事，
    /// 大小写与末尾分隔符也不算修改。
    /// </summary>
    private bool MinecraftDirectoryChanged
    {
        get
        {
            try
            {
                return !string.Equals(
                    _paths.ResolveMinecraftDirectory(MinecraftDirectory).TrimEnd('\\', '/'),
                    _paths.ResolveMinecraftDirectory(_settings.Current.MinecraftDirectory).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 路径不合法时退回文本比较；保存时还会再校验一次
                return NullIfBlank(MinecraftDirectory) != _settings.Current.MinecraftDirectory;
            }
        }
    }

    private string NormalizedVersionManifestUrl => string.IsNullOrWhiteSpace(VersionManifestUrl)
        ? DefaultVersionManifestUrl
        : VersionManifestUrl.Trim();

    private void LoadFromSettings() => LoadFromSettings(_settings.Current);

    private void LoadFromSettings(LauncherSettings s)
    {
        MemoryModeIndex = s.MemoryAllocationMode == MemoryAllocationMode.Manual ? 1 : 0;
        MaxMemoryMb = MemoryAllocator.Clamp(s.MaxMemoryMb);
        JavaPath = s.JavaPath ?? "";
        MinecraftDirectory = s.MinecraftDirectory ?? "";
        VersionManifestUrl = s.VersionManifestUrl;
        DownloadThreads = s.DownloadThreads;
        DownloadMirrorUrl = s.DownloadMirrorUrl ?? "";
        NewInstanceIsolationIndex = (int)s.NewInstanceIsolation;
        AfterLaunchActionIndex = (int)s.AfterLaunchAction;
        ModSourcePreferenceIndex = (int)s.ModSourcePreference;
        ModMirrorUrl = s.ModMirrorUrl ?? "";
        CurseForgeApiKey = s.CurseForgeApiKey ?? "";
    }

    /// <summary>
    /// 读取物理内存，刷新自动分配的预览值。
    /// 滑条上限不超过物理内存，但不强行压低用户已存的值——
    /// 换到内存更小的机器上打开设置时，不该悄悄把用户设好的 16GB 改成 8GB。
    /// </summary>
    private void RefreshMemoryInfo()
    {
        try
        {
            var info = _memory.GetSystemMemory();
            if (info.TotalMb <= 0)
            {
                SystemMemoryText = "未能检测到物理内存";
                AutoMemoryPreview = "";
                return;
            }

            SystemMemoryText = $"物理内存 {FormatMb(info.TotalMb)}，当前可用 {FormatMb(info.AvailableMb)}";

            // 滑条上限取物理内存总量（向下取整到 512MB）。
            // 必须把当前值也算进去：Slider 会把超出上限的 Value 拉回上限并回写绑定，
            // 那等于又悄悄改掉了用户的设置。
            MemorySliderMaximum = Math.Clamp(
                Math.Max(info.TotalMb / 512 * 512, MaxMemoryMb),
                MemoryAllocator.MinimumMb,
                MemoryAllocator.MaximumMb);

            // 预览按共享游戏目录里的模组数量估算；实例开启隔离后用的是自己的目录，实际值可能不同
            var mods = MemoryAllocationService.CountMods(
                _paths.ResolveMinecraftDirectory(_settings.Current.MinecraftDirectory));
            var recommended = MemoryAllocator.Recommend(info.TotalMb, info.AvailableMb, mods);
            AutoMemoryPreview = $"当前会分配 {FormatMb(recommended)}（按共享游戏目录里的 {mods} 个模组估算）";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取物理内存失败");
            SystemMemoryText = "未能检测到物理内存";
            AutoMemoryPreview = "";
        }
    }

    private static string FormatMb(int mb) => mb >= 1024 ? $"{mb / 1024.0:0.#} GB" : $"{mb} MB";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await _settings.LoadAsync();
        LoadFromSettings();
        RefreshMemoryInfo();
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

    /// <summary>
    /// 恢复默认值：只改界面上的值，点保存才写盘。
    /// 这样用户可以先把各项看一遍再决定，也可以直接离开放弃。
    /// </summary>
    [RelayCommand]
    private void ResetToDefaults() => LoadFromSettings(new LauncherSettings());

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!await TrySaveAsync())
        {
            return;
        }

        StatusMessage = "设置已保存";
        await _dialogs.ShowMessageAsync("已保存", "设置已保存。");
    }

    /// <summary>离开页面时询问是否保存未提交的修改。</summary>
    public Task<SaveChangesChoice> AskSaveBeforeLeavingAsync() =>
        _dialogs.ShowSaveChangesAsync("设置尚未保存", "设置的修改还没有保存，离开前要保存吗？");

    /// <summary>
    /// 校验并写入设置。失败时已经提示过用户，返回 false。
    /// 与 <see cref="SaveAsync"/> 分开，是因为离开页面时的保存不该再弹一次「已保存」。
    /// </summary>
    public async Task<bool> TrySaveAsync()
    {
        if (!TryNormalizeMinecraftDirectory(out var minecraftDirectory, out var pathError))
        {
            await _dialogs.ShowMessageAsync("路径无效", pathError);
            return false;
        }

        var s = _settings.Current;
        s.MemoryAllocationMode = IsManualMemory ? MemoryAllocationMode.Manual : MemoryAllocationMode.Automatic;
        s.MaxMemoryMb = MemoryAllocator.Clamp(MaxMemoryMb);
        s.MinecraftDirectory = minecraftDirectory;
        s.JavaPath = NullIfBlank(JavaPath);
        s.VersionManifestUrl = NormalizedVersionManifestUrl;
        s.DownloadThreads = Math.Clamp(DownloadThreads, 1, 32);
        s.DownloadMirrorUrl = NullIfBlank(DownloadMirrorUrl);
        s.NewInstanceIsolation = (VersionIsolationDefault)Math.Clamp(NewInstanceIsolationIndex, 0, 2);
        s.AfterLaunchAction = (AfterLaunchAction)Math.Clamp(AfterLaunchActionIndex, 0, 2);

        // Mod 源三项：换了任何一个，缓存里的项目与版本都属于旧源，必须清掉
        var modSourceChanged = s.ModSourcePreference != (ModSourcePreference)Math.Clamp(ModSourcePreferenceIndex, 0, 2)
                               || s.ModMirrorUrl != NullIfBlank(ModMirrorUrl)
                               || s.CurseForgeApiKey != NullIfBlank(CurseForgeApiKey);

        s.ModSourcePreference = (ModSourcePreference)Math.Clamp(ModSourcePreferenceIndex, 0, 2);
        s.ModMirrorUrl = NullIfBlank(ModMirrorUrl);
        s.CurseForgeApiKey = NullIfBlank(CurseForgeApiKey);

        try
        {
            await _settings.SaveAsync();

            if (modSourceChanged)
            {
                _modCache.Clear();
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存设置失败");
            await _dialogs.ShowMessageAsync("保存失败", ex.Message);
            return false;
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
