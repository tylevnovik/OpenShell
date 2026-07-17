using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Set-Date</c> command. Per ADR-0048 §9.5.
/// <para>
/// Sets the system time. <strong>Destructive</strong> — requires admin rights.
/// Windows: <c>SetSystemTime</c>; Unix: <c>settimeofday</c>.
/// Declares <c>[SupportsShouldProcess]</c> per ADR-0049.
/// </para>
/// </summary>
[Verb("Set", Noun = "Date", Aliases = ["date-set"])]
[SupportsShouldProcess]
[Description("Sets the system date and time. Requires admin rights.")]
public sealed class SetDateCommand : ICommand<SetDateCommand.Args>
{
    /// <summary>Arguments for <c>Set-Date</c>.</summary>
    /// <param name="Date">Target date/time.</param>
    /// <param name="Adjust">TimeSpan adjustment.</param>
    public record Args(
        DateTime? Date = null,
        TimeSpan? Adjust = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        DateTime target;
        if (args.Date is not null)
            target = args.Date.Value;
        else if (args.Adjust is not null)
            target = DateTime.Now + args.Adjust.Value;
        else
            throw new ArgumentException("Set-Date requires -Date or -Adjust.");

        // ShouldProcess gate (per ADR-0049 §8)
        if (!ctx.ShouldProcess($"system time to {target:O}", "Set-Date"))
            yield break;

        if (OperatingSystem.IsWindows())
        {
            SetWindowsSystemTime(target);
        }
        else
        {
            SetUnixSystemTime(target);
        }

        yield return new Item
        {
            Path = new Paths.ItemPath { Provider = "cli", InternalPath = "Set-Date" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty.With("Value", target),
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetSystemTime(ref SYSTEMTIME lpSystemTime);

    private static void SetWindowsSystemTime(DateTime dt)
    {
        var utc = dt.ToUniversalTime();
        var st = new SYSTEMTIME
        {
            wYear = (ushort)utc.Year,
            wMonth = (ushort)utc.Month,
            wDay = (ushort)utc.Day,
            wHour = (ushort)utc.Hour,
            wMinute = (ushort)utc.Minute,
            wSecond = (ushort)utc.Second,
            wMilliseconds = (ushort)utc.Millisecond,
        };
        if (!SetSystemTime(ref st))
            throw new UnauthorizedAccessException("Set-Date failed: requires administrator rights.");
    }

    private static void SetUnixSystemTime(DateTime dt)
    {
        // Unix settimeofday requires root; this is a placeholder
        throw new PlatformNotSupportedException("Set-Date on Unix requires root and settimeofday().");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
    }
}
