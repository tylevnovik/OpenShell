namespace OpenShell.Variables;

/// <summary>
/// 变量槽: 名称、值、声明类型与选项 (Private / Constant / ReadOnly)。Per ADR-0047 §1.1.
/// 替代 ADR-0042 的"裸 object 字典"模型, 支持类型化变量 [int]$x 与 New-Variable -Option。
/// </summary>
public sealed class VariableEntry
{
    /// <summary>变量名 (不含 $ 前缀, 不含 scope 修饰符)。</summary>
    public string Name { get; }

    /// <summary>当前值; 赋值时按 DeclaredType 强制转换 (若声明了类型)。</summary>
    public object? Value { get; set; }

    /// <summary>声明的类型约束 ([int]$x 时为 typeof(int)); null 表示无类型约束。</summary>
    public Type? DeclaredType { get; }

    /// <summary>Private 修饰符: 子作用域回溯时跳过此变量。</summary>
    public bool IsPrivate { get; init; }

    /// <summary>Constant 选项: 不可赋值也不可移除 (与 PowerShell 一致)。</summary>
    public bool IsConstant { get; init; }

    /// <summary>ReadOnly 选项: 不可赋值, 但可移除。</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>构造变量槽。所有选项默认 false / null。</summary>
    /// <param name="name">变量名。</param>
    /// <param name="value">初始值。</param>
    /// <param name="declaredType">声明的类型约束 (可选)。</param>
    /// <param name="isPrivate">Private 标记。</param>
    /// <param name="isConstant">Constant 选项。</param>
    /// <param name="isReadOnly">ReadOnly 选项。</param>
    public VariableEntry(
        string name,
        object? value,
        Type? declaredType = null,
        bool isPrivate = false,
        bool isConstant = false,
        bool isReadOnly = false)
    {
        Name = name;
        Value = value;
        DeclaredType = declaredType;
        IsPrivate = isPrivate;
        IsConstant = isConstant;
        IsReadOnly = isReadOnly;
    }
}
