using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-Location</c> command. Per ADR-0023 M1. Emits a virtual
/// <see cref="IItem"/> of kind <see cref="ItemKind.Property"/> representing the
/// host's current working location.
/// </summary>
[Verb("Get", Noun = "Location", Aliases = ["pwd", "gl"])]
[Description("Gets the current working location.")]
public sealed class GetLocationCommand : ICommand<GetLocationCommand.Args>
{
    /// <summary>Arguments for <c>Get-Location</c>. Currently takes no parameters.</summary>
    public record Args;

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var location = ctx.Host.CurrentLocation;
        await Task.CompletedTask;
        yield return new Item
        {
            Path = location,
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty
                .With("Path", location.Display)
                .With("Provider", location.Provider),
        };
    }
}
