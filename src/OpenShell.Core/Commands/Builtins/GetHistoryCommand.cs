using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.History;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-History</c> command. Per ADR-0020, ADR-0008 §3.
/// 列出最近执行的命令历史, 支持按数量和关键字过滤。
/// </summary>
[Verb("Get", Noun = "History", Aliases = ["history", "gh"])]
[Description("Lists command history, optionally filtered by -Count or -Query.")]
[Help(
    Synopsis = "Lists command history, optionally filtered by -Count or -Query.",
    Examples = new[]
    {
        "get-history                  # list all history",
        "get-history -Count 10       # last 10 entries",
        "get-history -Query \"cd\"   # search for commands containing 'cd'",
    },
    RelatedLinks = new[] { "clear-history" })]
public sealed class GetHistoryCommand : ICommand<GetHistoryCommand.Args>
{
    /// <summary>Arguments for <c>Get-History</c>.</summary>
    /// <param name="Count">返回的最大条数 (默认全部)。</param>
    /// <param name="Query">搜索关键字 (大小写不敏感子串匹配)。</param>
    public record Args(
        [property: Parameter] int? Count = null,
        [property: Parameter] string? Query = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var history = ctx.Host.Services.GetService(typeof(IHistoryService)) as IHistoryService;
        if (history is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "History service is not available in this context.",
                Operation = "get-history",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var entries = string.IsNullOrEmpty(args.Query)
            ? history.Recent
            : history.Search(args.Query!);

        if (args.Count is { } count && count > 0)
        {
            entries = entries.TakeLast(count).ToList();
        }

        if (entries.Count == 0)
        {
            await ctx.Host.WriteOutputLineAsync("(empty)", ct).ConfigureAwait(false);
            yield break;
        }

        // 表头: 序号 + 时间 + 结果 + 命令。
        await ctx.Host.WriteOutputLineAsync(
            "  #".PadRight(6) + "Time".PadRight(22) + "Exit".PadRight(8) + "Command", ct).ConfigureAwait(false);

        var idx = 1;
        foreach (var entry in entries)
        {
            var mark = entry.Success ? " " : "!";
            var time = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            await ctx.Host.WriteOutputLineAsync(
                $"{idx,4}. {time,-20} {entry.ExitCode,4}    {mark} {entry.Command}", ct).ConfigureAwait(false);
            idx++;
        }

        yield break;
    }
}
