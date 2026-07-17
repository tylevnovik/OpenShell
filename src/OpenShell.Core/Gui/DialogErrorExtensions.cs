using OpenShell.Errors;

namespace OpenShell.Gui.Abstractions;

/// <summary>
/// <see cref="IDialogService"/> 与 <see cref="ErrorRecord"/> 桥接扩展方法。Per ADR-0043 §7.
/// 把 <see cref="ErrorRecord"/> 转换为 <see cref="MessageBoxOptions"/> 并显示。
/// 多错误聚合显示；严重错误可选走 YesNo 重试对话框。
/// 此类位于 Core / Abstractions 层, 不引用 Avalonia.* 命名空间, 可单测。
/// </summary>
public static class DialogErrorExtensions
{
    /// <summary>
    /// 显示单个错误为消息框。Per ADR-0043 §7.
    /// 严重错误 (PermissionDenied / IOError) 默认 YesNoCancel 询问重试；
    /// 其他错误默认 OK 仅提示。
    /// </summary>
    /// <param name="dialogs">对话框服务实例。</param>
    /// <param name="error">错误记录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>对话框结果。Yes 表示用户选择重试。</returns>
    public static async Task<DialogResult> ShowErrorAsync(
        this IDialogService dialogs,
        ErrorRecord error,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(error);

        var options = ToMessageBoxOptions(error);
        return await dialogs.ShowMessageBoxAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 显示多个错误聚合为单一消息框。Per ADR-0043 §7.
    /// 仅第一个错误的 Message 进入 Message 字段, 其他错误在 Detail 中列出。
    /// </summary>
    /// <param name="dialogs">对话框服务实例。</param>
    /// <param name="errors">错误记录列表。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<DialogResult> ShowErrorsAsync(
        this IDialogService dialogs,
        IReadOnlyList<ErrorRecord> errors,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
        {
            return DialogResult.Cancel;
        }

        var first = errors[0];
        var detail = errors.Count > 1
            ? string.Join("\n\n", errors.Select(e => $"[{e.Category}] {e.Message}"))
            : first.Detail;

        var options = ToMessageBoxOptions(first) with { Detail = detail };
        return await dialogs.ShowMessageBoxAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把 <see cref="ErrorRecord"/> 转换为 <see cref="MessageBoxOptions"/>。Per ADR-0043 §7.
    /// 纯函数, 可 100% 单测覆盖。
    /// </summary>
    /// <param name="error">错误记录。</param>
    /// <returns>对应的消息框参数。</returns>
    public static MessageBoxOptions ToMessageBoxOptions(ErrorRecord error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var (kind, buttons) = error.Category switch
        {
            // 严重错误: 默认 YesNoCancel, 用户可选重试 / 跳过 / 取消整个操作。
            ErrorCategory.PermissionDenied => (MessageBoxKind.Error, MessageBoxButtons.YesNoCancel),
            ErrorCategory.IOError => (MessageBoxKind.Error, MessageBoxButtons.YesNoCancel),
            ErrorCategory.OperationTimeout => (MessageBoxKind.Warning, MessageBoxButtons.OKCancel),
            // 普通错误: 仅 OK 提示。
            _ => (MessageBoxKind.Warning, MessageBoxButtons.OK),
        };

        var title = string.IsNullOrEmpty(error.Operation)
            ? $"Error: {error.Category}"
            : $"{error.Operation}: {error.Category}";

        return new MessageBoxOptions
        {
            Title = title,
            Message = error.Message,
            Kind = kind,
            Buttons = buttons,
            Detail = error.Detail,
            RelatedPath = error.TargetPath,
        };
    }
}
