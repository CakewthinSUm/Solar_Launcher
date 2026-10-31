using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Solar.App.Converters;

/// <summary>bool → Visibility，用于 x:Bind 场景下控制元素显隐。</summary>
public sealed class VisibleWhenTrueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

/// <summary>bool → 取反的 Visibility，用于「有内容时隐藏空状态」。</summary>
public sealed class CollapsedWhenTrueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is not Visibility.Visible;
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

/// <summary>
/// 本地图片路径 → ImageSource。路径为空或文件不存在时返回 null，
/// 由界面上的占位图标顶上。
/// </summary>
public sealed class PathToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return new BitmapImage
            {
                // 换图标时文件名可能不变（都是 icon.png），不能用缓存里的旧图
                CreateOptions = BitmapCreateOptions.IgnoreImageCache,
                UriSource = new Uri(path),
            };
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>模组加载器的枚举值 → 中文显示名。</summary>
public sealed class ModLoaderDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        Core.Versions.ModLoader.Vanilla => "原版",
        Core.Versions.ModLoader.Forge => "Forge",
        Core.Versions.ModLoader.NeoForge => "NeoForge",
        Core.Versions.ModLoader.Fabric => "Fabric",
        Core.Versions.ModLoader.Quilt => "Quilt",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
