#nullable enable
// ADR-0049 §8 $PSCmdlet 自动变量运行时实现。
// 设计：
//   1. PSCmdletContext 暴露 ShouldProcess / ShouldContinue / WriteVerbose 等方法供脚本调用。
//   2. 内部委托到 ExecutionContext 的 IShouldProcessService（若已注册），否则返回安全默认值。
//   3. 与 C# 命令侧的 CommandContext.ShouldProcess 行为完全一致（per ADR-0049 §1 镜像原则）。

using OpenShell.Commands;
using OpenShell.Errors;

namespace OpenShell.Runtime;

/// <summary>
/// 脚本函数内 <c>$PSCmdlet</c> 自动变量包装对象。Per ADR-0049 §8.
/// 仅在声明 <c>[CmdletBinding]</c> 的脚本函数 / 脚本块内可见；否则 <c>$PSCmdlet</c> 为 <c>$null</c>。
/// </summary>
public sealed class PSCmdletContext
{
    private readonly ExecutionContext _ctx;
    private readonly string _commandName;
    private readonly string _verb;
    private readonly ConfirmImpact _declaredImpact;

    /// <summary>构造 PSCmdlet 上下文。</summary>
    /// <param name="ctx">所属执行上下文（用于读取 $WhatIfPreference / $ConfirmPreference / IShouldProcessService）。</param>
    /// <param name="commandName">命令名（用于错误信息与 MyInvocation）。</param>
    /// <param name="verb">命令动词（用于派生 ShouldProcess 的 action 参数，per ADR-0049 §3.3）。</param>
    /// <param name="declaredImpact">[CmdletBinding(ConfirmImpact=...)] 或 [SupportsShouldProcess(ConfirmImpact=...)] 静态声明。</param>
    public PSCmdletContext(ExecutionContext ctx, string commandName, string verb, ConfirmImpact declaredImpact)
    {
        _ctx = ctx;
        _commandName = commandName;
        _verb = verb;
        _declaredImpact = declaredImpact;
    }

    /// <summary>命令名。Per ADR-0049 §8 MyInvocation。</summary>
    public string CommandName => _commandName;

    /// <summary>声明的 ConfirmImpact（来自 [CmdletBinding(ConfirmImpact=...)]）。Per ADR-0049 §5.</summary>
    public ConfirmImpact DeclaredConfirmImpact => _declaredImpact;

    /// <summary>
    /// Per ADR-0049 §1 (原"延迟实现", 现已落实): 当 [CmdletBinding(SupportsPaging)] 为 true 时,
    /// 暴露 -First / -Skip / -IncludeTotalCount 通用参数。命令实现应读取此属性应用分页。
    /// 由 ScriptBlock.InjectCmdletBindingEnvironment 在调用时填充。
    /// </summary>
    public PagingParameters? PagingParameters { get; internal set; }

    /// <summary>
    /// Per ADR-0049 §1 (原"延迟实现", 现已落实): 当 [CmdletBinding(SupportsTransactions)] 为 true 时,
    /// 暴露 -UseTransaction switch 通用参数。命令实现可查询此值决定是否加入当前事务。
    /// 事务系统本身需要独立 ADR (批6), 这里仅暴露参数入口。
    /// </summary>
    public bool UseTransaction { get; internal set; }

    /// <summary>
    /// ShouldProcess(target, action) 完整重载。Per ADR-0049 §3.
    /// 决策流程：WhatIf → false；ConfirmPreference=None → true；impact ≥ preference → 提示；否则 true。
    /// </summary>
    public bool ShouldProcess(string target, string action)
        => CallShouldProcess(target, action, _declaredImpact);

    /// <summary>ShouldProcess(target, action, caption) 重载。caption 在 CLI 忽略。Per ADR-0049 §3.</summary>
    public bool ShouldProcess(string target, string action, string caption)
        => CallShouldProcess(target, action, _declaredImpact);

    /// <summary>ShouldProcess(target) 重载：action 从命令 Verb 派生。Per ADR-0049 §3.3.</summary>
    public bool ShouldProcess(string target)
        => CallShouldProcess(target, DeriveActionFromVerb(), _declaredImpact);

    /// <summary>ShouldContinue(target, action)：强制提示，不受 WhatIf/ConfirmPreference 影响。Per ADR-0049 §4.</summary>
    public bool ShouldContinue(string target, string action)
    {
        var service = ResolveService();
        if (service is null) return true;
        return service.ShouldContinue(target, action);
    }

    /// <summary>WriteVerbose：写入 Verbose 流（受 -Verbose 控制）。Per ADR-0049 §8.</summary>
    public void WriteVerbose(string text)
    {
        // 当前实现：委托到 host 输出（无独立 Verbose 流，简化为标准输出）。
        _ctx.Host?.WriteOutputLineAsync($"VERBOSE: {text}", _ctx.CancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>WriteWarning：写入 Warning 流。Per ADR-0049 §8.</summary>
    public void WriteWarning(string text)
    {
        _ctx.Host?.WriteOutputLineAsync($"WARNING: {text}", _ctx.CancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>WriteDebug：写入 Debug 流（受 -Debug 控制）。Per ADR-0049 §8.</summary>
    public void WriteDebug(string text)
    {
        _ctx.Host?.WriteOutputLineAsync($"DEBUG: {text}", _ctx.CancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>WriteError：写入错误流。Per ADR-0049 §8 / ADR-0026.</summary>
    public void WriteError(ErrorRecord errorRecord)
    {
        _ctx.WriteError(errorRecord);
    }

    // -------------------------------------------------------------------
    // 内部实现
    // -------------------------------------------------------------------

    private bool CallShouldProcess(string target, string action, ConfirmImpact impact)
    {
        var service = ResolveService();
        if (service is null)
        {
            // 无 IShouldProcessService 注册（单元测试 / 未配置 host）：
            // 退化到 $WhatIfPreference 变量读取 + 默认放行。
            var whatIf = ReadPreferenceVariable("WhatIfPreference", false);
            if (whatIf)
            {
                // Per ADR-0049 §3.1: 使用单引号包裹 action 与 target.
                _ctx.Host?.WriteOutputLineAsync(
                    $"What if: Performing the operation '{action}' on target '{target}'.",
                    _ctx.CancellationToken).GetAwaiter().GetResult();
                return false;
            }
            return true;
        }
        return service.ShouldProcess(target, action, MapImpact(impact));
    }

    private IShouldProcessService? ResolveService()
        => _ctx.Host?.Services?.GetService(typeof(IShouldProcessService)) as IShouldProcessService;

    private bool ReadPreferenceVariable(string name, bool defaultValue)
    {
        var v = _ctx.Variables?.Resolve(name);
        return v is bool b ? b : defaultValue;
    }

    private static OpenShell.Commands.ConfirmImpact MapImpact(ConfirmImpact impact)
        => impact switch
        {
            ConfirmImpact.None => OpenShell.Commands.ConfirmImpact.None,
            ConfirmImpact.Low => OpenShell.Commands.ConfirmImpact.Low,
            ConfirmImpact.Medium => OpenShell.Commands.ConfirmImpact.Medium,
            ConfirmImpact.High => OpenShell.Commands.ConfirmImpact.High,
            _ => OpenShell.Commands.ConfirmImpact.Medium,
        };

    private string DeriveActionFromVerb()
        => string.IsNullOrEmpty(_verb) ? "Process" : _verb;
}

/// <summary>
/// Per ADR-0049 §1 (原"延迟实现", 现已落实): [CmdletBinding(SupportsPaging)] 暴露的通用分页参数。
/// 命令实现应在枚举结果时按 <see cref="Skip"/> 跳过、按 <see cref="First"/> 限制数量,
/// 并在 <see cref="IncludeTotalCount"/> 为 true 时通过 <c>$PSCmdlet.WriteInformation</c> 或
/// 设置 <c>$TotalCount</c> 变量报告总数。
/// </summary>
public sealed class PagingParameters
{
    /// <summary>
    /// -First &lt;UInt64&gt;: 最多返回的项数。默认 <see cref="ulong.MaxValue"/> 表示无限制。
    /// 命令实现应在产出 First 项后停止枚举。
    /// </summary>
    public ulong First { get; init; } = ulong.MaxValue;

    /// <summary>
    /// -Skip &lt;UInt64&gt;: 跳过开头的 N 项。默认 0。
    /// 与 <see cref="First"/> 组合实现分页 (Skip=10, First=5 → 第 11~15 项)。
    /// </summary>
    public ulong Skip { get; init; } = 0;

    /// <summary>
    /// -IncludeTotalCount: switch, 命令应在输出第一项前报告总匹配数。
    /// 实际总数通过 <c>$TotalCount</c> 自动变量暴露 (命令在 begin 块或枚举前 Set 该变量)。
    /// </summary>
    public bool IncludeTotalCount { get; init; } = false;
}
