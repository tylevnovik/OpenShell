using System.Runtime.CompilerServices;
using OpenShell.Configuration;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Updates;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Update-OpenShell</c> 命令。Per ADR-0037 §11.
/// 检查 → 下载 → 安装一气呵成。
/// ADR-0037 §13: 支持 <c>-Offline &lt;path&gt;</c> 跳过联网检查, 直接从本地包安装 (air-gapped 部署)。
/// </summary>
[Verb("Update", Noun = "OpenShell", Aliases = ["update-openshell"])]
[Description("Downloads and installs the latest OpenShell release.")]
[Help(
    Synopsis = "Checks, downloads and installs the latest OpenShell version via GitHub Releases.",
    Examples = new[]
    {
        "update-openshell           # check, download, and install",
        "update-openshell -Offline C:\\path\\to\\openshell-cli.exe   # install from a local package",
    },
    RelatedLinks = new[] { "check-update", "rollback-update" })]
public sealed class UpdateOpenShellCommand : ICommand<UpdateOpenShellCommand.Args>
{
    /// <summary>Arguments for <c>Update-OpenShell</c>.</summary>
    public record Args
    {
        /// <summary>
        /// 离线安装包绝对路径 (Per ADR-0037 §13). 设置时跳过 CheckForUpdatesAsync / DownloadAsync,
        /// 直接调用 <c>IUpdateService.InstallFromOfflineAsync</c>. 必须是已通过 SHA256 / 签名校验的本地副本。
        /// </summary>
        [Parameter(Aliases = ["offline-path"])]
        public string? Offline { get; init; }
    }

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
                Operation = "update-openshell",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // ADR-0037 §13: 离线安装路径 — 跳过联网检查。
        if (!string.IsNullOrWhiteSpace(args.Offline))
        {
            await ctx.Host.WriteOutputLineAsync(
                $"Installing from offline package: {args.Offline}", ct).ConfigureAwait(false);
            try
            {
                await updateService.InstallFromOfflineAsync(args.Offline!, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ctx.Errors?.Write(new ErrorRecord
                {
                    Category = ErrorCategory.IOError,
                    Message = $"Failed to install offline update: {ex.Message}",
                    Operation = "update-openshell",
                    Phase = ErrorPhase.Operation,
                    Exception = ex,
                });
                yield break;
            }
            await ctx.Host.WriteOutputLineAsync(
                "Offline install complete. Restart OpenShell for the new version to take effect.",
                ct).ConfigureAwait(false);
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
                Operation = "update-openshell",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }

        if (info is null)
        {
            await ctx.Host.WriteOutputLineAsync("Already up to date.", ct).ConfigureAwait(false);
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Found update to {info.Version} ({info.SizeBytes:N0} bytes).", ct).ConfigureAwait(false);

        var progress = new Progress<double>(p =>
        {
            // 简化进度显示：每 10% 一行
            var pct = (int)(p * 100);
            if (pct % 10 == 0)
            {
                Console.WriteLine($"  downloaded {pct}%");
            }
        });

        try
        {
            await updateService.DownloadAsync(info, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.IOError,
                Message = $"Failed to download update: {ex.Message}",
                Operation = "update-openshell",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            "Download complete. Installing...", ct).ConfigureAwait(false);

        try
        {
            await updateService.InstallAsync(info, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.IOError,
                Message = $"Failed to install update: {ex.Message}. Restart OpenShell and run 'rollback-update' to restore the previous version.",
                Operation = "update-openshell",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Updated to {info.Version}. Restart OpenShell for the new version to take effect.",
            ct).ConfigureAwait(false);

        yield break;
    }
}
