namespace OpenShell.Variables;

/// <summary>
/// 默认 <see cref="IVariableRegistry"/> 内存实现。Per ADR-0047 (revises ADR-0042).
/// 用 ScopeStack 替代三层平铺字典, 支持函数调用栈帧生命周期、$private: / $using: 修饰符。
/// 保留 Session 枚举值作为 Local 的别名 (向后兼容)。
/// </summary>
public sealed class InMemoryVariableRegistry : IVariableRegistry
{
    // 自动变量名集合 (只读 / 不可移除)。Per ADR-0042 §3 / ADR-0047 §1.2 / ADR-0049 §2/§8.
    // 注意：$WhatIfPreference / $ConfirmPreference / $PSCmdlet 是"作用域可覆盖"的偏好变量，
    // 在 [CmdletBinding] 函数作用域内可被 Set 覆盖（Per ADR-0049 §2），不列为只读自动变量。
    // $matches / $foreach / $switch 也是可读写变量（运行时在 Local 作用域 Set 更新，Per ADR-0042 §3.5），不列为只读。
    private static readonly HashSet<string> AutomaticNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // 状态字面量
        "?", "LASTEXITCODE", "TRUE", "FALSE", "NULL",
        // 环境信息
        "PWD", "HOME", "HOST", "HOSTNAME", "PID", "OS", "PROFILE",
        // 错误流
        "ERROR", "ERRORS",
        // 函数/脚本块参数
        "ARGS", "INPUT",
        // 自动变量 (在求值上下文动态绑定)
        "_", "PSITEM",
        // 其他 (尚未实现但已声明只读性)
        "PSBOUNDPARAMETERS", "MYINVOCATION",
        "PSSCRIPTROOT", "PSCOMMANDPATH",
    };

    /// <summary>底层作用域栈 (可注入用于测试)。</summary>
    public ScopeStack Stack { get; }

    public InMemoryVariableRegistry(ScopeStack? stack = null)
    {
        Stack = stack ?? new ScopeStack();
        InitializeAutomaticVariables();
    }

    private void InitializeAutomaticVariables()
    {
        // 字面量自动变量 (Global 框, Per ADR-0042 §3).
        SetAutomaticEntry("TRUE", true);
        SetAutomaticEntry("FALSE", false);
        SetAutomaticEntry("NULL", null!);

        // 环境信息自动变量 (Global 框).
        SetAutomaticEntry("HOME", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        SetAutomaticEntry("HOSTNAME", Environment.MachineName);
        SetAutomaticEntry("PID", Environment.ProcessId);
        SetAutomaticEntry("OS", Environment.OSVersion.Platform switch
        {
            PlatformID.Win32NT => "Windows",
            PlatformID.Unix => "Linux",
            PlatformID.MacOSX => "macOS",
            _ => Environment.OSVersion.Platform.ToString(),
        });

        // ShouldProcess 偏好默认值。Per ADR-0049 §2 / ADR-0042 §3.8.
        // 偏好变量非只读（用户可在 profile/REPL 顶层 Set 覆盖，[CmdletBinding] 命令作用域也可 Set 覆盖）。
        SetPreferenceDefault("WHATIFPREFERENCE", false);            // $WhatIfPreference = $false
        SetPreferenceDefault("CONFIRMPREFERENCE", "High");          // $ConfirmPreference = 'High'
    }

    private void SetAutomaticEntry(string name, object value)
    {
        Stack.Global.Set(name, new VariableEntry(name, value, isReadOnly: true));
    }

    /// <summary>
    /// 初始化偏好变量默认值（Global 框，isReadOnly: false）。
    /// 偏好变量非自动变量，用户/命令作用域可通过 Set 覆盖。Per ADR-0042 §3.8 / ADR-0049 §2.
    /// </summary>
    private void SetPreferenceDefault(string name, object value)
    {
        Stack.Global.Set(name, new VariableEntry(name, value, isReadOnly: false));
    }

    /// <inheritdoc />
    public object? Resolve(string name, VariableScope scope = VariableScope.Session)
    {
        // $env:NAME 桥接。Per ADR-0047 §10.5 / ADR-0042 §4.
        if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var envName = name[4..];
            return Environment.GetEnvironmentVariable(envName);
        }

        // 作用域修饰符: $global: / $script: / $local: / $private: / $using:
        var (cleanName, explicitScope) = StripScopeModifier(name);
        if (explicitScope is { } t)
        {
            return t switch
            {
                VariableScope.Global => Stack.LookupGlobal(cleanName)?.Value,
                VariableScope.Script => Stack.LookupScript(cleanName)?.Value,
                VariableScope.Local => Stack.LookupLocal(cleanName)?.Value,
                VariableScope.Private => Stack.LookupLocal(cleanName)?.Value,
                // Per ADR-0047 §1.2 + ADR-0046 §4: 本地上下文中 $using: 退化为闭包读取（Local 查找），
                // 与 ScriptBlock 闭包捕获语义兼容。远程上下文（Invoke-Command / Start-Job）由远程宿主处理。
                VariableScope.Using => Stack.LookupLocal(cleanName)?.Value,
                _ => Stack.Lookup(cleanName)?.Value,
            };
        }

        // 默认: 自顶向下回溯查找 (跳过 Private 父作用域变量).
        return Stack.Lookup(cleanName)?.Value;
    }

    /// <inheritdoc />
    public void Set(string name, object value, VariableScope scope = VariableScope.Session)
    {
        // $env:NAME 桥接: 实际调用 OS SetEnvironmentVariable (Per ADR-0047 §10.5; 修复 ADR-0042 旧 bug).
        if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var envName = name[4..];
            var envValue = value?.ToString() ?? string.Empty;
            // .NET 行为: 空字符串等价于删除该变量。
            Environment.SetEnvironmentVariable(envName, envValue);
            return;
        }

        var (cleanName, explicitScope) = StripScopeModifier(name);
        var actualScope = NormalizeScope(explicitScope ?? scope);

        // 自动变量 (只读): 写入抛 ReadOnlyVariableException.
        if (IsReadOnly(cleanName))
        {
            throw new ReadOnlyVariableException(cleanName);
        }

        // $private: 写入当前帧, IsPrivate=true.
        var isPrivate = actualScope == VariableScope.Private;
        var targetFrame = actualScope switch
        {
            VariableScope.Global => Stack.Global,
            VariableScope.Script => Stack.Script,
            // Local / Session / Private 都写入当前帧 (Private 仅标记 IsPrivate).
            _ => Stack.Current,
        };

        targetFrame.Set(cleanName, new VariableEntry(cleanName, value, isPrivate: isPrivate));
    }

    /// <inheritdoc />
    public bool Remove(string name, VariableScope scope = VariableScope.Session)
    {
        // $env: 不通过 Remove 删除 (调用方应直接用 Environment API 或 Set null).
        if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var envName = name[4..];
            Environment.SetEnvironmentVariable(envName, null);
            return true;
        }

        var (cleanName, explicitScope) = StripScopeModifier(name);
        var actualScope = NormalizeScope(explicitScope ?? scope);

        if (IsReadOnly(cleanName)) return false;

        return actualScope switch
        {
            VariableScope.Global => Stack.Global.Remove(cleanName),
            VariableScope.Script => Stack.Script.Remove(cleanName),
            _ => Stack.Current.Remove(cleanName),
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<KeyValuePair<string, object>> List(VariableScope? scope = null)
    {
        var result = new List<KeyValuePair<string, object>>();

        if (scope is null)
        {
            // 列举所有可见变量 (子覆盖父, 与 Resolve 行为一致).
            foreach (var kv in Stack.EnumerateVisible())
            {
                result.Add(new KeyValuePair<string, object>(kv.Key, kv.Value.Value!));
            }
        }
        else
        {
            var normalized = NormalizeScope(scope.Value);
            var frame = normalized switch
            {
                VariableScope.Global => Stack.Global,
                VariableScope.Script => Stack.Script,
                _ => Stack.Current,
            };
            foreach (var kv in frame.Variables)
            {
                result.Add(new KeyValuePair<string, object>(kv.Key, kv.Value.Value!));
            }
        }

        return result;
    }

    /// <inheritdoc />
    public bool IsReadOnly(string name)
    {
        var (cleanName, _) = StripScopeModifier(name);
        // $env: 不属于自动变量 (写入走 OS API).
        if (cleanName.StartsWith("env:", StringComparison.OrdinalIgnoreCase)) return false;
        return AutomaticNames.Contains(cleanName);
    }

    /// <inheritdoc />
    public void SetAutomatic(string name, object value)
    {
        // 由核心系统调用 (如 CliHost 更新 $? / $LASTEXITCODE / $PWD / $ERROR).
        // 直接写入 Global 框 (绕过 IsReadOnly 检查).
        Stack.Global.Set(name, new VariableEntry(name, value, isReadOnly: AutomaticNames.Contains(name)));
    }

    /// <summary>
    /// 推入新作用域栈帧 (函数调用 / 脚本块执行)。返回 IDisposable, Dispose 时弹栈。
    /// Per ADR-0047 §1.6.
    /// </summary>
    public IDisposable PushScope(VariableScope kind = VariableScope.Local) => Stack.PushScope(kind);

    /// <summary>
    /// 处理作用域修饰符: $global:x / $script:x / $local:x / $private:x / $using:x。
    /// 返回 (cleanName, explicitScope?), explicitScope 为 null 表示默认。
    /// </summary>
    private static (string CleanName, VariableScope? ExplicitScope) StripScopeModifier(string name)
    {
        if (name.StartsWith("global:", StringComparison.OrdinalIgnoreCase))
            return (name[7..], VariableScope.Global);
        if (name.StartsWith("script:", StringComparison.OrdinalIgnoreCase))
            return (name[7..], VariableScope.Script);
        if (name.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
            return (name[6..], VariableScope.Local);
        if (name.StartsWith("private:", StringComparison.OrdinalIgnoreCase))
            return (name[8..], VariableScope.Private);
        if (name.StartsWith("using:", StringComparison.OrdinalIgnoreCase))
            return (name[6..], VariableScope.Using);
        return (name, null);
    }

    /// <summary>
    /// 把 Session 视为 Local 的别名 (向后兼容 ADR-0042 调用方)。
    /// </summary>
    private static VariableScope NormalizeScope(VariableScope scope) => scope switch
    {
        VariableScope.Session => VariableScope.Local,
        _ => scope,
    };
}
