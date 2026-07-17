using System.Runtime.CompilerServices;
using OpenShell.Commands;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;

namespace OpenShell.Providers.Remote.Commands;

/// <summary>
/// <c>Test-SftpConnection</c> 命令 (ADR-0019 §3)。
/// 测试到指定 host+user 的 SFTP 连接是否可用 (鉴权 + 文件系统访问)。
/// 凭据必须已通过 set-sftpcredential 配置。
/// </summary>
[Verb("Invoke", Noun = "SftpConnectionTest", Aliases = ["tsftp", "test-sftp"])]
[Description("Tests SFTP connectivity to a host (auth + filesystem access).")]
[Help(
    Synopsis = "Tests SFTP connectivity (authentication + filesystem listing) for a configured host+user.",
    Examples = new[]
    {
        "test-sftpconnection example.com alice            # default port 22",
        "test-sftpconnection example.com alice -Port 2222",
    },
    RelatedLinks = new[] { "set-sftpcredential", "get-sftpcredential", "remove-sftpcredential" })]
public sealed class TestSftpConnectionCommand : ICommand<TestSftpConnectionCommand.Args>
{
    /// <summary>Arguments for <c>Test-SftpConnection</c>.</summary>
    /// <param name="Host">远程主机。</param>
    /// <param name="User">登录用户名。</param>
    /// <param name="Port">SSH 端口, 默认 22。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Host,
        [property: Parameter(Position = 1, Mandatory = true)] string User,
        [property: Parameter] int Port = 22);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sftpProvider = ctx.Providers.Resolve<SftpProvider>("sftp");
        if (sftpProvider is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ProviderNotFound,
                Message = "SftpProvider is not registered. Verify 'sftp::' provider is loaded.",
                Operation = "test-sftpconnection",
                Phase = ErrorPhase.ProviderResolution,
            });
            yield break;
        }

        if (args.Port < 1 || args.Port > 65535)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = $"Invalid port: {args.Port}. Must be in range 1..65535.",
                Operation = "test-sftpconnection",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Connecting to {args.User}@{args.Host}:{args.Port} ...", ct).ConfigureAwait(false);

        var ok = await sftpProvider.TestConnectionAsync(args.User, args.Host, args.Port, ct).ConfigureAwait(false);

        yield return new Item
        {
            Path = new() { Provider = "sftp", InternalPath = $"{args.User}@{args.Host}:{args.Port}/" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty
                .With("Host", args.Host)
                .With("User", args.User)
                .With("Port", args.Port)
                .With("Connected", ok)
                .With("Status", ok ? "OK" : "Failed"),
        };

        await ctx.Host.WriteOutputLineAsync(
            ok
                ? $"  OK: connection to {args.User}@{args.Host}:{args.Port} succeeded."
                : $"  FAILED: connection to {args.User}@{args.Host}:{args.Port} failed. "
                  + "Check credentials (set-sftpcredential) and network.",
            ct).ConfigureAwait(false);
    }
}
