using System.Runtime.CompilerServices;
using OpenShell.Configuration;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-Config</c> command. Per ADR-0022.
/// 读取配置项: 无参列出全部, 带参查询单项。
/// </summary>
[Verb("Get", Noun = "Config", Aliases = ["gcfg"])]
[Description("Reads configuration values.")]
[Help(
    Synopsis = "Reads configuration values (all or a specific key).",
    Examples = new[]
    {
        "get-config                   # list all config",
        "get-config theme             # query a specific key",
    },
    RelatedLinks = new[] { "set-config" })]
public sealed class GetConfigCommand : ICommand<GetConfigCommand.Args>
{
    /// <summary>Arguments for <c>Get-Config</c>.</summary>
    /// <param name="Name">配置项名称 (如 theme / promptStyle / historySize 等)。省略时列出全部。</param>
    public record Args(
        [property: Parameter(Position = 0)] string? Name = null);

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
                Operation = "get-config",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var config = configService.Config;

        if (!string.IsNullOrEmpty(args.Name))
        {
            // 查询单项: 按名称匹配。
            var value = ConfigAccessor.Get(config, args.Name!);
            await ctx.Host.WriteOutputLineAsync(
                $"{args.Name} = {value}", ct).ConfigureAwait(false);
            yield break;
        }

        // 列出全部标量配置项。
        await ctx.Host.WriteOutputLineAsync(
            "Key".PadRight(24) + "Value", ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            "theme".PadRight(24) + config.Theme, ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            "promptStyle".PadRight(24) + config.PromptStyle, ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            "historySize".PadRight(24) + config.HistorySize, ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            "maxParallelOperations".PadRight(24) + config.MaxParallelOperations, ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            "profileStopOnError".PadRight(24) + config.ProfileStopOnError, ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            "autoUpdate".PadRight(24) + config.AutoUpdate, ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            "updateChannel".PadRight(24) + config.UpdateChannel, ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            "updateCheckFrequency".PadRight(24) + config.UpdateCheckFrequency, ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            "includePrerelease".PadRight(24) + config.IncludePrerelease, ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            "securityRole".PadRight(24) + config.SecurityRole, ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            "securityStrictness".PadRight(24) + config.SecurityStrictness, ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            "protectedPaths".PadRight(24) + string.Join(";", config.ProtectedPaths), ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            "executionPolicy".PadRight(24) + config.ExecutionPolicy, ct).ConfigureAwait(false);

        if (config.Aliases.Count > 0)
        {
            await ctx.Host.WriteOutputLineAsync("", ct).ConfigureAwait(false);
            await ctx.Host.WriteOutputLineAsync("[aliases]", ct).ConfigureAwait(false);
            foreach (var kv in config.Aliases)
            {
                await ctx.Host.WriteOutputLineAsync(
                    $"  {kv.Key} = {kv.Value}", ct).ConfigureAwait(false);
            }
        }

        if (config.Variables.Count > 0)
        {
            await ctx.Host.WriteOutputLineAsync("", ct).ConfigureAwait(false);
            await ctx.Host.WriteOutputLineAsync("[variables]", ct).ConfigureAwait(false);
            foreach (var kv in config.Variables)
            {
                await ctx.Host.WriteOutputLineAsync(
                    $"  {kv.Key} = {kv.Value}", ct).ConfigureAwait(false);
            }
        }

        yield break;
    }
}
