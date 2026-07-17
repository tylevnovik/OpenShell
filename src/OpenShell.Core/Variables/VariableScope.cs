namespace OpenShell.Variables;

/// <summary>
/// 变量作用域。Per ADR-0047 §1 (revises ADR-0042 §5).
/// 栈式模型: Local (current) > Script > Global (bottom, 自动变量 + $env: 桥接).
/// Private 与 Using 是修饰语义而非独立栈帧层级 (Private 标记在 VariableEntry.IsPrivate, Using 走远程序列化).
/// </summary>
public enum VariableScope
{
    /// <summary>全局只读: 自动变量 ($? / $PWD / $HOME 等) + $env: 桥接。栈底。</summary>
    Global,

    /// <summary>脚本/profile 作用域。</summary>
    Script,

    /// <summary>REPL / 当前会话最高优先级。等价于 Local (向后兼容别名)。</summary>
    Session,

    /// <summary>当前局部作用域 (函数调用 / 脚本块执行)。M4 起的首选默认。</summary>
    Local,

    /// <summary>私有变量: 写入当前 Local 帧但 IsPrivate=true, 子作用域回溯时不可见。</summary>
    Private,

    /// <summary>跨作用域: 仅在 Invoke-Command / Start-Job 上下文合法 (M5+ 实现远程序列化)。</summary>
    Using,
}
