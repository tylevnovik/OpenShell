using System.Runtime.CompilerServices;
using OpenShell.Configuration;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Set-UpdateChannel</c> 命令。Per ADR-0037 §11 / §2.
/// 切换更新通道 (stable / beta / dev) 并持久化到 <c>config.toml</c>。
/// </summary>
[Verb("Set", Noun = "UpdateChannel", Aliases = ["suc"])]
[Description("Sets the OpenShell update channel (stable, beta, or dev).")]
[Help(
    Synopsis = "Sets the update channel used by check-update / update-openshell.",
    Examples = new[]
    {
        "set-updatechannel stable   # use stable releases only (default)",
        "set-updatechannel beta      # include beta pre-releases",
        "set-updatechannel dev       # include dev/nightly pre-releases",
    },
    RelatedLinks = new[] { "check-update", "update-openshell", "get-config" })]
public sealed class SetUpdateChannelCommand : ICommand<SetUpdateChannelCommand.Args>
{
    /// <summary>允许的通道值 (大小写不敏感)。</summary>
    private static readonly HashSet<string> AllowedChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        "stable", "beta", "dev",
    };

    /// <summary>Arguments for <c>Set-UpdateChannel</c>.</summary>
    public record Args
    {
        /// <summary>通道名: stable / beta / dev (大小写不敏感)。</summary>
        [Parameter(Position = 0, Mandatory = true)]
        public string? Channel { get; init; }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(args.Channel) || !AllowedChannels.Contains(args.Channel))
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = $"Invalid update channel '{args.Channel}'. Allowed values: stable, beta, dev (case-insensitive).",
                Operation = "set-updatechannel",
                Phase = ErrorPhase.Parse,
            });
            yield break;
        }

        var configService = ctx.Host.Services.GetService(typeof(IConfigurationService)) as IConfigurationService;
        if (configService is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Configuration service is not available in this context.",
                Operation = "set-updatechannel",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 归一化为小写存储, 避免后续字符串比较的歧义。
        var normalized = args.Channel!.ToLowerInvariant();
        configService.Config.UpdateChannel = normalized;

        // dev / beta 通道自动启用 IncludePrerelease (stable 关闭)。
        configService.Config.IncludePrerelease = normalized != "stable";

        try
        {
            await configService.SaveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.IOError,
                Message = $"Failed to persist update channel: {ex.Message}",
                Operation = "set-updatechannel",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Update channel set to '{normalized}'. IncludePrerelease={configService.Config.IncludePrerelease}.",
            ct).ConfigureAwait(false);

        yield break;
    }
}
