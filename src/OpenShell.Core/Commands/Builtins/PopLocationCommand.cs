using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Pop-Location</c> command. Per ADR-0023 M1 / ADR-0048 §3.6. Pops the most
/// recently pushed location from the host's <see cref="OpenShell.Locations.ILocationStack"/>
/// singleton (resolved from <see cref="IHost.Services"/>) and switches the host to it.
/// Writes an <see cref="ErrorRecord"/> when the stack is empty.
/// </summary>
[Verb("Pop", Noun = "Location", Aliases = ["popd", "pop"])]
[Description("Pops the most recent location from the stack and switches to it.")]
public sealed class PopLocationCommand : ICommand<PopLocationCommand.Args>
{
    /// <summary>Arguments for <c>Pop-Location</c>. Currently takes no parameters.</summary>
    public record Args;

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stack = LocationStackResolver.Resolve(ctx);
        if (!stack.TryPop(out var popped))
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "The location stack is empty.",
                Operation = "pop-location",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        ctx.Host.CurrentLocation = popped;
        await ctx.Host.WriteOutputLineAsync(popped.Display, ct).ConfigureAwait(false);

        yield break;
    }
}
