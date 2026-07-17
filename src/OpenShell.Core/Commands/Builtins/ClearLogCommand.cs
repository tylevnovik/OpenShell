using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Logging;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Clear-Log</c> command. Per ADR-0031 §12.
/// 清空内存日志存储; 默认同时删除磁盘日志文件, <c>-KeepFiles</c> 可保留磁盘文件。
/// </summary>
[Verb("Clear", Noun = "Log", Aliases = ["cllog", "clog"])]
[Description("Clears the in-memory log store, optionally also disk log files.")]
[Help(
    Synopsis = "Clears the in-memory log store. Disk log files are removed unless -KeepFiles is set.",
    Examples = new[]
    {
        "clear-log                # clear memory + delete all openshell-*.log files",
        "clear-log -KeepFiles     # clear memory only, keep on-disk files",
    },
    RelatedLinks = new[] { "get-log", "get-error" })]
public sealed class ClearLogCommand : ICommand<ClearLogCommand.Args>
{
    /// <summary>Arguments for <c>Clear-Log</c>.</summary>
    /// <param name="KeepFiles">若为 true, 仅清空内存, 保留磁盘日志文件; 默认 false。</param>
    public record Args(
        [property: Parameter] bool KeepFiles = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var store = ctx.Host.Services.GetService(typeof(ILogStore)) as ILogStore;
        if (store is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Log store is not available in this context.",
                Operation = "clear-log",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        store.Clear();

        // 清空磁盘日志文件, 除非显式指定 -KeepFiles。
        var fileSink = ctx.Host.Services.GetService(typeof(FileLogSink)) as FileLogSink;
        if (!args.KeepFiles && fileSink is not null)
        {
            fileSink.ClearFiles();
            await ctx.Host.WriteOutputLineAsync("Log store and disk log files cleared.", ct).ConfigureAwait(false);
        }
        else
        {
            await ctx.Host.WriteOutputLineAsync("In-memory log store cleared.", ct).ConfigureAwait(false);
            if (args.KeepFiles)
            {
                await ctx.Host.WriteOutputLineAsync("(disk log files kept; -KeepFiles)", ct).ConfigureAwait(false);
            }
        }

        yield break;
    }
}
