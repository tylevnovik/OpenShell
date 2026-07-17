using System.Globalization;
using Avalonia.Data.Converters;
using OpenShell.I18n;
using OpenShell.Items;

namespace OpenShell.Gui.Host.Converters;

/// <summary>
/// 文件项类型格式化转换器。Explorer 风格：
/// - Directory/Container 显示 "File folder"
/// - 其他类型显示 ContentType 扩展名（如 "txt"、"cs"）或 "File"
/// </summary>
public sealed class ItemTypeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IItem item) return string.Empty;

        if (item.Kind is ItemKind.Directory or ItemKind.Container)
            return I18nAccessor.Translate("gui.type.folder");

        // ContentType 通常带 "text/plain" 这种 MIME 形式，取斜杠后部分作为扩展名
        if (!string.IsNullOrEmpty(item.ContentType))
        {
            var idx = item.ContentType.LastIndexOf('/');
            return idx >= 0 ? item.ContentType[(idx + 1)..] : item.ContentType;
        }

        return I18nAccessor.Translate("gui.type.file");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
