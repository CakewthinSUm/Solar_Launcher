using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Formatting;
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

/// <summary>年份下拉的一项；<see cref="Year"/> 为 null 表示「全部年份」。</summary>
public sealed record YearOption(int? Year, string Label);

/// <summary>
/// Minecraft 下载页：版本清单与按版本号搜索。
/// 其余五个分区（Mod / 光影 / 整合包 / 资源包 / 数据包）走
/// <see cref="AddonDownloadViewModel"/>，它们共用另一套搜索与安装流程。
/// </summary>
public sealed partial class DownloadViewModel : ViewModelBase
{
    private readonly IVersionManifestService _manifestService;
    private readonly ILogger<DownloadViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMinecraftSection))]
    private DownloadSection _currentSection;

    [ObservableProperty]
    private string _sectionTitle = DownloadSectionInfo.All[0].Title;

    [ObservableProperty]
    private string _sectionDescription = DownloadSectionInfo.All[0].Description;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _showReleases = true;

    [ObservableProperty]
    private bool _showSnapshots;

    [ObservableProperty]
    private bool _showPrereleases;

    [ObservableProperty]
    private bool _showOldBeta;

    [ObservableProperty]
    private bool _showOldAlpha;

    [ObservableProperty]
    private int _yearIndex;

    [ObservableProperty]
    private string _emptyStateText = "";

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

    /// <summary>
    /// 发布年份筛选。加载清单后只追加、不整体替换：
    /// 替换 ItemsSource 会把 ComboBox 的 SelectedIndex 重置成 -1。
    /// </summary>
    public ObservableCollection<YearOption> YearOptions { get; } = new() { new(null, "全部年份") };

    /// <summary>没有可显示的结果。加载中、出错、还没加载时都为 false，免得空状态闪一下。</summary>
    public bool HasNoResults => !IsBusy && !HasErrorMessage && Versions.Count == 0 && _all.Count > 0;

    public bool IsMinecraftSection => CurrentSection == DownloadSection.Minecraft;

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

            RebuildYearOptions();
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
            OnPropertyChanged(nameof(HasNoResults));
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowReleasesChanged(bool value) => ApplyFilter();

    partial void OnShowSnapshotsChanged(bool value) => ApplyFilter();

    partial void OnShowPrereleasesChanged(bool value) => ApplyFilter();

    partial void OnShowOldBetaChanged(bool value) => ApplyFilter();

    partial void OnShowOldAlphaChanged(bool value) => ApplyFilter();

    partial void OnYearIndexChanged(int value)
    {
        if (value < 0)
        {
            // 选项变化时 ComboBox 可能先写回 -1，拉回「全部年份」。
            YearIndex = 0;
            return;
        }

        ApplyFilter();
    }

    /// <summary>恢复默认筛选：只看正式版、不限年份。</summary>
    [RelayCommand]
    private void ResetFilters()
    {
        ShowReleases = true;
        ShowSnapshots = false;
        ShowPrereleases = false;
        ShowOldBeta = false;
        ShowOldAlpha = false;
        YearIndex = 0;
    }

    private void RebuildYearOptions()
    {
        if (YearOptions.Count > 1)
        {
            return;
        }

        var years = _all
            .Where(v => v.ReleaseTime.HasValue)
            .Select(v => v.ReleaseTime!.Value.Year)
            .Distinct()
            .OrderByDescending(year => year);

        foreach (var year in years)
        {
            YearOptions.Add(new YearOption(year, year.ToString()));
        }
    }

    private int? SelectedYear =>
        YearIndex > 0 && YearIndex < YearOptions.Count ? YearOptions[YearIndex].Year : null;

    private List<VersionCategory> SelectedCategories()
    {
        var categories = new List<VersionCategory>();

        if (ShowReleases)
        {
            categories.Add(VersionCategory.Release);
        }

        if (ShowSnapshots)
        {
            categories.Add(VersionCategory.Snapshot);
        }

        if (ShowPrereleases)
        {
            categories.Add(VersionCategory.Prerelease);
        }

        if (ShowOldBeta)
        {
            categories.Add(VersionCategory.OldBeta);
        }

        if (ShowOldAlpha)
        {
            categories.Add(VersionCategory.OldAlpha);
        }

        return categories;
    }

    private void ApplyFilter()
    {
        var categories = SelectedCategories();

        IEnumerable<VersionSummary> query;

        if (categories.Count == 0)
        {
            query = Enumerable.Empty<VersionSummary>();
            EmptyStateText = "请至少选择一种版本类型";
        }
        else
        {
            query = _all.Where(v => categories.Any(category => VersionDisplay.Matches(v, category)));
            EmptyStateText = "没有符合条件的版本";
        }

        if (SelectedYear is { } year)
        {
            query = query.Where(v => v.ReleaseTime?.Year == year);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var keyword = SearchText.Trim();
            query = query.Where(v => v.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        Versions.Clear();
        foreach (var version in query)
        {
            Versions.Add(version);
        }

        StatusMessage = $"共 {Versions.Count} 个版本";
        OnPropertyChanged(nameof(HasNoResults));
    }
}
