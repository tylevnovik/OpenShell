using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenShell.Gui.Host.ViewModels;
using OpenShell.I18n;
using OpenShell.Items;
using OpenShell.Preview;
using ReactiveUI;

namespace OpenShell.Gui.Host.Views;

/// <summary>主窗口内嵌预览面板。选中项改变后通过 IPreviewService 异步刷新。</summary>
public partial class PreviewPane : UserControl
{
    private readonly IPreviewService? _previewService;
    private readonly II18nService? _i18n;
    private MainViewModel? _viewModel;
    private PaneViewModel? _pane;
    private IDisposable? _activePaneSubscription;
    private CancellationTokenSource? _previewCts;
    private Bitmap? _bitmap;

    public PreviewPane() : this(null, null)
    {
    }

    public PreviewPane(IPreviewService? previewService, II18nService? i18n = null)
    {
        InitializeComponent();
        _previewService = previewService ?? Program.Services?.GetService<IPreviewService>();
        _i18n = i18n ?? Program.Services?.GetService<II18nService>();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => DisposeSubscriptions();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DisposeSubscriptions();
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is null)
            return;

        _activePaneSubscription = _viewModel.WhenAnyValue(vm => vm.ActivePane)
            .Subscribe(BindPane);
        BindPane(_viewModel.ActivePane);
    }

    private void BindPane(PaneViewModel? pane)
    {
        if (_pane is not null)
            _pane.SelectedItems.CollectionChanged -= OnSelectedItemsChanged;

        _pane = pane;
        if (_pane is not null)
            _pane.SelectedItems.CollectionChanged += OnSelectedItemsChanged;

        _ = RefreshAsync();
    }

    private void OnSelectedItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => _ = RefreshAsync();

    /// <summary>按当前主选中项刷新预览；公开供 headless 合规测试等待完成。</summary>
    public async Task RefreshAsync()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;
        var item = _pane?.SelectedItems.FirstOrDefault();

        if (item is null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                DisposeBitmap();
                PreviewContent.Content = null;
                StatusText.Text = T("gui.previewPane.empty");
            });
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            DisposeBitmap();
            PreviewContent.Content = null;
            StatusText.Text = T("gui.previewPane.loading");
        });
        if (_previewService is null || !_previewService.CanPreview(item))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ct.IsCancellationRequested)
                    StatusText.Text = T("gui.previewPane.unsupported");
            });
            return;
        }

        try
        {
            var preview = await _previewService.CreatePreviewAsync(
                item,
                new PreviewOptions(MaxWidth: 400, MaxHeight: 300),
                ct);
            ct.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() => ApplyPreview(preview));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ct.IsCancellationRequested)
                    StatusText.Text = ex.Message;
            });
        }
    }

    private void ApplyPreview(PreviewViewModel? preview)
    {
        PreviewContent.Content = preview switch
        {
            PreviewViewModel.Text text => BuildText(text.Content),
            PreviewViewModel.Code code => BuildText(code.HighlightedContent),
            PreviewViewModel.Image image => BuildImage(image),
            PreviewViewModel.Archive archive => BuildArchive(archive),
            PreviewViewModel.Pdf pdf => BuildText(pdf.ExtractedText),
            PreviewViewModel.Video video => BuildVideo(video),
            PreviewViewModel.NotSupported unsupported => BuildText(unsupported.Reason),
            _ => BuildText(T("gui.previewPane.unsupported")),
        };
        StatusText.Text = preview is PreviewViewModel.NotSupported
            ? T("gui.previewPane.unsupported")
            : T("gui.previewPane.ready");
    }

    private static Control BuildText(string text)
        => new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Consolas,Menlo,Monaco,monospace"),
                TextWrapping = TextWrapping.Wrap,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

    private Control BuildImage(PreviewViewModel.Image image)
    {
        using var stream = new MemoryStream(image.PngData, writable: false);
        _bitmap = new Bitmap(stream);
        return new ScrollViewer
        {
            Content = new Avalonia.Controls.Image
            {
                Source = _bitmap,
                Stretch = Stretch.Uniform,
                MaxWidth = image.Width,
                MaxHeight = image.Height,
            },
        };
    }

    /// <summary>视频预览: 有缩略图则显示缩略图 + 元数据; 否则仅元数据文本。</summary>
    private Control BuildVideo(PreviewViewModel.Video video)
    {
        var metadataBlock = new TextBlock
        {
            Text = video.Metadata ?? T("gui.previewPane.noMetadata"),
            FontFamily = new FontFamily("Consolas,Menlo,Monaco,monospace"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };

        if (video.ThumbnailPng is null || video.ThumbnailPng.Length == 0)
            return new ScrollViewer { Content = metadataBlock };

        using var stream = new MemoryStream(video.ThumbnailPng, writable: false);
        _bitmap = new Bitmap(stream);
        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Children =
                {
                    new Avalonia.Controls.Image
                    {
                        Source = _bitmap,
                        Stretch = Stretch.Uniform,
                        MaxWidth = video.ThumbnailWidth > 0 ? video.ThumbnailWidth : ThumbnailFallbackEdge,
                        MaxHeight = video.ThumbnailHeight > 0 ? video.ThumbnailHeight : ThumbnailFallbackEdge,
                    },
                    metadataBlock,
                },
            },
        };
    }

    private const int ThumbnailFallbackEdge = 320;

    private static Control BuildArchive(PreviewViewModel.Archive archive)
    {
        var list = new ListBox { ItemsSource = archive.Entries };
        list.ItemTemplate = new FuncDataTemplate<IItem>((item, _) =>
            new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        return list;
    }

    private void DisposeSubscriptions()
    {
        _activePaneSubscription?.Dispose();
        _activePaneSubscription = null;
        if (_pane is not null)
            _pane.SelectedItems.CollectionChanged -= OnSelectedItemsChanged;
        _pane = null;
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        DisposeBitmap();
    }

    private void DisposeBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private string T(string key, params object[] args)
        => _i18n?.Translate(key, args) ?? key switch
        {
            "gui.previewPane.empty" => "Select an item to preview",
            "gui.previewPane.loading" => "Loading preview...",
            "gui.previewPane.ready" => "Preview ready",
            "gui.previewPane.unsupported" => "Preview is not supported",
            "gui.previewPane.noMetadata" => "No metadata available",
            _ => key,
        };
}
