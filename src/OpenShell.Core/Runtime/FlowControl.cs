#nullable enable
// ADR-0045 §3 控制流信号 + ExecutionResult。
// 设计：break/continue/return/exit/throw 通过控制流信号在 AST 节点间传播，
// 而非抛异常（性能 + 语义清晰）。只有 throw 在跨作用域时转异常。

namespace OpenShell.Runtime;

/// <summary>控制流信号类型。per ADR-0045 §3.</summary>
public enum FlowSignalKind
{
    None,       // 正常执行
    Break,      // break
    Continue,   // continue
    Return,     // return [value]
    Exit,       // exit [code]
    Throw,      // throw [value]
}

/// <summary>AST 求值结果。携带可能的控制流信号。</summary>
public readonly record struct ExecutionResult(
    object? ResultValue,
    FlowSignalKind Signal,
    int ExitCode,
    object? ThrownValue,
    string? Label)
{
    public static ExecutionResult Empty { get; } = new(null, FlowSignalKind.None, 0, null, null);

    public static ExecutionResult Of(object? value) => new(value, FlowSignalKind.None, 0, null, null);
    public static ExecutionResult Break(string? label = null) => new(null, FlowSignalKind.Break, 0, null, label);
    public static ExecutionResult Continue(string? label = null) => new(null, FlowSignalKind.Continue, 0, null, label);
    public static ExecutionResult Return(object? value) => new(value, FlowSignalKind.Return, 0, null, null);
    public static ExecutionResult Exit(int code) => new(null, FlowSignalKind.Exit, code, null, null);
    public static ExecutionResult Throw(object? value) => new(null, FlowSignalKind.Throw, 0, value, null);

    /// <summary>求值结果值（别名 ResultValue，便于 Evaluator 内部使用）。</summary>
    public object? Value => ResultValue;

    public bool HasSignal => Signal != FlowSignalKind.None;
    public bool IsNormal => Signal == FlowSignalKind.None;
}
