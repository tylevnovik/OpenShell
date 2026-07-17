using System.Runtime.CompilerServices;
using OpenShell.Commands;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;

namespace OpenShell.Providers.Remote.Commands;

/// <summary>
/// <c>Remove-SftpCredential</c> 命令 (ADR-0019 §3)。
/// 删除已配置的 SFTP 凭据。仅指定 host 时删除该 host 下所有凭据。
/// </summary>
[Verb("Remove", Noun = "SftpCredential", Aliases = ["rcred", "rm-sftpcred"])]
[Description("Removes SFTP credentials for a host (+ optional user).")]
[Help(
    Synopsis = "Removes SFTP credentials for a host, or a specific host+user combination.",
    Examples = new[]
    {
        "remove-sftpcredential example.com             # remove all credentials for host",
        "remove-sftpcredential example.com alice       # remove only alice@example.com",
    },
    RelatedLinks = new[] { "set-sftpcredential", "get-sftpcredential", "test-sftpconnection" })]
public sealed class RemoveSftpCredentialCommand : ICommand<RemoveSftpCredentialCommand.Args>
{
    /// <summary>Arguments for <c>Remove-SftpCredential</c>.</summary>
    /// <param name="Host">要删除凭据的 host (必填)。</param>
    /// <param name="User">可选: 仅删除该 user 的凭据。省略则删除 host 下所有凭据。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Host,
        [property: Parameter(Position = 1)] string? User = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var credProvider = ctx.Host.Services.GetService(typeof(InMemoryCredentialProvider)) as InMemoryCredentialProvider;
        if (credProvider is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "InMemoryCredentialProvider is not registered in the host services.",
                Operation = "remove-sftpcredential",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var removed = credProvider.RemoveCredentials(args.Host, args.User);
        if (removed)
        {
            await ctx.Host.WriteOutputLineAsync(
                args.User is null
                    ? $"Removed SFTP credentials for host '{args.Host}'."
                    : $"Removed SFTP credential for {args.User}@{args.Host}.",
                ct).ConfigureAwait(false);
        }
        else
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemNotFound,
                Message = args.User is null
                    ? $"No SFTP credentials found for host '{args.Host}'."
                    : $"No SFTP credential found for {args.User}@{args.Host}.",
                Operation = "remove-sftpcredential",
                Phase = ErrorPhase.Operation,
                Suggestion = "run 'get-sftpcredential' to list configured credentials",
            });
        }

        yield break;
    }
}
