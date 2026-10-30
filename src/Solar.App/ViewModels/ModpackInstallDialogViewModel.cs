using CommunityToolkit.Mvvm.ComponentModel;
using Solar.App.Formatting;
using Solar.App.Services;
using Solar.Core.Content;
using Solar.Core.Versions;
using Solar.Core.Modpacks;
using Solar.Core.Mods;

namespace Solar.App.ViewModels;

/// <summary>
/// 整合包安装对话框。
/// <para>
/// 打开时先把整合包本体下下来解析（Modrinth 是 .mrpack，CurseForge 还要逐个解析
/// projectID/fileID），所以前半段是“准备”状态，用户能看到正在做什么。
/// 安装本身会新建一个隔离版本，文件全部下进这个版本的游戏目录。
/// </para>
/// </summary>
public sealed partial class ModpackInstallDialogViewModel : ViewModelBase
{
    private readonly IModpackService _modpacks;
    private readonly TaskbarProgressService _taskbar;

    private ModpackPlan? _plan;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _header = "安装整合包";

    [ObservableProperty]
    private string _gameText = "";

    [ObservableProperty]
    private string _filesText = "";

    [ObservableProperty]
    private string _VersionName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusyNow))]
    private bool _isPreparing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusyNow))]
    private bool _isInstalling;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private bool _canInstall;

    [ObservableProperty]
    private string _installText = "创建版本并安装";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanWarning))]
    private string? _planWarning;

    public bool HasPlanWarning => !string.IsNullOrWhiteSpace(PlanWarning);

    public bool IsBusyNow => IsPreparing || IsInstalling;

    /// <summary>安装成功后的结果；页面据此显示状态。</summary>
    public ModpackInstallResult? Result { get; private set; }

    public ModpackInstallDialogViewModel(IModpackService modpacks, TaskbarProgressService taskbar)
    {
        _modpacks = modpacks;
        _taskbar = taskbar;
    }

    public async Task LoadAsync(ModVersion version)
    {
        if (_plan is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();

        try
        {
            IsPreparing = true;
            ErrorMessage = null;
            ProgressValue = 0;
            IsProgressIndeterminate = true;
            ProgressText = "正在下载整合包…";

            var progress = new Progress<ModpackInstallProgress>(p =>
            {
                ProgressText = p.Message;
                IsProgressIndeterminate = p.Stage < ModpackInstallStage.DownloadingFiles || p.Total == 0;
                ProgressValue = p.Total == 0 ? 0 : (double)p.Completed / p.Total;
            });

            var plan = await _modpacks.CreatePlanAsync(version, progress, _cts.Token);
            _plan = plan;

            Header = $"安装 {plan.Name}";
            VersionName = plan.Name;
            GameText = FormatGame(plan);
            FilesText = $"共 {plan.Files.Count} 个文件"
                        + (plan.TotalBytes > 0 ? $"，约 {ContentPack.FormatSize(plan.TotalBytes)}" : "");

            PlanWarning = BuildWarning(plan);

            CanInstall = true;
            ProgressText = "准备完成";
            ProgressValue = 1;
            IsProgressIndeterminate = false;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "已取消。";
        }
        catch (Exception ex)
        {
            ErrorMessage = "读取整合包失败：" + ex.Message;
        }
        finally
        {
            IsPreparing = false;
        }
    }

    public async Task<bool> InstallAsync()
    {
        if (_plan is not { } plan || !CanInstall)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(VersionName))
        {
            ErrorMessage = "版本名不能为空。";
            return false;
        }

        if (_cts is null || _cts.IsCancellationRequested)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
        }

        try
        {
            IsInstalling = true;
            ErrorMessage = null;
            CanInstall = false;
            ProgressValue = 0;
            IsProgressIndeterminate = true;

            var progress = new Progress<ModpackInstallProgress>(p =>
            {
                ProgressText = p.Message;
                IsProgressIndeterminate = p.Stage < ModpackInstallStage.DownloadingFiles || p.Total == 0;
                ProgressValue = p.Total == 0 ? 0 : (double)p.Completed / p.Total;

                if (p.Stage == ModpackInstallStage.DownloadingFiles && p.Total > 0)
                {
                    _taskbar.Report((double)p.Completed / p.Total);
                }
                else
                {
                    _taskbar.Report(null);
                }
            });

            Result = await _modpacks.InstallAsync(
                plan,
                VersionName,
                progress,
                _cts.Token);

            ProgressValue = 1;
            IsProgressIndeterminate = false;
            ProgressText = Result.FailedFiles.Count == 0
                ? $"完成：安装了 {Result.InstalledFiles} 个文件"
                : $"完成：安装了 {Result.InstalledFiles} 个文件，{Result.FailedFiles.Count} 个失败";
            _taskbar.Flash();
            return true;
        }
        catch (OperationCanceledException)
        {
            ProgressText = "已取消";
            ErrorMessage = "安装已取消。已经下载的文件会保留，版本也还在列表里。";
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            ProgressText = "";
            return false;
        }
        finally
        {
            IsInstalling = false;
            IsProgressIndeterminate = false;
            _taskbar.Reset();
            CanInstall = Result is null;
        }
    }

    /// <summary>用户点「取消」或关掉对话框：中止正在进行的下载。</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>对话框关闭时删掉整合包临时文件。</summary>
    public void Cleanup()
    {
        if (_plan is { } plan)
        {
            _modpacks.Cleanup(plan);
        }
    }

    private static string FormatGame(ModpackPlan plan)
    {
        var loader = plan.Loader == ModLoader.Vanilla
            ? "原版"
            : $"{ModDisplay.LoaderName(plan.Loader)} {plan.LoaderVersion}".Trim();

        var version = string.IsNullOrWhiteSpace(plan.Version) ? "" : $" · 整合包 {plan.Version}";
        return $"{plan.MinecraftVersion} · {loader}{version}";
    }

    private static string? BuildWarning(ModpackPlan plan)
    {
        var parts = new List<string>();

        if (plan.FailedFiles.Count > 0)
        {
            parts.Add($"有 {plan.FailedFiles.Count} 个文件在解析时失败（作者可能关闭了分发），安装后可能缺少内容。");
        }

        parts.Add("会创建一个独立版本（版本隔离）。游戏本体与加载器在首次启动时自动补装。");

        return string.Join("\n", parts);
    }
}
