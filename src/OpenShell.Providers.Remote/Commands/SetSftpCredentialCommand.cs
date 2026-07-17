using System.Runtime.CompilerServices;
using OpenShell.Commands;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;

namespace OpenShell.Providers.Remote.Commands;

/// <summary>
/// <c>Set-SftpCredential</c> 命令 (ADR-0019 §3)。
/// 保存 SFTP 凭据到本地存储 (<c>~/.openshell/sftp-credentials.json</c>)。
/// 同 host+user 主键会覆盖旧凭据。
/// </summary>
[Verb("Set", Noun = "SftpCredential", Aliases = ["scred", "set-sftpcred"])]
[Description("Saves SFTP credentials for a host+user to the local credential store.")]
[Help(
    Synopsis = "Saves SFTP credentials (host + user + password or private key) to the local credential store.",
    Examples = new[]
    {
        "set-sftpcredential example.com alice -Password s3cret               # password auth, default port 22",
        "set-sftpcredential example.com alice -Port 2222 -Password s3cret    # custom port",
        "set-sftpcredential example.com alice -PrivateKeyPath ~/.ssh/id_rsa  # private key auth",
        "set-sftpcredential example.com alice -PrivateKeyPath ~/.ssh/id_rsa -PrivateKeyPassphrase secret",
    },
    RelatedLinks = new[] { "get-sftpcredential", "remove-sftpcredential", "test-sftpconnection" })]
public sealed class SetSftpCredentialCommand : ICommand<SetSftpCredentialCommand.Args>
{
    /// <summary>Arguments for <c>Set-SftpCredential</c>.</summary>
    /// <param name="Host">远程主机 (hostname or IP)。</param>
    /// <param name="User">登录用户名。</param>
    /// <param name="Password">密码 (与 PrivateKeyPath 二选一)。</param>
    /// <param name="Port">SSH 端口, 默认 22。</param>
    /// <param name="PrivateKeyPath">SSH 私钥文件本地路径。</param>
    /// <param name="PrivateKeyPassphrase">私钥 passphrase。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Host,
        [property: Parameter(Position = 1, Mandatory = true)] string User,
        [property: Parameter(Position = 2)] string? Password = null,
        [property: Parameter] int Port = 22,
        [property: Parameter] string? PrivateKeyPath = null,
        [property: Parameter] string? PrivateKeyPassphrase = null);

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
                Operation = "set-sftpcredential",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 校验: password 或 private key 至少有一个。
        if (string.IsNullOrEmpty(args.Password) && string.IsNullOrEmpty(args.PrivateKeyPath))
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "Either -Password or -PrivateKeyPath must be provided.",
                Operation = "set-sftpcredential",
                Phase = ErrorPhase.ArgumentBinding,
                Suggestion = "rerun with -Password <pw> or -PrivateKeyPath <path>",
            });
            yield break;
        }

        if (args.Port < 1 || args.Port > 65535)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = $"Invalid port: {args.Port}. Must be in range 1..65535.",
                Operation = "set-sftpcredential",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        var cred = new SftpCredentials
        {
            Host = args.Host,
            User = args.User,
            Port = args.Port,
            Password = args.Password,
            PrivateKeyPath = args.PrivateKeyPath,
            PrivateKeyPassphrase = args.PrivateKeyPassphrase,
        };
        credProvider.SetCredentials(cred);

        await ctx.Host.WriteOutputLineAsync(
            $"Saved SFTP credential for {args.User}@{args.Host}:{args.Port} "
            + (args.PrivateKeyPath is not null ? "(private key)" : "(password)"),
            ct).ConfigureAwait(false);

        yield break;
    }
}
