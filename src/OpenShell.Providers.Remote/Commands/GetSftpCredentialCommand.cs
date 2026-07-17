using System.Runtime.CompilerServices;
using OpenShell.Commands;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Providers.Remote.Commands;

/// <summary>
/// <c>Get-SftpCredential</c> 命令 (ADR-0019 §3)。
/// 列出已配置的 SFTP 凭据 (不输出 password 明文, 仅 HasPassword 标志)。
/// 可按 host 过滤。
/// </summary>
[Verb("Get", Noun = "SftpCredential", Aliases = ["gcred", "get-sftpcred"])]
[Description("Lists configured SFTP credentials (passwords masked).")]
[Help(
    Synopsis = "Lists configured SFTP credentials. Passwords are masked.",
    Examples = new[]
    {
        "get-sftpcredential                  # list all credentials",
        "get-sftpcredential example.com     # filter by host",
    },
    RelatedLinks = new[] { "set-sftpcredential", "remove-sftpcredential", "test-sftpconnection" })]
public sealed class GetSftpCredentialCommand : ICommand<GetSftpCredentialCommand.Args>
{
    /// <summary>Arguments for <c>Get-SftpCredential</c>.</summary>
    /// <param name="Host">可选: 按 host 过滤。</param>
    public record Args(
        [property: Parameter(Position = 0)] string? Host = null);

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
                Operation = "get-sftpcredential",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var all = credProvider.ListCredentials(args.Host);
        if (all.Count == 0)
        {
            await ctx.Host.WriteOutputLineAsync(
                args.Host is null
                    ? "No SFTP credentials configured. Run 'set-sftpcredential' to add one."
                    : $"No SFTP credentials found for host '{args.Host}'.",
                ct).ConfigureAwait(false);
            yield break;
        }

        foreach (var cred in all)
        {
            var path = new ItemPath
            {
                Provider = "sftp",
                InternalPath = $"{cred.User}@{cred.Host}:{cred.Port}/",
            };
            yield return new Item
            {
                Path = path,
                Kind = ItemKind.Property,
                Properties = PropertyBag.Empty
                    .With("Host", cred.Host)
                    .With("User", cred.User)
                    .With("Port", cred.Port)
                    .With("HasPassword", cred.Password is not null)
                    .With("PrivateKeyPath", cred.PrivateKeyPath ?? "")
                    .With("HasPrivateKeyPassphrase", cred.PrivateKeyPassphrase is not null),
            };
        }
    }
}
