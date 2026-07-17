using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Interop;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Start-IpcServer</c> 命令。Per ADR-0021 §3.
/// 在后台线程启动 <see cref="IIpcChannel.StartAsync"/> (服务端模式, 阻塞直到客户端连接)。
/// 命令立即返回, 用户可通过 <c>Get-IpcStatus</c> 查询连接状态。
/// </summary>
[Verb("Start", Noun = "IpcServer", Aliases = ["ipc-start"])]
[Description("Starts the IPC server (Named Pipe / Unix Domain Socket) for GUI↔CLI interop.")]
public sealed class StartIpcServerCommand : ICommand<StartIpcServerCommand.Args>
{
    /// <summary>Arguments for <c>Start-IpcServer</c>. Currently takes no parameters.</summary>
    public record Args;

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = ctx.Host.Services.GetService(typeof(IIpcChannel)) as IIpcChannel;
        if (channel is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ConfigurationError,
                Message = "IPC channel is not registered in the service container.",
                Operation = "start-ipcserver",
                Phase = ErrorPhase.Parse,
                Suggestion = "Ensure IIpcChannel is registered in the host DI container.",
            });
            yield break;
        }

        if (channel.IsConnected)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = $"IPC channel is already connected on endpoint '{channel.ChannelName}'.",
                Operation = "start-ipcserver",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 后台启动服务端: StartAsync 阻塞直到客户端连接, 不能在 REPL 主线程调用。
        // 异常通过 Console.Error 输出 (命令已返回, 无法走 ctx.Errors)。
        _ = Task.Run(async () =>
        {
            try
            {
                await channel.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ipc] server start failed: {ex.Message}");
            }
        });

        await ctx.Host.WriteOutputLineAsync(
            $"IPC server starting on endpoint: {channel.ChannelName}", ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            $"  protocol version: {NamedPipeIpcChannel.CurrentProtocolVersion}", ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            "  waiting for client connection (use 'get-ipcstatus' to check)...", ct).ConfigureAwait(false);

        yield break;
    }
}
