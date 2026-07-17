using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Layout;
using OpenShell.Items;

namespace OpenShell.Gui.Host.Converters;

/// <summary>
/// 文件项图标转换器。用几何图形画 Explorer 风格图标：
/// - 文件夹：黄色折叠矩形（前面板 + 后面板 + 折叠标签）
/// - 文件：白色矩形 + 右上角折角 + 底部横线（模拟文档）
/// 返回 Control（包含 Path/Rectangle），由 ContentControl 承载。
/// </summary>
public sealed class ItemIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IItem item) return null;

        // V-15: 支持通过 ConverterParameter 传入图标尺寸，跟随视图模式变化
        var size = 16.0;
        if (parameter is int intVal) size = intVal;
        else if (parameter is double dblVal) size = dblVal;

        return item.Kind is ItemKind.Directory or ItemKind.Container
            ? CreateFolderIcon(size)
            : CreateFileIcon(item.ContentType, item.Name, size);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>创建文件夹图标：黄色折叠矩形。V-15: size 参数控制图标尺寸（用 Viewbox 缩放）。</summary>
    private static Control CreateFolderIcon(double size)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };

        // 后面板（深黄色，露出折叠标签部分）
        var back = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse("M 1,4 L 6,4 L 7,5.5 L 15,5.5 L 15,13 L 1,13 Z"),
            Fill = new SolidColorBrush(Color.Parse("#E8C547")),
        };
        // 前面板（亮黄色）
        var front = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse("M 1,6 L 15,6 L 14,13 L 0,13 Z"),
            Fill = new SolidColorBrush(Color.Parse("#FFD45E")),
            Stroke = new SolidColorBrush(Color.Parse("#C9A227")),
            StrokeThickness = 0.5,
        };
        canvas.Children.Add(back);
        canvas.Children.Add(front);
        // V-15: size != 16 时用 Viewbox 缩放
        return size == 16 ? canvas : new Viewbox { Child = canvas, Width = size, Height = size, Stretch = Stretch.Uniform };
    }

    /// <summary>创建文件图标：白色矩形 + 折角 + 横线。V-15: size 参数控制图标尺寸（用 Viewbox 缩放）。</summary>
    private static Control CreateFileIcon(string? contentType, string? name, double size)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };

        // 根据扩展名选颜色（Explorer 风格的常见配色）
        var (bodyColor, edgeColor) = GetFileColors(contentType, name);

        // 文件主体（带折角）
        var body = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse("M 3,1 L 10,1 L 13,4 L 13,15 L 3,15 Z"),
            Fill = new SolidColorBrush(bodyColor),
            Stroke = new SolidColorBrush(edgeColor),
            StrokeThickness = 0.5,
        };
        // 折角（右上角三角）
        var fold = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse("M 10,1 L 10,4 L 13,4 Z"),
            Fill = new SolidColorBrush(edgeColor),
        };
        // 横线（模拟文字行）
        var line1 = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse("M 5,8 L 11,8"),
            Stroke = new SolidColorBrush(Color.Parse("#999999")),
            StrokeThickness = 0.8,
        };
        var line2 = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse("M 5,10.5 L 11,10.5"),
            Stroke = new SolidColorBrush(Color.Parse("#999999")),
            StrokeThickness = 0.8,
        };
        var line3 = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse("M 5,13 L 9,13"),
            Stroke = new SolidColorBrush(Color.Parse("#999999")),
            StrokeThickness = 0.8,
        };

        canvas.Children.Add(body);
        canvas.Children.Add(fold);
        canvas.Children.Add(line1);
        canvas.Children.Add(line2);
        canvas.Children.Add(line3);
        // V-15: size != 16 时用 Viewbox 缩放
        return size == 16 ? canvas : new Viewbox { Child = canvas, Width = size, Height = size, Stretch = Stretch.Uniform };
    }

    /// <summary>根据 ContentType 返回文件图标配色（body, edge）。</summary>
    private static (Color body, Color edge) GetFileColors(string? contentType, string? name)
    {
        // ContentType 前缀匹配（优先于扩展名）
        if (!string.IsNullOrEmpty(contentType))
        {
            // 文本类：白色 + 灰边
            if (contentType.StartsWith("text/")) return (Colors.White, Color.Parse("#888888"));
            // 图片类：浅蓝 + 蓝边
            if (contentType.StartsWith("image/")) return (Color.Parse("#E3F2FD"), Color.Parse("#1976D2"));
            // 音频/视频：浅紫 + 紫边
            if (contentType.StartsWith("audio/") || contentType.StartsWith("video/"))
                return (Color.Parse("#F3E5F5"), Color.Parse("#7B1FA2"));
            // 应用/可执行：浅红 + 红边
            if (contentType.StartsWith("application/")) return (Color.Parse("#FFEBEE"), Color.Parse("#C62828"));
        }

        // V-16: 扩展名兜底——ContentType 未匹配时按文件名扩展名着色
        var ext = System.IO.Path.GetExtension(name)?.ToLowerInvariant();
        if (ext is not null)
        {
            // PDF：粉红 + 深红边
            if (ext == ".pdf") return (Color.Parse("#FCE4EC"), Color.Parse("#C62828"));
            // 可执行：浅红 + 深红边
            if (ext is ".exe" or ".msi" or ".bat" or ".cmd") return (Color.Parse("#FFEBEE"), Color.Parse("#C62828"));
            // 归档：浅橙 + 深橙边
            if (ext is ".zip" or ".7z" or ".rar" or ".gz" or ".tar") return (Color.Parse("#FFF3E0"), Color.Parse("#E65100"));
            // 代码：浅绿 + 深绿边
            if (ext is ".cs" or ".ts" or ".js" or ".py" or ".go" or ".rs" or ".java") return (Color.Parse("#E8F5E9"), Color.Parse("#2E7D32"));
            // 数据：浅蓝 + 深蓝边
            if (ext is ".json" or ".xml" or ".yaml" or ".yml") return (Color.Parse("#E3F2FD"), Color.Parse("#1565C0"));
            // 标记/样式：浅蓝 + 蓝边
            if (ext is ".html" or ".css") return (Color.Parse("#E3F2FD"), Color.Parse("#1976D2"));
            // 文本/日志：近白 + 灰边
            if (ext is ".md" or ".txt" or ".log") return (Color.Parse("#FAFAFA"), Color.Parse("#888888"));
            // 图片：浅蓝 + 蓝边
            if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".svg" or ".ico") return (Color.Parse("#E3F2FD"), Color.Parse("#1976D2"));
            // 视频：浅紫 + 紫边
            if (ext is ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv") return (Color.Parse("#F3E5F5"), Color.Parse("#7B1FA2"));
            // 音频：浅紫 + 紫边
            if (ext is ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a") return (Color.Parse("#F3E5F5"), Color.Parse("#7B1FA2"));
            // Word：浅蓝 + 深蓝边
            if (ext is ".doc" or ".docx") return (Color.Parse("#E3F2FD"), Color.Parse("#1565C0"));
            // Excel：浅绿 + 深绿边
            if (ext is ".xls" or ".xlsx") return (Color.Parse("#E8F5E9"), Color.Parse("#2E7D32"));
            // PowerPoint：浅橙 + 深橙边
            if (ext is ".ppt" or ".pptx") return (Color.Parse("#FFF3E0"), Color.Parse("#E65100"));
        }

        return (Colors.White, Color.Parse("#AAAAAA"));
    }
}
