using System.Runtime.CompilerServices;
using OpenShell.Configuration;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Updates;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Check-Update</c> 命令。Per ADR-0037 §11.
/// 主动检查 GitHub Releases 上是否有可用更新；同时按 <c>updateCheckFrequency</c> 持久化最后检查时间。
/// </summary>
[Verb("Check", Noun = "Update", Aliases = ["check-update"])]
[Description("Checks GitHub Releases for a newer OpenShell version.")]
[Help(
    Synopsis = "Checks for OpenShell updates via GitHub Releases.",
    Examples = new[]
    {
        "check-update           # check for updates now",
    },
    RelatedLinks = new[] { "update-openshell", "rollback-update" })]
public sealed class CheckUpdateCommand : ICommand<CheckUpdateCommand.Args>
{
    /// <summary>Arguments for <c>Check-Update</c>.</summary>
    public record Args;

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var updateService = ctx.Host.Services.GetService(typeof(IUpdateService)) as IUpdateService;
        if (updateService is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Update service is not available in this context.",
                Operation = "check-update",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 把 IncludePrerelease 配置注入到服务 (如果是 GitHubReleasesUpdateService)。
        var configService = ctx.Host.Services.GetService(typeof(IConfigurationService)) as IConfigurationService;
        if (configService is not null && updateService is GitHubReleasesUpdateService gh)
        {
            gh.IncludePrerelease = configService.Config.IncludePrerelease;
        }

        await ctx.Host.WriteOutputLineAsync("Checking for updates...", ct).ConfigureAwait(false);

        UpdateInfo? info;
        try
        {
            info = await updateService.CheckForUpdatesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NetworkError,
                Message = $"Failed to check for updates: {ex.Message}",
                Operation = "check-update",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }

        // 更新最后检查时间 (ADR-0037 §2)。
        var stateStore = ctx.Host.Services.GetService(typeof(UpdateStateStore)) as UpdateStateStore;
        try { stateStore?.WriteLastCheckTime(DateTimeOffset.UtcNow); } catch { /* best-effort */ }

        if (info is null)
        {
            await ctx.Host.WriteOutputLineAsync("Already up to date.", ct).ConfigureAwait(false);
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"A new version {info.Version} is available!", ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            $"  Published: {info.PublishedAt:yyyy-MM-dd HH:mm:ss} UTC", ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync(
            $"  Size: {info.SizeBytes:N0} bytes", ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(info.ReleaseNotes))
        {
            await ctx.Host.WriteOutputLineAsync("  Release notes:", ct).ConfigureAwait(false);
            await ctx.Host.WriteOutputLineAsync(info.ReleaseNotes, ct).ConfigureAwait(false);
        }
        await ctx.Host.WriteOutputLineAsync(
            "Run 'update-openshell' to download and install.", ct).ConfigureAwait(false);

        yield break;
    }
}
