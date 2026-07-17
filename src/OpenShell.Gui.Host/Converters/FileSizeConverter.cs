using System.Globalization;
using Avalonia.Data.Converters;
using OpenShell.Items;

namespace OpenShell.Gui.Host.Converters;

/// <summary>
/// 文件大小格式化转换器。Explorer 风格：
/// - null（目录）显示空字符串
/// - &lt; 1KB 显示字节数
/// - &lt; 1MB 显示 KB
/// - &lt; 1GB 显示 MB
/// - 否则显示 GB
/// 保留 1 位小数（字节数除外）。
/// </summary>
public sealed class FileSizeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long size) return string.Empty;
        if (size < 1024) return size.ToString("N0", culture) + " B";
        if (size < 1024 * 1024) return (size / 1024.0).ToString("N1", culture) + " KB";
        if (size < 1024 * 1024 * 1024) return (size / (1024.0 * 1024)).ToString("N1", culture) + " MB";
        return (size / (1024.0 * 1024 * 1024)).ToString("N1", culture) + " GB";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
