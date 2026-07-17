using System.Runtime.CompilerServices;
using OpenShell.Configuration;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Set-Config</c> command. Per ADR-0022.
/// 设置配置项并持久化到 <c>config.toml</c>。
/// </summary>
[Verb("Set", Noun = "Config", Aliases = ["scfg"])]
[Description("Sets a configuration value and persists it.")]
[Help(
    Synopsis = "Sets a configuration value and persists it to config.toml.",
    Examples = new[]
    {
        "set-config theme light              # set theme",
        "set-config historySize 5000         # set history size",
        "set-config autoUpdate false         # disable auto-update",
    },
    RelatedLinks = new[] { "get-config" })]
public sealed class SetConfigCommand : ICommand<SetConfigCommand.Args>
{
    /// <summary>Arguments for <c>Set-Config</c>.</summary>
    /// <param name="Name">配置项名称。</param>
    /// <param name="Value">配置项值。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Name,
        [property: Parameter(Position = 1, Mandatory = true)] string Value);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var configService = ctx.Host.Services.GetService(typeof(IConfigurationService)) as IConfigurationService;
        if (configService is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Configuration service is not available in this context.",
                Operation = "set-config",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        if (!ConfigAccessor.TrySet(configService.Config, args.Name, args.Value))
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = $"Unknown or read-only config key: '{args.Name}'. Valid keys: theme, promptStyle, historySize, maxParallelOperations, profileStopOnError, autoUpdate, updateChannel, updateCheckFrequency, includePrerelease.",
                Operation = "set-config",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        try
        {
            await configService.SaveAsync(ct).ConfigureAwait(false);
            await ctx.Host.WriteOutputLineAsync(
                $"Set {args.Name} = {args.Value} (saved).", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.IOError,
                Message = $"Failed to persist config: {ex.Message}",
                Operation = "set-config",
                Phase = ErrorPhase.Operation,
            });
        }

        yield break;
    }
}
