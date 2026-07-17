using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Interop;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Get-IpcStatus</c> 命令。Per ADR-0021.
/// 查询当前 IPC 通道的状态 (endpoint / connected / protocol version)。
/// </summary>
[Verb("Get", Noun = "IpcStatus", Aliases = ["ipc-status"])]
[Description("Gets the current IPC channel status (endpoint, connected, protocol version).")]
public sealed class GetIpcStatusCommand : ICommand<GetIpcStatusCommand.Args>
{
    /// <summary>Arguments for <c>Get-IpcStatus</c>. Currently takes no parameters.</summary>
    public record Args;

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = ctx.Host.Services.GetService(typeof(IIpcChannel)) as IIpcChannel;

        var endpoint = channel?.ChannelName ?? "(not registered)";
        var connected = channel?.IsConnected ?? false;
        var version = NamedPipeIpcChannel.CurrentProtocolVersion;

        await Task.CompletedTask;

        yield return new Item
        {
            Path = ItemPath.Parse("ipc::status"),
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty
                .With("Endpoint", endpoint)
                .With("Connected", connected)
                .With("ProtocolVersion", version)
                .With("Platform", OperatingSystem.IsWindows() ? "NamedPipe" : "UnixDomainSocket"),
        };
    }
}
