using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using OpenShell.Gui.Abstractions;
using OpenShell.I18n;
using OpenShell.Paths;

namespace OpenShell.Gui.Host.Services;

/// <summary>
/// Avalonia 实现 <see cref="IDialogService"/>。Per ADR-0043 §3.
/// MessageBox / Input 用自定义 Window（项目未引用 Avalonia.Dialogs 包，无 ContentDialog）。
/// 文件 / 文件夹对话框用 <see cref="IStorageProvider"/> 原生 picker.
/// 自定义对话框 (ShowCustomAsync) 委托给 <see cref="IDialogHost"/>.
/// 所有 ShowXxxAsync 在 UI 线程调用并完成（Per ADR-0043 §10 线程模型）。
/// </summary>
internal sealed class AvaloniaDialogService : IDialogService
{
    private readonly IDialogHost _dialogHost;

    // T-310: i18n 服务（可选; 未注册时为 null, 回退硬编码英文）。
    private readonly II18nService? _i18n;

    /// <summary>
    /// 构造 AvaloniaDialogService。
    /// </summary>
    /// <param name="dialogHost">对话框宿主, 用于 ShowCustomAsync 委托 (Per ADR-0043 §2)。</param>
    public AvaloniaDialogService(IDialogHost dialogHost, II18nService? i18n = null)
    {
        _dialogHost = dialogHost ?? throw new ArgumentNullException(nameof(dialogHost));

        // T-310: 从全局 DI 容器解析 II18nService。
        _i18n = i18n ?? Program.Services?.GetService(typeof(II18nService)) as II18nService;
    }

    /// <summary>T-310: 翻译 key; i18n 未注入时回退到 key 本身。</summary>
    private string T(string key, params object[] args) => _i18n?.Translate(key, args) ?? key;

    /// <summary>
    /// 懒解析 MainWindow：DI 容器在 Avalonia Application 启动前就构建，
    /// 此时 MainWindow 尚未创建。所有 ShowXxxAsync 调用时 MainWindow 已就绪。
    /// </summary>
    private static Window MainWindow
    {
        get
        {
            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is { } window)
            {
                return window;
            }

            throw new InvalidOperationException(
                "AvaloniaDialogService 要求已运行的 IClassicDesktopStyleApplicationLifetime 且 MainWindow 已创建。");
        }
    }

    /// <inheritdoc />
    public async Task<DialogResult> ShowMessageBoxAsync(MessageBoxOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dialog = new MessageBoxWindow(options);
        return await dialog.ShowDialogAsync(MainWindow);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ItemPath>?> ShowOpenFileDialogAsync(FileDialogOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var storage = MainWindow.StorageProvider;
        var startLocation = await GetSuggestedStartLocation(storage, options.InitialDirectory);

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = options.Title ?? T("gui.dialog.openTitle"),
            AllowMultiple = options.AllowMultiple,
            FileTypeFilter = MapFilePickerFileTypes(options.Filters),
            SuggestedStartLocation = startLocation,
        });

        ct.ThrowIfCancellationRequested();
        if (files.Count == 0) return null;

        return files.Select(ToItemPath).ToList();
    }

    /// <inheritdoc />
    public async Task<ItemPath?> ShowSaveFileDialogAsync(FileDialogOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var storage = MainWindow.StorageProvider;
        var startLocation = await GetSuggestedStartLocation(storage, options.InitialDirectory);

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = options.Title ?? T("gui.dialog.saveAsTitle"),
            DefaultExtension = options.DefaultExtension,
            SuggestedFileName = options.DefaultFileName,
            FileTypeChoices = MapFilePickerFileTypes(options.Filters),
            SuggestedStartLocation = startLocation,
        });

        ct.ThrowIfCancellationRequested();
        return file is null ? null : ToItemPath(file);
    }

    /// <inheritdoc />
    public async Task<ItemPath?> ShowFolderBrowserAsync(FolderDialogOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var storage = MainWindow.StorageProvider;
        var startLocation = await GetSuggestedStartLocation(storage, options.InitialDirectory);

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = options.Title ?? T("gui.dialog.selectFolderTitle"),
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
        });

        ct.ThrowIfCancellationRequested();
        if (folders.Count == 0) return null;

        return ToItemPath(folders[0]);
    }

    /// <inheritdoc />
    public async Task<string?> ShowInputAsync(InputDialogOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dialog = new InputDialogWindow(options);
        return await dialog.ShowDialogAsync(MainWindow);
    }

    /// <inheritdoc />
    /// <summary>
    /// 显示自定义对话框视图。Per ADR-0043 §2, §8.
    /// 委托给 <see cref="IDialogView{T}.ShowAsync"/> 并传入 <see cref="IDialogHost"/> (Avalonia 实现)。
    /// View 实现一般是 Avalonia Window, 由 IDialogHost 调 ShowDialog 弹出模态。
    /// </summary>
    public async Task<T?> ShowCustomAsync<T>(IDialogView<T> view, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        ct.ThrowIfCancellationRequested();
        // view 实现内部用 _dialogHost 弹模态窗口。返回 default(T) 表示用户取消。
        var result = await view.ShowAsync(_dialogHost, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 把 OpenShell <see cref="FileFilter"/> 列表转为 Avalonia <see cref="FilePickerFileType"/> 列表。
    /// 空列表返回 null（Avalonia 此时显示所有文件）。
    /// </summary>
    private static IReadOnlyList<FilePickerFileType>? MapFilePickerFileTypes(IReadOnlyList<FileFilter> filters)
    {
        if (filters.Count == 0) return null;
        return filters
            .Select(f => new FilePickerFileType(f.Name)
            {
                Patterns = f.Patterns.ToList(),
            })
            .ToList();
    }

    /// <summary>
    /// 把 Avalonia <see cref="IStorageItem"/> 转为 OpenShell <see cref="ItemPath"/>.
    /// Provider 固定为 "fs"（StorageProvider 选出的都是本地文件系统路径）。
    /// InternalPath 用 '/' 作分隔符（Per ADR-0006 路径模型）。
    /// </summary>
    private static ItemPath ToItemPath(IStorageItem item)
    {
        // item.Path 是 Uri；LocalPath 给 OS 原生路径，统一替换为 '/'
        return new ItemPath
        {
            Provider = "fs",
            InternalPath = item.Path.LocalPath.Replace('\\', '/'),
        };
    }

    /// <summary>
    /// 把 OpenShell <see cref="ItemPath"/> 转为 Avalonia <see cref="IStorageFolder"/> 作为 picker 起始位置。
    /// 仅支持 fs provider；路径不存在或无效时返回 null（picker 退化为默认位置）。
    /// </summary>
    private static async Task<IStorageFolder?> GetSuggestedStartLocation(IStorageProvider storage, ItemPath? initial)
    {
        if (initial is not { } path) return null;
        if (path.Provider != "fs") return null;
        var localPath = path.InternalPath;
        if (string.IsNullOrEmpty(localPath)) return null;

        try
        {
            // 规范化为绝对路径（相对路径相对 Environment.CurrentDirectory）
            var fullPath = Path.GetFullPath(localPath);

            // 构造 file:// URI：Windows 盘符路径（C:\...）Uri 可直接识别；
            // Unix 绝对路径（/home/...）需显式加 file:// 前缀
            if (!Uri.TryCreate(fullPath, UriKind.Absolute, out var uri))
            {
                if (!Uri.TryCreate("file://" + fullPath, UriKind.Absolute, out uri))
                {
                    return null;
                }
            }

            return await storage.TryGetFolderFromPathAsync(uri);
        }
        catch
        {
            // 路径解析失败（不存在 / 权限不足 / 非法字符）—— 退化到 picker 默认位置
            return null;
        }
    }
}
