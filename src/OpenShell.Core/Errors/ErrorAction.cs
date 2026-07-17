namespace OpenShell.Errors;

/// <summary>
/// Controls how non-terminating errors are handled. Per ADR-0026 §13 + ADR-0049 §11.1.
/// Terminating errors always propagate regardless of this setting.
/// </summary>
public enum ErrorAction
{
    /// <summary>Default. Write to error stream and continue.</summary>
    Continue,

    /// <summary>Promote non-terminating errors to terminating (throw).</summary>
    Stop,

    /// <summary>Silently skip without writing to error stream.</summary>
    SilentlyContinue,

    /// <summary>Like Continue but never write to the stream; only count.</summary>
    Ignore,

    /// <summary>
    /// Per ADR-0049 §11.1: 非终止错误时弹出确认提示 (复用 ShouldProcess 的 Y/A/N/L/S/? 提示)。
    /// 用户选 Y/A → 继续执行 (等同 Continue); N/L → 跳过当前项 (SilentlyContinue); S → 挂起到嵌套 REPL。
    /// </summary>
    Inquire,

    /// <summary>
    /// Per ADR-0049 §11.1: 挂起命令, 进入嵌套 REPL; 用户排查后可恢复 (实际语义与 Inquire 类似)。
    /// </summary>
    Suspend,
}
