using OpenShell.Errors;

namespace OpenShell.Filter;

/// <summary>
/// Filter DSL 解析异常。Per ADR-0012 §7.
/// 包含位置信息，由命令层包装为 <see cref="ErrorRecord"/>（Category=ParseError）。
/// </summary>
public sealed class FilterParseException : Exception
{
    /// <summary>出错 token 在原始表达式中的字符偏移（0-based）。-1 表示未知。</summary>
    public int Position { get; }

    /// <summary>出错 token 的文本。</summary>
    public string? Token { get; }

    public FilterParseException(string message, int position = -1, string? token = null)
        : base(message)
    {
        Position = position;
        Token = token;
    }

    public FilterParseException(string message, Exception innerException, int position = -1, string? token = null)
        : base(message, innerException)
    {
        Position = position;
        Token = token;
    }

    /// <summary>转换为 <see cref="ErrorRecord"/>，Category=ParseError。</summary>
    public ErrorRecord ToErrorRecord(string? operation = null)
    {
        return new ErrorRecord
        {
            Category = ErrorCategory.ParseError,
            Message = Message,
            Detail = Position >= 0 ? $"at position {Position}, token: {Token ?? "?"}" : null,
            Operation = operation,
            Phase = ErrorPhase.Parse,
        };
    }
}
