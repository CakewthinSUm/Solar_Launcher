using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Solar.App.Converters;

/// <summary>bool → Visibility，用于 x:Bind 场景下控制元素显隐。</summary>
public sealed class VisibleWhenTrueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

/// <summary>非空字符串 → Visible。</summary>
public sealed class VisibleWhenTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>bool 取反，用于“进行中时禁用按钮”这类绑定。</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is not true;
}

/// <summary>模组加载器的枚举值 → 中文显示名。</summary>
public sealed class ModLoaderDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        Core.Instances.ModLoader.Vanilla => "原版",
        Core.Instances.ModLoader.Forge => "Forge",
        Core.Instances.ModLoader.NeoForge => "NeoForge",
        Core.Instances.ModLoader.Fabric => "Fabric",
        Core.Instances.ModLoader.Quilt => "Quilt",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
