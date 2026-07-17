using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Remoting;
using Microsoft.Extensions.DependencyInjection;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>New-PSSession</c> 命令。Per ADR-0059 §6.
/// 建立 SSH 远程会话, 返回会话 Id。
/// </summary>
[Verb("New", Noun = "PSSession", Aliases = ["nsn"])]
[Description("Creates a persistent SSH remote session.")]
[Help(
    Synopsis = "Creates a persistent SSH remote session (New-PSSession).",
    Examples = new[]
    {
        "new-pssession -HostName user@host          # create session, outputs Id",
        "new-pssession -HostName user@host -Name srv # create with friendly name",
    },
    RelatedLinks = new[] { "invoke-command", "get-pssession", "remove-pssession" })]
public sealed class NewPSSessionCommand : ICommand<NewPSSessionCommand.Args>
{
    /// <summary>Arguments for <c>New-PSSession</c>.</summary>
    /// <param name="HostName">目标主机 (user@host 或 host)。必填。</param>
    /// <param name="Name">会话友好名。可选。</param>
    /// <param name="Port">SSH 端口。默认 22。</param>
    public record Args(
        [property: Parameter] string HostName,
        [property: Parameter] string? Name = null,
        [property: Parameter] int Port = 22);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var manager = ctx.Host.Services.GetService(typeof(PSSessionManager)) as PSSessionManager;
        if (manager is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Remoting service is not available in this context.",
                Operation = "new-pssession",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var options = new PSSessionOptions
        {
            HostName = args.HostName,
            Name = args.Name,
            Port = args.Port,
        };

        IPSSession session;
        try
        {
            session = manager.Create(options);
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = $"failed to create PSSession to {args.HostName}: {ex.Message}",
                Operation = "new-pssession",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"PSSession {session.Id} created (transport={session.Transport}, computer={session.ComputerName})",
            ct).ConfigureAwait(false);

        // 同时 yield 会话信息作为 IItem, 便于管道消费。
        yield return new Item
        {
            Path = OpenShell.Paths.ItemPath.Root("remoting"),
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty
                .With("Id", session.Id)
                .With("ComputerName", session.ComputerName)
                .With("Transport", session.Transport)
                .With("Name", session.Name)
                .With("Session", session),
        };
    }
}
