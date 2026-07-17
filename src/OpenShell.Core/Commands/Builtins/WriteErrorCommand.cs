using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Write-Error</c> command. Per ADR-0048 §2.3 / ADR-0026.
/// <para>
/// Constructs an <see cref="ErrorRecord"/> from the supplied message / category / target
/// and writes it to the host's <see cref="IErrorStream"/>. Yields nothing — the error
/// enters the structured error stream (stream 2), not the success stream.
/// </para>
/// <para>
/// If <see cref="CommandContext.Errors"/> is null (host without error stream), the
/// error is written to <see cref="Console.Error"/> as a graceful fallback so the
/// message is never lost.
/// </para>
/// </summary>
[Verb("Write", Noun = "Error", Aliases = ["we", "error"])]
[Description("Writes an object to the error stream.")]
public sealed class WriteErrorCommand : ICommand<WriteErrorCommand.Args>
{
    /// <summary>Arguments for <c>Write-Error</c>.</summary>
    /// <param name="Message">Error message text. Mandatory. Position 0.</param>
    /// <param name="Category">Error category name (per ADR-0026 §2). Default <see cref="ErrorCategory.Unknown"/> (PowerShell's <c>NotSpecified</c>-equivalent).</param>
    /// <param name="ErrorId">Optional error identifier for scripting.</param>
    /// <param name="TargetPath">Optional path of the target involved in the error.</param>
    /// <param name="Suggestion">Optional actionable suggestion shown to the user.</param>
    public record Args(
        [property: Parameter(Position = 0)] string? Message = null,
        [property: Parameter] string? Category = null,
        [property: Parameter] string? ErrorId = null,
        [property: Parameter] string? TargetPath = null,
        [property: Parameter] string? Suggestion = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var message = string.IsNullOrEmpty(args.Message) ? "Write-Error: (no message)" : args.Message!;
        var category = ParseCategory(args.Category);
        var targetPath = ParseTargetPath(args.TargetPath);

        var record = new ErrorRecord
        {
            Message = message,
            Category = category,
            Operation = "write-error",
            Phase = ErrorPhase.Operation,
            TargetPath = targetPath,
            Suggestion = args.Suggestion,
        };

        if (ctx.Errors is { } errors)
        {
            errors.Write(record);
        }
        else
        {
            // No structured error stream available — degrade to Console.Error so the message
            // is not lost (mirrors legacy hosts / minimal test setups).
            await Console.Error.WriteLineAsync(record.ToString()).ConfigureAwait(false);
        }

        yield break;
    }

    private static ErrorCategory ParseCategory(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ErrorCategory.Unknown;
        return Enum.TryParse<ErrorCategory>(name, ignoreCase: true, out var c)
            ? c
            : ErrorCategory.Unknown;
    }

    private static OpenShell.Paths.ItemPath? ParseTargetPath(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : OpenShell.Paths.ItemPath.Parse(raw);
}
