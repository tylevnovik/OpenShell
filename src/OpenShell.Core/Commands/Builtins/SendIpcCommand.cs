using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Interop;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Send-IpcCommand</c> 命令。Per ADR-0021 §1.
/// 向 IPC 对端发送 <see cref="IpcCommandRequest"/> (命令行 + 工作目录)。
/// 对端 (CLI 子进程) 执行后通过 <see cref="IpcCommandResponse"/> 返回结果。
/// </summary>
[Verb("Send", Noun = "IpcCommand", Aliases = ["ipc-send"])]
[Description("Sends a command request to the IPC peer (GUI↔CLI).")]
public sealed class SendIpcCommand : ICommand<SendIpcCommand.Args>
{
    /// <summary>Arguments for <c>Send-IpcCommand</c>.</summary>
    /// <param name="Message">要发送的命令行 (如 "get-childitem -Recurse")。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Message);

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
                Operation = "send-ipccommand",
                Phase = ErrorPhase.Parse,
                Suggestion = "Ensure IIpcChannel is registered in the host DI container.",
            });
            yield break;
        }

        if (!channel.IsConnected)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = $"IPC channel is not connected (endpoint: {channel.ChannelName}).",
                Operation = "send-ipccommand",
                Phase = ErrorPhase.Operation,
                Suggestion = "Run 'start-ipcserver' to start the IPC server, or ensure the peer is running.",
            });
            yield break;
        }

        var request = new IpcCommandRequest(
            CommandLine: args.Message,
            WorkingDirectory: ctx.CurrentLocation,
            RequestId: Guid.NewGuid());

        await channel.SendAsync(request, ct).ConfigureAwait(false);

        await ctx.Host.WriteOutputLineAsync(
            $"IPC command sent (request id: {request.RequestId:N}).", ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            $"  command: {request.CommandLine}", ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            $"  cwd: {request.WorkingDirectory.Display}", ct).ConfigureAwait(false);

        yield break;
    }
}
