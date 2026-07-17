using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Stop-Process</c> 命令：终止进程。Per ADR-0048 §7.3.
/// <para>
/// 通过 <c>-Id</c> 或 <c>-Name</c> 指定目标进程。<c>-Force</c> 强制终止（kill -9 语义）。
/// </para>
/// <para>
/// <see cref="SupportsShouldProcessAttribute"/>：破坏性操作，<see cref="ConfirmImpact.High"/>。
/// </para>
/// </summary>
[Verb("Stop", Noun = "Process", Aliases = ["spps", "kill"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.High)]
[Description("Stops a running process.")]
public sealed class StopProcessCommand : ICommand<StopProcessCommand.Args>
{
    /// <summary>Arguments for <c>Stop-Process</c>.</summary>
    /// <param name="Id">要终止的进程 ID。</param>
    /// <param name="Name">要终止的进程名（通配符）。</param>
    /// <param name="Force">强制终止。</param>
    /// <param name="PassThru">返回被终止的进程对象。</param>
    public record Args(
        [property: Parameter] int[]? Id = null,
        [property: Parameter] string[]? Name = null,
        [property: Parameter] bool Force = false,
        [property: Parameter] bool PassThru = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (args.Id is null && args.Name is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "Either -Id or -Name must be specified.",
                Operation = "stop-process",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        var targets = ResolveTargets(args);

        foreach (var proc in targets)
        {
            ct.ThrowIfCancellationRequested();

            // ShouldProcess 确认（High impact）。
            if (!ctx.ShouldProcess($"{proc.ProcessName} (pid {proc.Id})", "Stop process", ConfirmImpact.High))
            {
                proc.Dispose();
                continue;
            }

            if (args.PassThru)
                yield return ToItem(proc);

            try
            {
                proc.Kill(entireProcessTree: args.Force);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ctx.Errors?.Write(new ErrorRecord
                {
                    Category = ErrorCategory.OperationFailed,
                    Message = $"Failed to stop process '{proc.ProcessName}' (pid {proc.Id}): {ex.Message}",
                    Operation = "stop-process",
                    Phase = ErrorPhase.Operation,
                });
            }
            finally
            {
                proc.Dispose();
            }
        }

        await Task.CompletedTask;
    }

    private static List<Process> ResolveTargets(Args args)
    {
        var result = new List<Process>();

        if (args.Id is { Length: > 0 } ids)
        {
            foreach (var id in ids)
            {
                try
                {
                    var proc = Process.GetProcessById(id);
                    result.Add(proc);
                }
                catch (ArgumentException)
                {
                    // 进程不存在，跳过。
                }
            }
        }

        if (args.Name is { Length: > 0 } names)
        {
            foreach (var name in names)
            {
                Process[] found;
                try
                {
                    found = Process.GetProcessesByName(name);
                }
                catch
                {
                    continue;
                }
                result.AddRange(found);
            }
        }

        return result;
    }

    private static IItem ToItem(Process proc)
    {
        var props = PropertyBag.Empty
            .With("Id", proc.Id)
            .With("Name", proc.ProcessName);

        return new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = proc.ProcessName },
            Kind = ItemKind.Property,
            Properties = props,
        };
    }
}
