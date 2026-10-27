using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Solar.Core.Addons;

namespace Solar.App.ViewModels;

/// <summary>
/// 创建页上的一张组件卡片（Forge / NeoForge / Fabric / Fabric API / OptiFine …）。
/// <para>
/// 卡片只描述「能不能选、当前选了哪个」；真正决定装什么的是
/// <see cref="ComponentSelection"/>，由 <see cref="InstanceCreateViewModel"/> 汇总。
/// </para>
/// </summary>
public sealed partial class ComponentCardViewModel : ObservableObject
{
    public ComponentCardViewModel(ComponentKind kind, string title, string description)
    {
        Kind = kind;
        Title = title;
        Description = description;
    }

    public ComponentKind Kind { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>下拉框里的可选项；元素类型由卡片自己决定，界面上只显示 <c>ToString()</c>。</summary>
    public ObservableCollection<object> Options { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedText))]
    private object? _selected;

    /// <summary>不可用时的原因；为空表示可选。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    private string? _unavailableReason;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>加载失败的提示（例如网络不通）。</summary>
    [ObservableProperty]
    private string? _loadError;

    public bool HasSelection => Selected is not null;

    public bool IsEnabled => UnavailableReason is null;

    public string SelectedText => Selected switch
    {
        null => "未选择",
        OptiFineBuild optifine => optifine.DisplayVersion,
        LiteLoaderBuild liteLoader => liteLoader.Version,
        AddonModVersion mod => mod.VersionNumber,
        _ => Selected.ToString() ?? "未选择",
    };

    partial void OnSelectedChanged(object? value) => OnPropertyChanged(nameof(SelectedText));

    partial void OnUnavailableReasonChanged(string? value) => OnPropertyChanged(nameof(IsEnabled));

    /// <summary>清空选择；版本列表保留，用户还能再选回来。</summary>
    public void Clear() => Selected = null;
}
