// SourcePosition 与 SourceSpan：所有 AST/Token 共享的源代码位置信息。
// 命名空间 OpenShell.Parsing（与 Token 同级），供 AstNodes.cs 引用。
// 借鉴 PS IScriptExtent（Position.cs）：提供 StartOffset/EndOffset/Text/File 便捷访问。Per T-101。

namespace OpenShell.Parsing;

/// <summary>源代码位置（行/列/字节偏移）。</summary>
public sealed record SourcePosition(int Line, int Column, int Offset)
{
    public static SourcePosition Zero { get; } = new(1, 1, 0);
    public override string ToString() => $"({Line},{Column})";
}

/// <summary>
/// 源代码跨度。借鉴 PS <c>IScriptExtent</c>（Position.cs）。
/// 提供 Start/End 位置 + 便捷属性（StartOffset/EndOffset/Length/Text/File）。
/// </summary>
public sealed record SourceSpan(SourcePosition Start, SourcePosition End)
{
    public static SourceSpan Empty { get; } = new(SourcePosition.Zero, SourcePosition.Zero);

    /// <summary>起始字节偏移（借鉴 PS IScriptExtent.StartOffset）。</summary>
    public int StartOffset => Start.Offset;

    /// <summary>结束字节偏移（借鉴 PS IScriptExtent.EndOffset）。</summary>
    public int EndOffset => End.Offset;

    /// <summary>跨度长度（字节）。EndOffset - StartOffset。</summary>
    public int Length => End.Offset - Start.Offset;

    /// <summary>起始行号（借鉴 PS IScriptExtent.StartLineNumber）。</summary>
    public int StartLine => Start.Line;

    /// <summary>起始列号（借鉴 PS IScriptExtent.StartColumnNumber）。</summary>
    public int StartColumn => Start.Column;

    /// <summary>结束行号（借鉴 PS IScriptExtent.EndLineNumber）。</summary>
    public int EndLine => End.Line;

    /// <summary>结束列号（借鉴 PS IScriptExtent.EndColumnNumber）。</summary>
    public int EndColumn => End.Column;

    public override string ToString() => Start.ToString();
}
