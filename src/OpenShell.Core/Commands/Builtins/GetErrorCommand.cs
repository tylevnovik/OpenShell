using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-Error</c> command. Per ADR-0026 / ADR-0031 §12.
/// 从 <see cref="IErrorStream"/> 读最近错误, 列表输出。别名 <c>gerr</c> / <c>errors</c>。
/// </summary>
[Verb("Get", Noun = "Error", Aliases = ["gerr", "errors"])]
[Description("Lists recent error records from the error stream.")]
[Help(
    Synopsis = "Lists recent error records (most recent last).",
    Examples = new[]
    {
        "get-error              # last 50 errors (default)",
        "get-error -Count 10   # last 10 errors",
    },
    RelatedLinks = new[] { "get-log", "clear-history" })]
public sealed class GetErrorCommand : ICommand<GetErrorCommand.Args>
{
    /// <summary>Arguments for <c>Get-Error</c>.</summary>
    /// <param name="Count">返回的最大条数 (默认 50)。</param>
    public record Args(
        [property: Parameter] int Count = 50);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var errors = ctx.Errors;
        if (errors is null)
        {
            // 未注入错误流: 仅有命令内部 context 可访问; 此处提示并退出。
            await ctx.Host.WriteOutputLineAsync("(error stream not available)", ct).ConfigureAwait(false);
            yield break;
        }

        var all = errors.RecentErrors;
        var take = args.Count > 0 ? args.Count : 50;
        var recent = all.Count <= take ? all : all.Skip(all.Count - take).ToArray();

        if (recent.Count == 0)
        {
            await ctx.Host.WriteOutputLineAsync("(no recent errors)", ct).ConfigureAwait(false);
            yield break;
        }

        // 表头: 时间 + Category + Command + Message。
        await ctx.Host.WriteOutputLineAsync(
            "  Time".PadRight(24) + "Category".PadRight(20) + "Command".PadRight(20) + "Message", ct).ConfigureAwait(false);

        foreach (var err in recent)
        {
            var time = err.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var category = err.Category.ToString();
            var op = err.Operation ?? "";
            var msg = err.Message;
            await ctx.Host.WriteOutputLineAsync(
                $"  {time,-22}{category,-20}{op,-20}{msg}", ct).ConfigureAwait(false);

            if (err.TargetPath is { } p)
            {
                await ctx.Host.WriteOutputLineAsync(
                    $"  {"",22}{"",20}{"path",-20}{p.Display}", ct).ConfigureAwait(false);
            }
            if (err.Suggestion is { } s)
            {
                await ctx.Host.WriteOutputLineAsync(
                    $"  {"",22}{"",20}{"suggestion",-20}{s}", ct).ConfigureAwait(false);
            }
        }

        yield break;
    }
}
