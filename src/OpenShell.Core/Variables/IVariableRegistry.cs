namespace OpenShell.Variables;

/// <summary>
/// 变量注册表。Per ADR-0042 §9.
/// 三层作用域（Session > Script > Global），自动变量只读，环境变量通过 $env: 桥接。
/// 替换 M1 CliHost 中硬编码的 $? / $LASTEXITCODE if 分支。
/// </summary>
public interface IVariableRegistry
{
    /// <summary>解析变量名。命中则返回值，否则返回 null。</summary>
    /// <param name="name">变量名（不含 $ 前缀），支持 $env:NAME 形式（带 env: 前缀）。</param>
    /// <param name="scope">作用域，默认 Session。</param>
    object? Resolve(string name, VariableScope scope = VariableScope.Session);

    /// <summary>设置用户变量。自动变量（IsReadOnly 返回 true）赋值抛 ReadOnlyVariableException。</summary>
    void Set(string name, object value, VariableScope scope = VariableScope.Session);

    /// <summary>移除用户变量。自动变量不可移除。</summary>
    bool Remove(string name, VariableScope scope = VariableScope.Session);

    /// <summary>列举变量。scope=null 列举所有作用域。</summary>
    IReadOnlyList<KeyValuePair<string, object>> List(VariableScope? scope = null);

    /// <summary>判断是否为只读变量（自动变量）。</summary>
    bool IsReadOnly(string name);

    /// <summary>更新自动变量值（仅核心系统调用，如 CliHost 在命令执行后更新 $? / $LASTEXITCODE）。</summary>
    void SetAutomatic(string name, object value);

    /// <summary>推入新的作用域帧。返回 IDisposable，Dispose 时弹栈。Per ADR-0047 §1.</summary>
    /// <param name="kind">作用域类型，默认 Local。</param>
    IDisposable PushScope(VariableScope kind = VariableScope.Local);
}

/// <summary>
/// 自动变量赋值给只读变量时抛出。Per ADR-0042 §12.
/// </summary>
public sealed class ReadOnlyVariableException : Exception
{
    public ReadOnlyVariableException(string name) : base($"Cannot assign to read-only automatic variable '${name}'.") { }
}

/// <summary>
/// 引用未定义变量时抛出。Per ADR-0042 §12.
/// </summary>
public sealed class VariableNotFoundException : Exception
{
    public VariableNotFoundException(string name) : base($"Variable '${name}' is not defined.") { }
}
