using System.Globalization;
using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Get-Date</c> command. Per ADR-0048 §9.4.
/// <para>
/// Returns the current date/time (or a specified date). Supports <c>-Format</c> (.NET format string)
/// and <c>-UFormat</c> (Unix strftime-style, PS-compatible).
/// </para>
/// </summary>
[Verb("Get", Noun = "Date", Aliases = ["date"])]
[Description("Gets the current date and time.")]
public sealed class GetDateCommand : ICommand<GetDateCommand.Args>
{
    /// <summary>Arguments for <c>Get-Date</c>.</summary>
    public record Args(
        DateTime? Date = null,
        int? Year = null,
        int? Month = null,
        int? Day = null,
        int? Hour = null,
        int? Minute = null,
        int? Second = null,
        int? Millisecond = null,
        string? Format = null,
        string? UFormat = null,
        bool AsUTC = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var date = args.Date ?? DateTime.Now;

        // Apply component overrides
        if (args.Year is int y || args.Month is int mo || args.Day is int d
            || args.Hour is int h || args.Minute is int mi || args.Second is int s
            || args.Millisecond is int ms)
        {
            date = new DateTime(
                args.Year ?? date.Year,
                args.Month ?? date.Month,
                args.Day ?? date.Day,
                args.Hour ?? date.Hour,
                args.Minute ?? date.Minute,
                args.Second ?? date.Second,
                args.Millisecond ?? date.Millisecond);
        }

        if (args.AsUTC)
            date = date.ToUniversalTime();

        object? result;
        if (args.Format is not null)
            result = date.ToString(args.Format, CultureInfo.InvariantCulture);
        else if (args.UFormat is not null)
            result = FormatU(date, args.UFormat);
        else
            result = date;

        yield return new Item
        {
            Path = new Paths.ItemPath { Provider = "cli", InternalPath = "Get-Date" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty
                .With("DateTime", result)
                .With("Value", result),
        };
    }

    /// <summary>
    /// PS-compatible strftime-style formatting. Per ADR-0048 §9.4.
    /// Supports the common PS format specifiers.
    /// </summary>
    private static string FormatU(DateTime date, string format)
    {
        var sb = new System.Text.StringBuilder(format.Length);
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 >= format.Length)
            {
                sb.Append(format[i]);
                continue;
            }
            char c = format[++i];
            switch (c)
            {
                case 'Y': sb.Append(date.ToString("yyyy")); break;
                case 'y': sb.Append(date.ToString("yy")); break;
                case 'm': sb.Append(date.ToString("MM")); break;
                case 'd': sb.Append(date.ToString("dd")); break;
                case 'H': sb.Append(date.ToString("HH")); break;
                case 'M': sb.Append(date.ToString("mm")); break;
                case 'S': sb.Append(date.ToString("ss")); break;
                case 'A': sb.Append(date.ToString("dddd")); break;
                case 'a': sb.Append(date.ToString("ddd")); break;
                case 'B': sb.Append(date.ToString("MMMM")); break;
                case 'b': sb.Append(date.ToString("MMM")); break;
                case 'p': sb.Append(date.ToString("tt")); break;
                case 'Z': sb.Append(date.ToString("zzz")); break;
                case 'j': sb.Append(date.DayOfYear.ToString("000")); break;
                case 'w': sb.Append((int)date.DayOfWeek); break;
                case 'R': sb.Append(date.ToString("HH:mm")); break;
                case 'T': sb.Append(date.ToString("HH:mm:ss")); break;
                case 'D': sb.Append(date.ToString("MM/dd/yy")); break;
                case 'F': sb.Append(date.ToString("yyyy-MM-dd")); break;
                case '%': sb.Append('%'); break;
                default: sb.Append('%').Append(c); break;
            }
        }
        return sb.ToString();
    }
}
