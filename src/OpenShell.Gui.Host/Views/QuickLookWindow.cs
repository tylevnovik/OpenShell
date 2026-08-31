using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenShell.Commands.Builtins;
using OpenShell.I18n;
using OpenShell.Items;
using OpenShell.Preview;

namespace OpenShell.Gui.Host.Views;

/// <summary>
/// Quick Look 预览窗口 (Avalonia 实现)。Per ADR-0030 §1.
/// 空格键触发 → 弹出居中预览 → Esc / 再按空格关闭 (per ADR-0030 §1).
/// 实现 <see cref="IQuickLookWindow"/> 接口, 由 <see cref="QuickLookCommand"/> 通过 DI 解析调用。
/// 不使用 axaml, 纯 C# code-behind (与 MainWindow / GlobalSearchWindow 风格一致)。
/// </summary>
/// <remarks>
/// 渲染各 <see cref="PreviewViewModel"/> 变体:
/// <list type="bullet">
///   <item><see cref="PreviewViewModel.Text"/> / <see cref="PreviewViewModel.Pdf"/>: ScrollViewer + TextBlock (等宽字体)。</item>
///   <item><see cref="PreviewViewModel.Code"/>: 解析 ANSI 转义为彩色 Run (keyword=红 / string=绿 / comment=黄)。</item>
///   <item><see cref="PreviewViewModel.Image"/>: Avalonia <see cref="Image"/> 控件加载 PNG 字节。</item>
///   <item><see cref="PreviewViewModel.Archive"/>: ListBox 列出包内条目 (Name + Size)。</item>
///   <item><see cref="PreviewViewModel.Video"/>: TextBlock 显示元数据。</item>
///   <item><see cref="PreviewViewModel.NotSupported"/>: TextBlock 显示原因。</item>
/// </list>
/// </remarks>
internal sealed class QuickLookWindow : Window, IQuickLookWindow
{
    // ANSI 转义正则 (per ECMA-48): 匹配 \x1b[Nm 形式, N 为数字。
    private static readonly Regex AnsiColorRegex = new(@"\x1b\[(\d+)m", RegexOptions.Compiled);

    private readonly TextBlock _titleText = new()
    {
        FontWeight = FontWeight.Bold,
        Margin = new Thickness(8, 4, 8, 0),
    };
    private readonly TextBlock _pathText = new()
    {
        FontSize = 11,
        Foreground = Brushes.Gray,
        Margin = new Thickness(8, 0, 8, 4),
    };
    private readonly ContentControl _contentArea = new();
    private Bitmap? _currentBitmap;
    private readonly II18nService? _i18n;

    public QuickLookWindow(II18nService? i18n = null)
    {
        // T-313: 从全局 DI 容器解析 II18nService (可选; 未注册时为 null, 回退硬编码英文)。
        _i18n = i18n ?? Program.Services?.GetService(typeof(II18nService)) as II18nService;

        Title = T("gui.quicklook.title");
        Width = 640;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // T-313: 订阅 LocaleChanged 事件，动态切换语言后刷新窗口标题。
        if (_i18n is not null)
        {
            _i18n.LocaleChanged += OnLocaleChanged;
        }

        // Avalonia 11: 附加属性 DockPanel.Dock 不能在对象初始化器中赋值, 需通过 SetDock 设置。Per MainWindow.cs 模式。
        var headerBorder = new Border
        {
            Background = Brushes.LightGray,
            Child = new StackPanel
            {
                Children = { _titleText, _pathText },
            },
        };
        DockPanel.SetDock(headerBorder, Dock.Top);

        Content = new DockPanel
        {
            Children =
            {
                headerBorder,
                _contentArea,
            },
        };

        // Esc / 空格关闭 (per ADR-0030 §1: 再按空格 / Esc 关闭)。
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || e.Key == Key.Space)
            {
                Close();
                e.Handled = true;
            }
        };

        Closed += (_, _) =>
        {
            // T-313: 关闭窗口时解除 LocaleChanged 订阅避免泄漏。
            if (_i18n is not null)
            {
                _i18n.LocaleChanged -= OnLocaleChanged;
            }
            DisposeBitmap();
        };
    }

    /// <inheritdoc />
    void IQuickLookWindow.Show(IItem item, PreviewViewModel? viewModel)
    {
        // QuickLookCommand 可能在后台线程调用; UI 操作必须切到 UI 线程 (per Avalonia 线程模型)。
        Dispatcher.UIThread.Post(() =>
        {
            _titleText.Text = item.Name;
            _pathText.Text = item.Path.Display;
            DisposeBitmap();
            _contentArea.Content = BuildPreviewControl(viewModel);

            if (!IsVisible)
                Show();
            else
                Activate();
        });
    }

    /// <summary>构建预览控件: 按 <see cref="PreviewViewModel"/> 变体分派。</summary>
    private Control BuildPreviewControl(PreviewViewModel? vm) => vm switch
    {
        null => BuildNotSupported(T("gui.quicklook.noPreview")),
        PreviewViewModel.Text t => BuildTextPreview(t.Content, t.Language, t.TotalLines, t.Truncated),
        PreviewViewModel.Code c => BuildCodePreview(c),
        PreviewViewModel.Image i => BuildImagePreview(i),
        PreviewViewModel.Archive a => BuildArchivePreview(a),
        PreviewViewModel.Pdf p => BuildPdfPreview(p),
        PreviewViewModel.Video v => BuildVideoPreview(v),
        PreviewViewModel.NotSupported ns => BuildNotSupported(ns.Reason),
        _ => BuildNotSupported(T("gui.quicklook.unknownType", vm.GetType().Name)),
    };

    /// <summary>创建 Dock.Top 的灰色 header TextBlock (Avalonia 11 附加属性需通过 SetDock 设置)。</summary>
    private static TextBlock CreateDockedHeader(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = Brushes.Gray,
            Margin = new Thickness(4),
        };
        DockPanel.SetDock(tb, Dock.Top);
        return tb;
    }

    /// <summary>文本预览: ScrollViewer + 等宽 TextBlock + 截断标记。</summary>
    private Control BuildTextPreview(string content, string? language, int totalLines, bool truncated)
    {
        var header = T("gui.quicklook.textHeader", language ?? "text", totalLines, truncated ? T("gui.quicklook.truncated") : "");
        var textBlock = new TextBlock
        {
            Text = content,
            FontFamily = new FontFamily("Consolas,Menlo,Monaco,monospace"),
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(4),
        };
        return new DockPanel
        {
            Children =
            {
                CreateDockedHeader(header),
                new ScrollViewer
                {
                    Content = textBlock,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
            },
        };
    }

    /// <summary>代码预览: 解析 ANSI 转义为彩色 Run, 等宽字体, 可滚动。</summary>
    private Control BuildCodePreview(PreviewViewModel.Code c)
    {
        var header = T("gui.quicklook.textHeader", c.Language, c.TotalLines, c.Truncated ? T("gui.quicklook.truncated") : "");
        var textBlock = BuildAnsiColoredTextBlock(c.HighlightedContent);

        return new DockPanel
        {
            Children =
            {
                CreateDockedHeader(header),
                new ScrollViewer
                {
                    Content = textBlock,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
            },
        };
    }

    /// <summary>
    /// 解析 ANSI 颜色转义 (\x1b[31m=红 / 32=绿 / 33=黄 / 0=reset) 为 Avalonia <see cref="Run"/>。
    /// 无 ANSI 的文本作为普通 Run (默认前景色)。
    /// </summary>
    private static TextBlock BuildAnsiColoredTextBlock(string content)
    {
        var textBlock = new TextBlock
        {
            FontFamily = new FontFamily("Consolas,Menlo,Monaco,monospace"),
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(4),
        };

        IBrush? currentBrush = null;
        var lastEnd = 0;
        var hasInlines = false;

        foreach (Match m in AnsiColorRegex.Matches(content))
        {
            // ANSI 码之前的文本。
            if (m.Index > lastEnd)
            {
                AddRun(textBlock, content[lastEnd..m.Index], currentBrush);
                hasInlines = true;
            }

            // 更新当前颜色 (per CodePreviewer: 31=keyword / 32=string / 33=comment / 0=reset)。
            var code = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            currentBrush = code switch
            {
                31 => Brushes.OrangeRed,
                32 => Brushes.ForestGreen,
                33 => Brushes.Goldenrod,
                0 => null,
                _ => currentBrush,
            };
            lastEnd = m.Index + m.Length;
        }

        // 剩余文本。
        if (lastEnd < content.Length)
        {
            AddRun(textBlock, content[lastEnd..], currentBrush);
            hasInlines = true;
        }

        // 无 inline (纯文本无 ANSI) → 直接设 Text。
        if (!hasInlines)
        {
            textBlock.Text = content;
        }

        return textBlock;
    }

    /// <summary>向 TextBlock.Inlines 添加一个 Run (惰性初始化 InlineCollection)。</summary>
    private static void AddRun(TextBlock textBlock, string text, IBrush? foreground)
    {
        if (string.IsNullOrEmpty(text)) return;
        textBlock.Inlines ??= new InlineCollection();
        textBlock.Inlines.Add(new Run { Text = text, Foreground = foreground });
    }

    /// <summary>图片预览: Avalonia <see cref="Image"/> 加载 PNG 字节流。</summary>
    private Control BuildImagePreview(PreviewViewModel.Image i)
    {
        try
        {
            using var ms = new MemoryStream(i.PngData);
            _currentBitmap = new Bitmap(ms);
            var image = new Image
            {
                Source = _currentBitmap,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var header = T("gui.quicklook.imageInfo", i.Width, i.Height, i.PngData.Length);
            return new DockPanel
            {
                Children =
                {
                    CreateDockedHeader(header),
                    new ScrollViewer { Content = image },
                },
            };
        }
        catch (Exception ex)
        {
            return BuildNotSupported(T("gui.quicklook.imageFailed", ex.Message));
        }
    }

    /// <summary>归档预览: ListBox 列出包内条目 (Name + Size)。Per ADR-0030 §2: 前 100 个条目。</summary>
    private Control BuildArchivePreview(PreviewViewModel.Archive a)
    {
        var header = T("gui.quicklook.entries", a.Entries.Count);
        var listBox = new ListBox
        {
            Margin = new Thickness(2),
            ItemsSource = a.Entries,
            ItemTemplate = new FuncDataTemplate<IItem>((_, _) =>
            {
                var name = new TextBlock { FontWeight = FontWeight.Bold };
                name.Bind(TextBlock.TextProperty, new Binding("Name"));
                var size = new TextBlock { FontSize = 11, Foreground = Brushes.Gray };
                size.Bind(TextBlock.TextProperty, new Binding("Size"));
                return new StackPanel
                {
                    Margin = new Thickness(2),
                    Children = { name, size },
                };
            }),
        };
        return new DockPanel
        {
            Children =
            {
                CreateDockedHeader(header),
                listBox,
            },
        };
    }

    /// <summary>PDF 预览: TextBlock 显示提取文本 + 估算页数。</summary>
    private Control BuildPdfPreview(PreviewViewModel.Pdf p)
    {
        var header = T("gui.quicklook.pdfPages", p.EstimatedPageCount);
        var textBlock = new TextBlock
        {
            Text = string.IsNullOrEmpty(p.ExtractedText) ? T("gui.quicklook.noText") : p.ExtractedText,
            FontFamily = new FontFamily("Consolas,Menlo,Monaco,monospace"),
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(4),
        };
        return new DockPanel
        {
            Children =
            {
                CreateDockedHeader(header),
                new ScrollViewer
                {
                    Content = textBlock,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
            },
        };
    }

    /// <summary>视频预览: 有缩略图则显示缩略图, 下方叠加时长 + 元数据文本。</summary>
    private Control BuildVideoPreview(PreviewViewModel.Video v)
    {
        var durationStr = v.Duration is { } d ? $"{d:hh\\:mm\\:ss}" : T("gui.quicklook.durationUnknown");
        var metadata = v.Metadata ?? T("gui.quicklook.metadataUnavailable");
        var textBlock = new TextBlock
        {
            Text = $"{durationStr}\n{metadata}",
            FontFamily = new FontFamily("Consolas,Menlo,Monaco,monospace"),
            Margin = new Thickness(4),
        };

        var body = new ScrollViewer { Content = textBlock };
        if (v.ThumbnailPng is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(v.ThumbnailPng);
                DisposeBitmap();
                _currentBitmap = new Bitmap(ms);
                body = new ScrollViewer
                {
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new Image
                            {
                                Source = _currentBitmap,
                                Stretch = Stretch.Uniform,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                MaxWidth = v.ThumbnailWidth > 0 ? v.ThumbnailWidth : 320,
                            },
                            textBlock,
                        },
                    },
                };
            }
            catch
            {
                // 缩略图解码失败时退回纯元数据视图。
            }
        }

        return new DockPanel
        {
            Children =
            {
                CreateDockedHeader(T("gui.quicklook.video")),
                body,
            },
        };
    }

    /// <summary>不支持预览: TextBlock 显示原因。</summary>
    private static Control BuildNotSupported(string reason)
    {
        return new TextBlock
        {
            Text = $"⚠ {reason}",
            FontSize = 14,
            Foreground = Brushes.DarkOrange,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16),
        };
    }

    /// <summary>释放当前 Bitmap (切换预览 / 关闭窗口时调用)。</summary>
    private void DisposeBitmap()
    {
        _currentBitmap?.Dispose();
        _currentBitmap = null;
    }

    /// <summary>T-313: 翻译 key; i18n 未注入时回退到 key 本身。</summary>
    private string T(string key, params object[] args) => _i18n?.Translate(key, args) ?? key;

    /// <summary>T-313: LocaleChanged 事件处理：在 UI 线程刷新窗口标题。</summary>
    private void OnLocaleChanged(object? sender, string e)
    {
        Dispatcher.UIThread.Post(() => Title = T("gui.quicklook.title"));
    }
}
