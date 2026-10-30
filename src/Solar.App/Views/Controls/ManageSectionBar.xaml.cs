using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Solar.App.Views.Controls;

/// <summary>
/// 「管理」页顶部的分区切换栏：实例 / Mods / 资源包 / 光影。
/// 侧边栏的「管理」不再展开子项，分区由这里切换，页面各自决定怎么导航。
/// </summary>
public sealed partial class ManageSectionBar : UserControl
{
    /// <summary>程序化改选中项时置位，避免把同步当成用户点击再导航一次。</summary>
    private bool _syncing;

    /// <summary>期望高亮的分区键；控件加载前设置也要记住。</summary>
    private string? _selectedKey;

    public ManageSectionBar()
    {
        InitializeComponent();
    }

    /// <summary>用户切到了另一个分区，参数是分区的导航键。</summary>
    public event EventHandler<string>? SectionSelected;

    /// <summary>
    /// 当前高亮的分区键。只同步显示，不触发 <see cref="SectionSelected"/>——
    /// 页面在导航进来时用它把选中项拨到自己的分区。
    /// </summary>
    public string? SelectedKey
    {
        get => _selectedKey;
        set
        {
            _selectedKey = value;
            ApplySelection();
        }
    }

    /// <summary>
    /// 页面导航进来时控件往往还没加载，SelectorBar 加载过程中会把选中项拨回第一项，
    /// 所以加载完成后再排一次同步，否则第一次进入 Mods / 资源包 / 光影 时会停在「实例」上。
    /// </summary>
    private void ManageSectionBar_Loaded(object sender, RoutedEventArgs e) =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplySelection);

    private void ApplySelection()
    {
        var item = Bar.Items.FirstOrDefault(i => Equals(i.Tag, _selectedKey));
        if (item is null || ReferenceEquals(item, Bar.SelectedItem))
        {
            return;
        }

        _syncing = true;
        try
        {
            Bar.SelectedItem = item;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Bar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncing || sender.SelectedItem?.Tag is not string key)
        {
            return;
        }

        SectionSelected?.Invoke(this, key);
    }
}
