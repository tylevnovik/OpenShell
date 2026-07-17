using OpenShell.Paths;

namespace OpenShell.Gui.Abstractions;

/// <summary>
/// 所有 GUI 对话框的统一入口。Per ADR-0043.
/// ViewModel 通过 DI 注入；CLI Host 提供文本降级实现，GUI Host 用 Avalonia 原生对话框。
/// 接口位于 Core / Abstractions 层，实现在 Host 层（参考 ADR-0014 Host Bridge 做法）。
/// </summary>
public interface IDialogService
{
    /// <summary>显示消息框（确认 / 警告 / 错误 / 询问）。</summary>
    Task<DialogResult> ShowMessageBoxAsync(MessageBoxOptions options, CancellationToken ct = default);

    /// <summary>显示打开文件对话框，支持多选。返回 null 表示用户取消。</summary>
    Task<IReadOnlyList<ItemPath>?> ShowOpenFileDialogAsync(FileDialogOptions options, CancellationToken ct = default);

    /// <summary>显示保存文件对话框。返回 null 表示用户取消。</summary>
    Task<ItemPath?> ShowSaveFileDialogAsync(FileDialogOptions options, CancellationToken ct = default);

    /// <summary>显示文件夹浏览对话框。返回 null 表示用户取消。</summary>
    Task<ItemPath?> ShowFolderBrowserAsync(FolderDialogOptions options, CancellationToken ct = default);

    /// <summary>显示自定义输入对话框（重命名 / 新建文件夹 / 跳转路径 等）。返回 null 表示用户取消。</summary>
    Task<string?> ShowInputAsync(InputDialogOptions options, CancellationToken ct = default);

    /// <summary>
    /// 显示自定义对话框视图（About / Preferences / Find / Properties 等）。Per ADR-0043 §2, §8.
    /// ViewModel 提供数据，View 提供 Avalonia 渲染。返回 null 表示用户取消。
    /// 默认实现委托给 <see cref="IDialogView{T}.ShowAsync"/>。
    /// </summary>
    /// <typeparam name="T">视图返回的结果类型。</typeparam>
    /// <param name="view">自定义对话框视图实例。</param>
    /// <param name="ct">取消令牌。</param>
    Task<T?> ShowCustomAsync<T>(IDialogView<T> view, CancellationToken ct = default);
}

/// <summary>
/// 自定义对话框视图抽象。Per ADR-0043 §2, §8.
/// 由 GUI Host 实现具体渲染（Avalonia 自定义 Window），由 ViewModel 提供数据。
/// 接口位于 Core / Abstractions 层，不引用 Avalonia.* 命名空间。
/// </summary>
/// <typeparam name="T">对话框返回结果类型。</typeparam>
public interface IDialogView<T>
{
    /// <summary>对话框标题。</summary>
    string Title { get; }

    /// <summary>
    /// 显示对话框。由 <see cref="IDialogHost"/> 提供宿主窗口（owner），实现可用 Avalonia ShowDialog。
    /// </summary>
    /// <param name="host">对话框宿主，提供 owner 窗口管理。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>用户选择的结果；取消时返回 default。</returns>
    Task<T> ShowAsync(IDialogHost host, CancellationToken ct);
}

/// <summary>
/// 对话框宿主抽象。Per ADR-0043 §2.
/// GUI Host 实现负责把任意 view（实际是 Avalonia Window）作为模态子窗口显示。
/// 接口位于 Core / Abstractions 层，不引用 Avalonia.* 命名空间。
/// </summary>
public interface IDialogHost
{
    /// <summary>
    /// 显示模态对话框视图，返回结果。view 实例具体类型由实现决定（Avalonia Window / CLI 文本 prompt）。
    /// </summary>
    /// <typeparam name="TResult">对话框返回结果类型。</typeparam>
    /// <param name="view">对话框视图实例（实现 IDialogView{TResult} 的 Avalonia Window）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<TResult> ShowAsync<TResult>(object view, CancellationToken ct);
}

/// <summary>消息框类型，决定图标与默认按钮。Per ADR-0043 §2.</summary>
public enum MessageBoxKind { Information, Warning, Error, Question }

/// <summary>消息框按钮组合，决定显示哪些按钮。Per ADR-0043 §2.</summary>
public enum MessageBoxButtons { OK, OKCancel, YesNo, YesNoCancel }

/// <summary>对话框统一结果枚举。Per ADR-0043 §2. Esc 永远返回 Cancel。</summary>
public enum DialogResult { OK, Cancel, Yes, No }

/// <summary>
/// 消息框参数。Per ADR-0043 §2.
/// 不可变 record，便于单测断言。
/// </summary>
public sealed record MessageBoxOptions
{
    /// <summary>消息框标题（必填）。</summary>
    public required string Title { get; init; }

    /// <summary>消息正文（必填）。</summary>
    public required string Message { get; init; }

    /// <summary>消息类型，影响图标。默认 Information。</summary>
    public MessageBoxKind Kind { get; init; } = MessageBoxKind.Information;

    /// <summary>按钮组合，默认 OK。</summary>
    public MessageBoxButtons Buttons { get; init; } = MessageBoxButtons.OK;

    /// <summary>折叠区域文本（堆栈 / 上下文），可选。参考 VS Code。</summary>
    public string? Detail { get; init; }

    /// <summary>相关路径，用于在文件管理器里跳转。可选。</summary>
    public ItemPath? RelatedPath { get; init; }
}

/// <summary>
/// 文件对话框参数（打开 / 另存为共用）。Per ADR-0043 §2.
/// </summary>
public sealed record FileDialogOptions
{
    /// <summary>对话框标题，可选。</summary>
    public string? Title { get; init; }

    /// <summary>起始目录，可选。null 时从最近使用目录回退。</summary>
    public ItemPath? InitialDirectory { get; init; }

    /// <summary>文件过滤器列表。</summary>
    public IReadOnlyList<FileFilter> Filters { get; init; } = Array.Empty<FileFilter>();

    /// <summary>是否允许多选。仅对打开对话框生效。</summary>
    public bool AllowMultiple { get; init; } = false;

    /// <summary>默认扩展名（不含点），仅对另存为生效。</summary>
    public string? DefaultExtension { get; init; }

    /// <summary>默认文件名，仅对另存为生效。</summary>
    public string? DefaultFileName { get; init; }
}

/// <summary>文件过滤器，如 new FileFilter("Text Files", new[] { "*.txt", "*.md" })。Per ADR-0043 §2.</summary>
public sealed record FileFilter(string Name, IReadOnlyList<string> Patterns);

/// <summary>
/// 文件夹浏览对话框参数。Per ADR-0043 §2.
/// </summary>
public sealed record FolderDialogOptions
{
    /// <summary>对话框标题，可选。</summary>
    public string? Title { get; init; }

    /// <summary>起始目录，可选。</summary>
    public ItemPath? InitialDirectory { get; init; }
}

/// <summary>
/// 输入对话框参数（重命名 / 新建文件夹 / 跳转路径 等）。Per ADR-0043 §2.
/// </summary>
public sealed record InputDialogOptions
{
    /// <summary>对话框标题（必填）。</summary>
    public required string Title { get; init; }

    /// <summary>输入框上方标签，可选。</summary>
    public string? Label { get; init; }

    /// <summary>默认值，可选。空回车时返回此值。</summary>
    public string? DefaultValue { get; init; }

    /// <summary>占位提示文本，可选。</summary>
    public string? Placeholder { get; init; }

    /// <summary>
    /// 校验函数：返回 null 表示通过，否则返回错误消息（重新输入）。可选。
    /// </summary>
    public Func<string, string?>? Validator { get; init; }
}
