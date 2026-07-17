using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Remoting;
using Microsoft.Extensions.DependencyInjection;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-PSSession</c> 命令。Per ADR-0059 §6.
/// 列出所有活跃的远程会话。
/// </summary>
[Verb("Get", Noun = "PSSession", Aliases = ["gsn"])]
[Description("Lists all active remote sessions.")]
[Help(
    Synopsis = "Lists all active PSSessions (Get-PSSession).",
    Examples = new[] { "get-pssession    # list all sessions" },
    RelatedLinks = new[] { "new-pssession", "remove-pssession" })]
public sealed class GetPSSessionCommand : ICommand<GetPSSessionCommand.Args>
{
    /// <summary>Arguments for <c>Get-PSSession</c>.</summary>
    public record Args();

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var manager = ctx.Host.Services.GetService(typeof(PSSessionManager)) as PSSessionManager;
        if (manager is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Remoting service is not available in this context.",
                Operation = "get-pssession",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            "Id".PadRight(6) + "ComputerName".PadRight(30) + "Transport".PadRight(10) + "State".PadRight(12) + "Name",
            ct).ConfigureAwait(false);

        foreach (var session in manager.GetAll())
        {
            await ctx.Host.WriteOutputLineAsync(
                $"{session.Id,-6}{session.ComputerName,-30}{session.Transport,-10}{session.State,-12}{session.Name ?? ""}",
                ct).ConfigureAwait(false);

            yield return new Item
            {
                Path = OpenShell.Paths.ItemPath.Root("remoting"),
                Kind = ItemKind.Property,
                Properties = PropertyBag.Empty
                    .With("Id", session.Id)
                    .With("ComputerName", session.ComputerName)
                    .With("Transport", session.Transport)
                    .With("State", session.State)
                    .With("Name", session.Name),
            };
        }
    }
}
