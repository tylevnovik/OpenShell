using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Security;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Clear-Audit</c> command. Per ADR-0036 §15.
/// 清空审计日志, 需 <c>-Force</c> 显式确认。
/// </summary>
[Verb("Clear", Noun = "Audit", Aliases = ["claudit", "caudit"])]
[Description("Clears the operation audit log. Requires -Force.")]
[Help(
    Synopsis = "Clears the operation audit log. Requires -Force to prevent accidental data loss.",
    Examples = new[]
    {
        "clear-audit -Force         # clears all audit entries",
        "clear-audit                # refuses without -Force",
    },
    RelatedLinks = new[] { "get-audit" })]
public sealed class ClearAuditCommand : ICommand<ClearAuditCommand.Args>
{
    /// <summary>Arguments for <c>Clear-Audit</c>.</summary>
    /// <param name="Force">必须为 true 才执行清除, 否则报错。</param>
    public record Args(
        [property: Parameter(Aliases = new[] { "-f" })] bool Force = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var audit = ctx.Host.Services.GetService(typeof(IAuditService)) as IAuditService;
        if (audit is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Audit service is not available in this context.",
                Operation = "clear-audit",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        if (!args.Force)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.PermissionDenied,
                Message = "clear-audit requires -Force to prevent accidental data loss.",
                Operation = "clear-audit",
                Phase = ErrorPhase.Parse,
                Suggestion = "re-run with: clear-audit -Force",
            });
            yield break;
        }

        await audit.ClearAsync(ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync("Audit log cleared.", ct).ConfigureAwait(false);

        yield break;
    }
}
