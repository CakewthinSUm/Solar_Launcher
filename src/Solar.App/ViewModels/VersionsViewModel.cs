using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.Core.Minecraft;
using Solar.Core.Models;

namespace Solar.App.ViewModels;

public sealed partial class VersionsViewModel : ViewModelBase
{
    private readonly IVersionManifestService _manifestService;
    private readonly ILogger<VersionsViewModel> _logger;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _showSnapshots;

    [ObservableProperty]
    private string _latestRelease = "—";

    [ObservableProperty]
    private string _latestSnapshot = "—";

    private List<VersionSummary> _all = new();

    public VersionsViewModel(IVersionManifestService manifestService, ILogger<VersionsViewModel> logger)
    {
        _manifestService = manifestService;
        _logger = logger;
    }

    public ObservableCollection<VersionSummary> Versions { get; } = new();

    public override Task OnNavigatedToAsync(object? parameter) => LoadAsync();

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
