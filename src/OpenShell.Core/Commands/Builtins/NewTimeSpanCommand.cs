using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>New-TimeSpan</c> command. Per ADR-0048 §9.3.
/// <para>
/// Returns a <see cref="TimeSpan"/>. Either <c>-Start</c>/<c>-End</c> pair or
/// individual component fields (<c>-Days</c>, <c>-Hours</c>, etc.) may be specified.
/// </para>
/// </summary>
[Verb("New", Noun = "TimeSpan", Aliases = ["timespan"])]
[Description("Creates a TimeSpan object.")]
public sealed class NewTimeSpanCommand : ICommand<NewTimeSpanCommand.Args>
{
    /// <summary>Arguments for <c>New-TimeSpan</c>.</summary>
    public record Args(
        DateTime? Start = null,
        DateTime? End = null,
        int Days = 0,
        int Hours = 0,
        int Minutes = 0,
        int Seconds = 0,
        int Milliseconds = 0);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        TimeSpan span;
        if (args.Start is not null || args.End is not null)
        {
            var start = args.Start ?? DateTime.Now;
            var end = args.End ?? DateTime.Now;
            span = end - start;
        }
        else
        {
            span = new TimeSpan(
                args.Days, args.Hours, args.Minutes, args.Seconds, args.Milliseconds);
        }

        yield return new Item
        {
            Path = new Paths.ItemPath { Provider = "cli", InternalPath = "New-TimeSpan" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty
                .With("TimeSpan", span)
                .With("Value", span)
                .With("TotalDays", span.TotalDays)
                .With("TotalHours", span.TotalHours)
                .With("TotalMinutes", span.TotalMinutes)
                .With("TotalSeconds", span.TotalSeconds),
        };
    }
}
