using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using OpenShell;
using OpenShell.Configuration;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Security;
using OpenShell.Sessions;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Start-Process</c> 命令：启动外部进程。Per ADR-0048 §7.2.
/// <para>
/// <c>-FilePath</c> 指定可执行文件路径，<c>-ArgumentList</c> 传参，
/// <c>-Wait</c> 等待退出，<c>-PassThru</c> 返回 <see cref="IItem"/> 表示的进程。
/// </para>
/// <para>
/// <see cref="SupportsShouldProcessAttribute"/>：进程生成是高风险操作，需确认。
/// </para>
/// </summary>
[Verb("Start", Noun = "Process", Aliases = ["saps", "start"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Medium)]
[Description("Starts a new process.")]
public sealed class StartProcessCommand : ICommand<StartProcessCommand.Args>
{
    /// <summary>Arguments for <c>Start-Process</c>.</summary>
    /// <param name="FilePath">可执行文件路径（mandatory）。</param>
    /// <param name="ArgumentList">命令行参数。</param>
    /// <param name="WorkingDirectory">工作目录。</param>
    /// <param name="Verb">Shell verb（Windows：open/edit/runas/print）。</param>
    /// <param name="WindowStyle">窗口风格：Normal/Hidden/Minimized/Maximized。</param>
    /// <param name="Wait">等待进程退出。</param>
    /// <param name="PassThru">返回进程对象。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string FilePath,
        [property: Parameter] string[]? ArgumentList = null,
        [property: Parameter] string? WorkingDirectory = null,
        [property: Parameter] string? Verb = null,
        [property: Parameter] string? WindowStyle = null,
        [property: Parameter] bool Wait = false,
        [property: Parameter] bool PassThru = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(args.FilePath))
        {
            yield break;
        }

        // ShouldProcess 确认（进程生成 Medium impact）。
        if (!ctx.ShouldProcess(args.FilePath, "Start process", ConfirmImpact.Medium))
            yield break;

        // ADR-0036 §12: 进程生成守卫 (第三方 Provider 沙箱 + GUI 宿主配置)。
        var config = ctx.Host.Services.GetService<IConfigurationService>()?.Config;
        var allowSpawnInGui = config?.AllowProcessSpawnInGui ?? false;
        try
        {
            ProcessSpawnGuard.EnsureAllowed(SandboxContext.Current, ctx.Host.Kind == HostKind.Gui, allowSpawnInGui);
        }
        catch (SecuritySandboxViolationException ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.PermissionDenied,
                Message = ex.Message,
                Operation = "start-process",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }

        var psi = new ProcessStartInfo
        {
            FileName = args.FilePath,
            UseShellExecute = !string.IsNullOrEmpty(args.Verb),
        };

        if (args.ArgumentList is { Length: > 0 } argList)
        {
            foreach (var arg in argList)
                psi.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrEmpty(args.WorkingDirectory))
            psi.WorkingDirectory = args.WorkingDirectory;

        // Verb（仅 UseShellExecute=true 时有效）。
        if (!string.IsNullOrEmpty(args.Verb))
            psi.Verb = args.Verb;

        // WindowStyle。
        if (!string.IsNullOrEmpty(args.WindowStyle))
        {
            psi.WindowStyle = args.WindowStyle.ToLowerInvariant() switch
            {
                "hidden" => ProcessWindowStyle.Hidden,
                "minimized" => ProcessWindowStyle.Minimized,
                "maximized" => ProcessWindowStyle.Maximized,
                _ => ProcessWindowStyle.Normal,
            };
        }

        // UseShellExecute=false 时不能重定向（除非显式配置），此处不处理重定向（简化实现）。
        var proc = Process.Start(psi);
        if (proc is null)
            yield break;

        // ADR-0034 §12: 将子进程注册到 ChildProcessTracker, 确保宿主退出时自动清理 (Job Object / SIGTERM)。
        ChildProcessTracker.AddProcess(proc);

        // 立即缓存进程名（进程退出后访问 ProcessName 会抛 InvalidOperationException）。
        var procName = proc.ProcessName;

        if (args.Wait)
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        if (args.PassThru)
        {
            yield return ToItem(proc, procName);
        }
        else
        {
            // 不 PassThru 时不需要保留进程对象引用。
            // 注意：不 dispose，因为进程可能仍在运行。
        }

        await Task.CompletedTask;
    }

    private static IItem ToItem(Process proc, string name)
    {
        var props = PropertyBag.Empty
            .With("Id", proc.Id)
            .With("Name", name)
            .With("ExitCode", proc.HasExited ? (object?)proc.ExitCode : null);

        return new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = name },
            Kind = ItemKind.Property,
            Properties = props,
        };
    }
}
