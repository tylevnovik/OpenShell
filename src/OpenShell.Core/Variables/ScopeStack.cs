namespace OpenShell.Variables;

/// <summary>
/// 作用域栈: 替代 ADR-0042 的三层平铺字典, 支持函数调用产生栈帧生命周期。Per ADR-0047 §1.1-1.3.
/// 启动时预置 Global / Script / Local 三层; PushScope 推入新 Local 帧, Dispose 时弹栈。
/// 查找走 Lookup 自顶向下回溯, 遇到 Private 且非当前帧时跳过。
/// </summary>
public sealed class ScopeStack
{
    private const int MaxDepth = 1000;
    private readonly List<ScopeFrame> _frames = new();

    /// <summary>构造默认栈: Global (栈底) → Script → Local (current)。</summary>
    public ScopeStack()
    {
        _frames.Add(new ScopeFrame(VariableScope.Global));
        _frames.Add(new ScopeFrame(VariableScope.Script));
        _frames.Add(new ScopeFrame(VariableScope.Local)); // default current
    }

    /// <summary>当前栈帧 (栈顶)。</summary>
    public ScopeFrame Current => _frames[^1];

    /// <summary>Global 栈帧 (栈底, 自动变量存放处)。</summary>
    public ScopeFrame Global => _frames[0];

    /// <summary>Script 栈帧 (倒数第二层)。</summary>
    public ScopeFrame Script => _frames[1];

    /// <summary>当前栈深度 (含 Global / Script / Local)。</summary>
    public int Depth => _frames.Count;

    /// <summary>按索引访问栈帧 (0 = 当前, 1 = 父, ..., N = Global)。</summary>
    public ScopeFrame this[int indexFromTop] => _frames[_frames.Count - 1 - indexFromTop];

    /// <summary>推入新作用域栈帧 (用于函数调用 / 脚本块执行)。返回 IDisposable, Dispose 时弹栈。</summary>
    /// <param name="kind">栈帧类型 (通常 Local)。</param>
    /// <exception cref="ScopeStackOverflowException">栈深度超过 MaxDepth。</exception>
    public IDisposable PushScope(VariableScope kind = VariableScope.Local)
    {
        if (_frames.Count >= MaxDepth)
        {
            throw new ScopeStackOverflowException(_frames.Count, MaxDepth);
        }
        _frames.Add(new ScopeFrame(kind));
        return new ScopePopper(this);
    }

    /// <summary>
    /// 自顶向下回溯查找变量。Per ADR-0047 §1.3.
    /// 遇到 IsPrivate 的 entry 且非当前帧时跳过 (skipPrivate=true 默认开启, 模拟子作用域不可见语义)。
    /// </summary>
    /// <param name="name">变量名。</param>
    /// <param name="skipPrivate">是否在父作用域中跳过 Private 标记的变量 (默认 true)。</param>
    /// <returns>命中的 VariableEntry 或 null。</returns>
    public VariableEntry? Lookup(string name, bool skipPrivate = true)
    {
        for (var i = _frames.Count - 1; i >= 0; i--)
        {
            var frame = _frames[i];
            if (frame.TryGet(name, out var entry))
            {
                if (skipPrivate && entry.IsPrivate && i < _frames.Count - 1)
                {
                    continue;
                }
                return entry;
            }
        }
        return null;
    }

    /// <summary>写入当前栈帧。</summary>
    public void SetCurrent(string name, VariableEntry entry) => Current.Set(name, entry);

    /// <summary>仅查当前栈帧 (用于 $local: 修饰符语义)。</summary>
    public VariableEntry? LookupLocal(string name)
        => Current.TryGet(name, out var entry) ? entry : null;

    /// <summary>仅查 Script 栈帧 (用于 $script: 修饰符语义)。</summary>
    public VariableEntry? LookupScript(string name)
        => Script.TryGet(name, out var entry) ? entry : null;

    /// <summary>仅查 Global 栈帧 (用于 $global: 修饰符语义)。</summary>
    public VariableEntry? LookupGlobal(string name)
        => Global.TryGet(name, out var entry) ? entry : null;

    /// <summary>从当前栈帧移除变量。</summary>
    public bool RemoveFromCurrent(string name) => Current.Remove(name);

    /// <summary>从指定栈帧移除变量 (按从顶算的 index)。</summary>
    public bool RemoveAt(int indexFromTop, string name)
    {
        if (indexFromTop < 0 || indexFromTop >= _frames.Count)
        {
            return false;
        }
        return _frames[_frames.Count - 1 - indexFromTop].Remove(name);
    }

    /// <summary>遍历所有可见变量 (从当前帧向上回溯, 子覆盖父, 不含 Private 跳过逻辑)。</summary>
    public IEnumerable<KeyValuePair<string, VariableEntry>> EnumerateVisible()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = _frames.Count - 1; i >= 0; i--)
        {
            var frame = _frames[i];
            foreach (var kv in frame.Variables)
            {
                if (seen.Add(kv.Key))
                {
                    yield return kv;
                }
            }
        }
    }

    private void Pop() => _frames.RemoveAt(_frames.Count - 1);

    private sealed class ScopePopper(ScopeStack stack) : IDisposable
    {
        public void Dispose() => stack.Pop();
    }
}

/// <summary>
/// 单个作用域栈帧: 持有一组变量 (大小写不敏感字典)。
/// Per ADR-0047 §1.1 ScopeFrame 是 ScopeStack 的内部数据单元。
/// </summary>
public sealed class ScopeFrame(VariableScope kind)
{
    /// <summary>本栈帧的作用域类型。</summary>
    public VariableScope Kind { get; } = kind;

    private readonly Dictionary<string, VariableEntry> _vars = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>尝试获取变量; 未命中返回 false。</summary>
    public bool TryGet(string name, out VariableEntry entry) => _vars.TryGetValue(name, out entry!);

    /// <summary>设置或覆盖变量。</summary>
    public void Set(string name, VariableEntry entry) => _vars[name] = entry;

    /// <summary>移除变量; 返回是否实际移除。</summary>
    public bool Remove(string name) => _vars.Remove(name);

    /// <summary>本栈帧的全部变量 (只读视图)。</summary>
    public IReadOnlyDictionary<string, VariableEntry> Variables => _vars;
}

/// <summary>
/// 栈深度超出限制时抛出。Per ADR-0047 §1.4 (防止无限递归栈溢出)。
/// </summary>
public sealed class ScopeStackOverflowException : OpenShell.Errors.OpenShellException
{
    /// <summary>当前栈深度。</summary>
    public int CurrentDepth { get; }

    /// <summary>允许的最大栈深度。</summary>
    public int MaxDepth { get; }

    public ScopeStackOverflowException(int currentDepth, int maxDepth)
        : base($"Scope stack overflow: depth {currentDepth} exceeds maximum {maxDepth}.")
    {
        CurrentDepth = currentDepth;
        MaxDepth = maxDepth;
    }

    public override OpenShell.Errors.ErrorCategory Category => OpenShell.Errors.ErrorCategory.OperationFailed;
}
