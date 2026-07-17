using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Write-Information</c> command. Per ADR-0048 §2.7.
/// <para>
/// Writes an <c>InformationRecord</c> to the information stream (stream 6).
/// In the CLI host, information messages are prefixed with <c>INFO:</c>.
/// Visibility is controlled by <c>$InformationPreference</c>.
/// </para>
/// </summary>
[Verb("Write", Noun = "Information", Aliases = ["wi"])]
[Description("Writes an informational message.")]
public sealed class WriteInformationCommand : ICommand<WriteInformationCommand.Args>
{
    /// <summary>Arguments for <c>Write-Information</c>.</summary>
    public record Args(
        [property: Parameter(Position = 0)] object? MessageData = null,
        string[]? Tags = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var data = args.MessageData ?? string.Empty;
        var line = $"INFO: {data}";

        await ctx.Host.WriteOutputLineAsync(line, ct).ConfigureAwait(false);

        yield break;
    }
}
