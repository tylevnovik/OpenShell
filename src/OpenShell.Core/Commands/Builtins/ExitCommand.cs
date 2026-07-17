using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Exit</c> command. Per ADR-0023 M1. Signals the host to exit by
/// throwing <see cref="OperationCanceledException"/>. The CLI host catches this
/// exception at the top-level REPL loop and terminates the process.
/// </summary>
[Verb("Exit", Noun = "", Aliases = ["quit", "q"])]
[Description("Exits the shell.")]
public sealed class ExitCommand : ICommand<ExitCommand.Args>
{
    /// <summary>Arguments for <c>Exit</c>. Currently takes no parameters.</summary>
    public record Args;

    /// <inheritdoc />
    public IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, CancellationToken ct = default)
        => throw new OperationCanceledException("Exit requested by user.");
}
