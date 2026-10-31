using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Solar.Core.Versions;

namespace Solar.App.ViewModels;

/// <summary>
/// 版本列表里的一个可折叠分组。分组对象在页面生命周期内保持不变，
/// 只更新 <see cref="Versions"/> 的内容，这样重新分组时展开状态和滚动位置都不会丢。
/// </summary>
public sealed partial class VersionGroupViewModel : ObservableObject
{
    public VersionGroupViewModel(VersionGroupKind kind)
    {
        Kind = kind;
        Title = VersionGrouping.GetTitle(kind);
    }

    public VersionGroupKind Kind { get; }

    public string Title { get; }

    public ObservableCollection<GameVersion> Versions { get; } = new();

    public int Count => Versions.Count;

    /// <summary>分组标题右侧的数量，如 <c>(18)</c>。</summary>
    public string CountText => $"({Count})";

    public bool IsEmpty => Count == 0;

    /// <summary>收藏夹始终显示；另外两组没有内容时整组收起。</summary>
    public bool IsVisible => Kind == VersionGroupKind.Favorites || Count > 0;

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>用新的分组结果刷新内容；内容没变时不重建行，避免无谓的视觉树重排。</summary>
    public void Apply(IReadOnlyList<GameVersion> versions)
    {
        if (Versions.SequenceEqual(versions))
        {
            return;
        }

        Versions.Clear();
        foreach (var version in versions)
        {
            Versions.Add(version);
        }

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsVisible));
    }
}
