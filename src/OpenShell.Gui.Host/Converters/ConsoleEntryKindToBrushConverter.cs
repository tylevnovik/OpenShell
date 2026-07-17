using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenShell.Gui.Host.ViewModels;

namespace OpenShell.Gui.Host.Converters;

/// <summary>
/// 控制台输出条目类型 → 前景色转换器。
/// Input = 白色、Output = 浅灰色、Error = 红色。
/// </summary>
public sealed class ConsoleEntryKindToBrushConverter : IValueConverter
{
    private static readonly IBrush InputBrush = new SolidColorBrush(Colors.White);
    private static readonly IBrush OutputBrush = new SolidColorBrush(Colors.LightGray);
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Colors.Red);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ConsoleEntryKind kind)
        {
            return kind switch
            {
                ConsoleEntryKind.Input => InputBrush,
                ConsoleEntryKind.Output => OutputBrush,
                ConsoleEntryKind.Error => ErrorBrush,
                _ => OutputBrush,
            };
        }
        return OutputBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
