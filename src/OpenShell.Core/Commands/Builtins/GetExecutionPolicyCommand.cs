using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Security;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-ExecutionPolicy</c> 命令。Per ADR-0054 §6.
/// 返回当前有效策略 (Process > User > Machine) 或列出所有 scope。
/// </summary>
[Verb("Get", Noun = "ExecutionPolicy", Aliases = ["gep"])]
[Description("Gets the effective script execution policy.")]
[Help(
    Synopsis = "Gets the effective script execution policy (Process > User > Machine).",
    Examples = new[]
    {
        "get-executionpolicy               # show effective policy",
        "get-executionpolicy -List         # list all scopes",
    },
    RelatedLinks = new[] { "set-executionpolicy" })]
public sealed class GetExecutionPolicyCommand : ICommand<GetExecutionPolicyCommand.Args>
{
    /// <summary>Arguments for <c>Get-ExecutionPolicy</c>.</summary>
    /// <param name="List">列出所有 scope 的策略 (Machine / User / Process)。</param>
    public record Args(
        [property: Parameter] bool List = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var svc = ctx.Host.Services.GetService(typeof(IExecutionPolicyService)) as IExecutionPolicyService;
        if (svc is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "ExecutionPolicy service is not available in this context.",
                Operation = "get-executionpolicy",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        if (args.List)
        {
            // 列出所有 scope。
            await ctx.Host.WriteOutputLineAsync(
                "Scope".PadRight(16) + "Policy", ct).ConfigureAwait(false);
            foreach (var (scope, policy) in svc.ListScopes())
            {
                await ctx.Host.WriteOutputLineAsync(
                    $"{scope,-16}{policy?.ToString() ?? "(not set)"}", ct).ConfigureAwait(false);
            }
            yield break;
        }

        // 默认: 显示有效策略。
        var effective = svc.GetEffectivePolicy();
        await ctx.Host.WriteOutputLineAsync(
            effective.ToString(), ct).ConfigureAwait(false);
        yield break;
    }
}
