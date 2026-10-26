using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.Core.Instances;
using Solar.Core.Java;

namespace Solar.App.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly IInstanceRepository _instances;
    private readonly IJavaDetector _javaDetector;
    private readonly ILogger<HomeViewModel> _logger;

    [ObservableProperty]
    private GameInstance? _selectedInstance;

    [ObservableProperty]
    private string _javaStatus = "未检测";

    public HomeViewModel(
        IInstanceRepository instances,
        IJavaDetector javaDetector,
        ILogger<HomeViewModel> logger)
    {
        _instances = instances;
        _javaDetector = javaDetector;
        _logger = logger;
    }

    public ObservableCollection<GameInstance> Instances { get; } = new();

    public string SelectedInstanceSummary => SelectedInstance is { } instance
        ? $"{instance.MinecraftVersion} · {instance.DisplayLoader}"
        : "先到“实例”页创建一个实例";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await RefreshAsync();
    }

    partial void OnSelectedInstanceChanged(GameInstance? value) =>
        OnPropertyChanged(nameof(SelectedInstanceSummary));

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;

            var instances = await _instances.GetAllAsync();
            Instances.Clear();
            foreach (var instance in instances)
            {
                Instances.Add(instance);
            }

            SelectedInstance = Instances.FirstOrDefault();

            var runtimes = await _javaDetector.DetectAsync();
            JavaStatus = runtimes.Count == 0
                ? "未找到 Java"
                : $"已检测 {runtimes.Count} 个 Java · 推荐 Java {runtimes[0].MajorVersion}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新主页失败");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Launch()
    {
        StatusMessage = SelectedInstance is null
            ? "还没有可启动的实例，请先到“实例”页创建一个。"
            : $"启动功能即将接入：{SelectedInstance.Name}";
    }
}
