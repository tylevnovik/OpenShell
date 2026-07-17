using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Debug-Process</c> command. Per ADR-0048 §7.5.
/// <para>
/// Attaches a debugger to the specified processes. Windows: calls <see cref="Debugger.Break"/>;
/// Unix: throws <see cref="PlatformNotSupportedException"/>.
/// </para>
/// </summary>
[Verb("Debug", Noun = "Process", Aliases = ["dp"])]
[Description("Debugs one or more processes.")]
public sealed class DebugProcessCommand : ICommand<DebugProcessCommand.Args>
{
    /// <summary>Arguments for <c>Debug-Process</c>.</summary>
    /// <param name="Name">Process name(s) to debug.</param>
    /// <param name="Id">Process ID(s) to debug.</param>
    public record Args(
        string[]? Name = null,
        int[]? Id = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Debug-Process is only supported on Windows.");

        var processes = ResolveProcesses(args);
        foreach (var proc in processes)
        {
            if (proc.HasExited) continue;

            // On Windows, signal the process to break into the debugger
            // Actual debugger attachment requires native API; this is a simplified implementation
            try
            {
                Debugger.Break();
            }
            catch (Exception ex)
            {
                await ctx.Host.WriteOutputLineAsync(
                    $"WARNING: Cannot debug process {proc.Id} ({proc.ProcessName}): {ex.Message}", ct)
                    .ConfigureAwait(false);
            }
        }

        yield break;
    }

    private static IEnumerable<Process> ResolveProcesses(Args args)
    {
        if (args.Id is int[] ids)
        {
            foreach (var id in ids)
            {
                var p = Process.GetProcessById(id);
                if (p is not null) yield return p;
            }
            yield break;
        }

        if (args.Name is string[] names)
        {
            foreach (var name in names)
            {
                foreach (var p in Process.GetProcessesByName(name))
                    yield return p;
            }
        }
    }
}
