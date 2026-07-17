using System.Runtime.CompilerServices;
using OpenShell.Configuration;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Security;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Set-ExecutionPolicy</c> 命令。Per ADR-0054 §6.
/// 设置指定 scope 的 ExecutionPolicy。Machine scope 需管理员权限。
/// </summary>
[Verb("Set", Noun = "ExecutionPolicy", Aliases = ["sep"])]
[Description("Sets the script execution policy for a scope.")]
[Help(
    Synopsis = "Sets the script execution policy (Restricted / RemoteSigned / Unrestricted / Bypass).",
    Examples = new[]
    {
        "set-executionpolicy RemoteSigned              # set User scope",
        "set-executionpolicy Bypass -Scope Process     # current session only",
        "set-executionpolicy Restricted -Scope Machine # requires admin (UAC/root)",
    },
    RelatedLinks = new[] { "get-executionpolicy" })]
public sealed class SetExecutionPolicyCommand : ICommand<SetExecutionPolicyCommand.Args>
{
    /// <summary>Arguments for <c>Set-ExecutionPolicy</c>.</summary>
    /// <param name="Policy">策略级别 (Restricted / RemoteSigned / Unrestricted / Bypass)。</param>
    /// <param name="Scope">作用域 (Machine / User / Process)。默认 User。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Policy,
        [property: Parameter] string Scope = "User");

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
                Operation = "set-executionpolicy",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        if (!Enum.TryParse<ExecutionPolicy>(args.Policy, ignoreCase: true, out var policy))
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = $"Invalid ExecutionPolicy '{args.Policy}'. Valid values: Restricted, RemoteSigned, Unrestricted, Bypass.",
                Operation = "set-executionpolicy",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        if (!Enum.TryParse<ExecutionPolicyScope>(args.Scope, ignoreCase: true, out var scope))
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = $"Invalid scope '{args.Scope}'. Valid values: Machine, User, Process.",
                Operation = "set-executionpolicy",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        try
        {
            svc.SetPolicy(policy, scope);

            // User scope 需持久化到 config.toml。Per ADR-0054 §6.
            if (scope == ExecutionPolicyScope.User)
            {
                var cfg = ctx.Host.Services.GetService(typeof(IConfigurationService)) as IConfigurationService;
                if (cfg is not null)
                {
                    await cfg.SaveAsync(ct).ConfigureAwait(false);
                }
            }

            await ctx.Host.WriteOutputLineAsync(
                $"ExecutionPolicy set to {policy} (scope: {scope}).", ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.PermissionDenied,
                Message = ex.Message,
                Operation = "set-executionpolicy",
                Phase = ErrorPhase.Operation,
            });
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = $"Failed to set ExecutionPolicy: {ex.Message}",
                Operation = "set-executionpolicy",
                Phase = ErrorPhase.Operation,
            });
        }

        yield break;
    }
}
