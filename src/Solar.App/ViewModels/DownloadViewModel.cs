using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.Core.Minecraft;
using Solar.Core.Models;

namespace Solar.App.ViewModels;

/// <summary>下载页的六个分区，与侧边栏「下载」下的子项一一对应。</summary>
public enum DownloadSection
{
    Minecraft,
    Mod,
    Shaders,
    Modpacks,
    ResourcePacks,
    DataPacks,
}

/// <summary>分区的展示信息与导航参数。</summary>
public sealed record DownloadSectionInfo(DownloadSection Section, string Key, string Title, string Description)
{
    public static IReadOnlyList<DownloadSectionInfo> All { get; } =
    [
        new(DownloadSection.Minecraft, "minecraft", "Minecraft", "安装游戏本体与版本"),
        new(DownloadSection.Mod, "mod", "Mod", "为实例添加模组"),
        new(DownloadSection.Shaders, "shaders", "光影", "安装光影包"),
        new(DownloadSection.Modpacks, "modpacks", "整合包", "一键安装整合包"),
        new(DownloadSection.ResourcePacks, "resourcepacks", "资源包", "替换游戏贴图与音效"),
        new(DownloadSection.DataPacks, "datapacks", "数据包", "扩展原版玩法"),
    ];

    public static DownloadSectionInfo FromKey(string? key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}

/// <summary>
/// 下载页：六个分区共用这一个视图模型，当前分区由侧边栏子项的导航参数决定。
/// 目前只有 Minecraft 分区接入了数据，其余分区为占位。
/// </summary>
public sealed partial class DownloadViewModel : ViewModelBase
{
    private readonly IVersionManifestService _manifestService;
    private readonly ILogger<DownloadViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMinecraftSection))]
    [NotifyPropertyChangedFor(nameof(IsPlaceholderSection))]
    private DownloadSection _currentSection;

    [ObservableProperty]
    private string _sectionTitle = DownloadSectionInfo.All[0].Title;

    [ObservableProperty]
    private string _sectionDescription = DownloadSectionInfo.All[0].Description;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _showSnapshots;

    [ObservableProperty]
    private string _latestRelease = "—";

    [ObservableProperty]
    private string _latestSnapshot = "—";

    private List<VersionSummary> _all = new();

    public DownloadViewModel(IVersionManifestService manifestService, ILogger<DownloadViewModel> logger)
    {
        _manifestService = manifestService;
        _logger = logger;
    }

    /// <summary>Minecraft 分区的版本列表。</summary>
    public ObservableCollection<VersionSummary> Versions { get; } = new();

    public bool IsMinecraftSection => CurrentSection == DownloadSection.Minecraft;

    /// <summary>尚未接入数据的五个分区共用一个占位视图。</summary>
    public bool IsPlaceholderSection => !IsMinecraftSection;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var section = DownloadSectionInfo.FromKey(parameter as string);
        CurrentSection = section.Section;
        SectionTitle = section.Title;
        SectionDescription = section.Description;

        // 版本清单只服务 Minecraft 分区，且拉取后会缓存，无需每次切换分区都重来。
        if (IsMinecraftSection && _all.Count == 0)
        {
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;

            var manifest = await _manifestService.GetManifestAsync();
            _all = manifest.Versions;

            LatestRelease = manifest.Latest?.Release ?? "—";
            LatestSnapshot = manifest.Latest?.Snapshot ?? "—";

            ApplyFilter();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载版本列表失败");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowSnapshotsChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = _all.AsEnumerable();

        if (!ShowSnapshots)
        {
            query = query.Where(v => v.Type == "release");
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(v => v.Id.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        Versions.Clear();
        foreach (var version in query.Take(300))
        {
            Versions.Add(version);
        }

        StatusMessage = $"共 {Versions.Count} 个版本";
    }
}
