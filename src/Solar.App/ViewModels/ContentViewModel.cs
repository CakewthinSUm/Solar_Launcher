using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Services;
using Solar.Core.Content;

namespace Solar.App.ViewModels;

/// <summary>「管理」下 Mods / 资源包 / 光影 三个分区的展示信息与导航键。</summary>
public sealed record ContentSectionInfo(ContentKind Kind, string Key, string Title, string Description)
{
    public static IReadOnlyList<ContentSectionInfo> All { get; } =
    [
        new(ContentKind.Mods, "mods", "Mods", "把 .jar 放进 .minecraft 下的 mods 文件夹"),
        new(ContentKind.ResourcePacks, "resourcepacks", "资源包", "替换游戏的贴图、字体与音效"),
        new(ContentKind.ShaderPacks, "shaderpacks", "光影", "需要配合 Iris / OptiFine 等光影加载器使用"),
    ];

    public static ContentSectionInfo FromKey(string? key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}

/// <summary>
/// 内容管理页：Mods、资源包、光影共用这一个视图模型，当前分区由页面顶部分区栏的导航参数决定。
/// 列表直接读共享游戏目录（设置里的 .minecraft）下的对应文件夹，不区分版本与版本隔离，
/// 文件系统就是唯一事实来源，用户手动往里放东西也能看到。
/// </summary>
public sealed partial class ContentViewModel : ViewModelBase
{
    private readonly IContentManager _content;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ContentViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SectionTitle))]
    [NotifyPropertyChangedFor(nameof(SectionDescription))]
    [NotifyPropertyChangedFor(nameof(ContentDirectory))]
    [NotifyPropertyChangedFor(nameof(EmptyHint))]
    private ContentSectionInfo _section = ContentSectionInfo.All[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(EmptyHint))]
    private bool _hasItems;

    [ObservableProperty]
    private ContentPack? _selectedPack;

    public ContentViewModel(
        IContentManager content,
        IDialogService dialogs,
        ILogger<ContentViewModel> logger)
    {
        _content = content;
        _dialogs = dialogs;
        _logger = logger;
    }

    public ObservableCollection<ContentPack> Packs { get; } = new();

    public string SectionTitle => Section.Title;

    public string SectionDescription => Section.Description;

    /// <summary>当前分区的内容文件夹，显示给用户，方便手动去放文件。</summary>
    public string ContentDirectory => _content.GetContentDirectory(Section.Kind);

    public bool IsEmpty => !HasItems;

    public string EmptyHint => "这个文件夹是空的，点「导入」把文件放进来。";

    public override Task OnNavigatedToAsync(object? parameter)
    {
        Load(parameter as string);
        return Task.CompletedTask;
    }

    /// <summary>切换分区后重新加载列表。</summary>
    [RelayCommand]
    private void Load(string? sectionKey = null)
    {
        if (sectionKey is not null)
        {
            Section = ContentSectionInfo.FromKey(sectionKey);
        }

        ReloadPacks();
    }

    private void ReloadPacks()
    {
        Packs.Clear();
        SelectedPack = null;
        ErrorMessage = null;

        try
        {
            foreach (var pack in _content.List(Section.Kind))
            {
                Packs.Add(pack);
            }

            HasItems = Packs.Count > 0;
        }
        catch (Exception ex)
        {
            // 目录被占用或没有权限时不要让页面崩掉，只把错误显示出来
            _logger.LogError(ex, "读取 {Kind} 目录失败", Section.Kind);
            ErrorMessage = $"读取 {ContentFolders.GetFolderName(Section.Kind)} 失败：{ex.Message}";
            HasItems = false;
        }
    }

    /// <summary>
    /// 导入用户选中的文件或文件夹。同名条目已存在时问一次是否覆盖。
    /// 页面负责弹出选择器，这里只处理复制与冲突。
    /// </summary>
    public async Task ImportAsync(IReadOnlyList<string> sourcePaths)
    {
        if (sourcePaths.Count == 0)
        {
            return;
        }

        // 先整体校验一遍：有选错的文件就当场说明，不要复制一半再报错
        var accepted = new List<string>();
        var rejected = new List<string>();
        foreach (var source in sourcePaths)
        {
            if (_content.CanImport(Section.Kind, source, out var reason))
            {
                accepted.Add(source);
            }
            else
            {
                rejected.Add(reason);
            }
        }

        if (rejected.Count > 0)
        {
            await _dialogs.ShowMessageAsync(
                "有文件不能导入",
                string.Join("\n", rejected.Distinct()));
        }

        if (accepted.Count == 0)
        {
            return;
        }

        var imported = 0;
        var skipped = 0;

        try
        {
            IsBusy = true;
            ErrorMessage = null;

            foreach (var source in accepted)
            {
                var overwrite = false;
                if (ExistsInTarget(source))
                {
                    overwrite = await _dialogs.ShowConfirmAsync(
                        "已存在同名条目",
                        $"{ContentFolders.GetFolderName(Section.Kind)} 里已经有“{Path.GetFileName(source)}”。\n要覆盖它吗？",
                        "覆盖",
                        "跳过");

                    if (!overwrite)
                    {
                        skipped++;
                        continue;
                    }
                }

                await _content.ImportAsync(Section.Kind, source, overwrite);
                imported++;
            }

            StatusMessage = imported == 0
                ? "没有导入任何文件。"
                : $"已导入 {imported} 项" + (skipped > 0 ? $"，跳过 {skipped} 项" : "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导入内容失败");
            await _dialogs.ShowMessageAsync("导入失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
            ReloadPacks();
        }
    }

    private bool ExistsInTarget(string sourcePath)
    {
        var target = Path.Combine(
            _content.GetContentDirectory(Section.Kind),
            Path.GetFileName(sourcePath));

        return File.Exists(target) || Directory.Exists(target);
    }

    [RelayCommand]
    private async Task DeleteAsync(ContentPack? pack)
    {
        if (pack is null)
        {
            return;
        }

        if (!await _dialogs.ShowConfirmAsync(
                "删除",
                $"确定要删除“{pack.Name}”吗？\n删除后无法恢复。",
                "删除",
                "取消"))
        {
            return;
        }

        try
        {
            _content.Delete(Section.Kind, pack);
            StatusMessage = $"已删除“{pack.Name}”";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除 {Name} 失败", pack.Name);
            await _dialogs.ShowMessageAsync("删除失败", ex.Message);
        }
        finally
        {
            ReloadPacks();
        }
    }

    [RelayCommand]
    private void Reveal(ContentPack? pack)
    {
        if (pack is null)
        {
            return;
        }

        try
        {
            _content.RevealInExplorer(pack);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开资源管理器失败");
            ErrorMessage = "无法打开资源管理器：" + ex.Message;
        }
    }

    /// <summary>打开内容文件夹本身，方便用户把文件直接拖进去。</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var directory = _content.GetContentDirectory(Section.Kind);
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开内容文件夹失败");
            ErrorMessage = "无法打开文件夹：" + ex.Message;
        }
    }
}
