using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Startup;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Reload-Profile</c> command. Per ADR-0041 §8.
/// 重新执行 profile 脚本（用户全局 + 项目级），无需重启会话。
/// </summary>
/// <remarks>
/// M1 简化：<c>reload-profile</c> 不会清空当前 Session 级状态（别名 / 函数 / 变量 / drive 持久），
/// 只是在已有状态之上重新执行 profile 命令。ADR-0041 §8 完整版本要求清空 Session 状态，
/// 该行为留待后续阶段实现。
/// </remarks>
[Verb("Reload", Noun = "Profile", Aliases = ["reload"])]
[Description("Re-executes profile scripts without restarting the session.")]
public sealed class ReloadProfileCommand : ICommand<ReloadProfileCommand.Args>
{
    /// <summary>Arguments for <c>Reload-Profile</c>. Currently takes no parameters.</summary>
    public record Args;

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var services = ctx.Host.Services;
        if (services is null)
        {
            await ctx.Host.WriteOutputLineAsync(
                "reload-profile: host service provider is not available.", ct).ConfigureAwait(false);
            yield break;
        }

        var profileLoader = (IProfileLoader?)services.GetService(typeof(IProfileLoader));
        if (profileLoader is null)
        {
            await ctx.Host.WriteOutputLineAsync(
                "reload-profile: IProfileLoader is not registered.", ct).ConfigureAwait(false);
            yield break;
        }

        var executor = (ICommandLineExecutor?)services.GetService(typeof(ICommandLineExecutor));
        if (executor is null)
        {
            await ctx.Host.WriteOutputLineAsync(
                "reload-profile: ICommandLineExecutor is not registered.", ct).ConfigureAwait(false);
            yield break;
        }

        ProfileExecutionResult result;
        try
        {
            result = await profileLoader.ExecuteAsync(
                line => executor.ExecuteAsync(line, ct),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(ErrorRecord.FromException(
                ex,
                operation: "reload-profile",
                phase: ErrorPhase.Operation));
            await ctx.Host.WriteOutputLineAsync(
                $"reload-profile: failed - {ex.Message}", ct).ConfigureAwait(false);
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Reloaded profile: {result.ExecutedFiles.Count} file(s), {result.LinesExecuted} line(s) executed.",
            ct).ConfigureAwait(false);

        if (result.Errors.Count > 0)
        {
            await ctx.Host.WriteOutputLineAsync(
                $"  {result.Errors.Count} error(s) reported. Use 'get-error' to inspect.",
                ct).ConfigureAwait(false);
        }

        if (!result.Success)
        {
            await ctx.Host.WriteOutputLineAsync(
                "  profile execution was aborted due to fatal errors.",
                ct).ConfigureAwait(false);
        }

        yield break;
    }
}
