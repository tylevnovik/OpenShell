using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Logging;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-Log</c> command. Per ADR-0031 §12.
/// 查询内存日志存储, 输出最近 N 条匹配日志。输出格式: 时间 + Level + Category + Message。
/// </summary>
[Verb("Get", Noun = "Log", Aliases = ["glog", "logs"])]
[Description("Queries recent structured log entries from the in-memory log store.")]
[Help(
    Synopsis = "Queries recent structured log entries from the in-memory log store.",
    Examples = new[]
    {
        "get-log                       # last 50 entries (default)",
        "get-log -Count 200            # last 200 entries",
        "get-log -Level error          # only Error/Critical entries",
        "get-log -Category CliHost     # filter by logger category",
        "get-log -Contains \"copy\"    # filter by message substring",
    },
    RelatedLinks = new[] { "clear-log", "get-error" })]
public sealed class GetLogCommand : ICommand<GetLogCommand.Args>
{
    /// <summary>Arguments for <c>Get-Log</c>.</summary>
    /// <param name="Count">返回的最大条数 (默认 50)。</param>
    /// <param name="Level">最低日志级别过滤 (trace/debug/info/warning/error/critical), 大小写不敏感。</param>
    /// <param name="Category">Logger 类别精确匹配 (大小写不敏感)。</param>
    /// <param name="Contains">消息子串过滤 (大小写不敏感)。</param>
    public record Args(
        [property: Parameter] int Count = 50,
        [property: Parameter] string? Level = null,
        [property: Parameter] string? Category = null,
        [property: Parameter] string? Contains = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var store = ctx.Host.Services.GetService(typeof(ILogStore)) as ILogStore;
        if (store is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Log store is not available in this context.",
                Operation = "get-log",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 解析 -Level 字符串到 LogLevel (大小写不敏感)。
        LogLevel? minLevel = null;
        if (!string.IsNullOrWhiteSpace(args.Level))
        {
            if (!Enum.TryParse<LogLevel>(args.Level, ignoreCase: true, out var parsed))
            {
                ctx.Errors?.Write(new ErrorRecord
                {
                    Category = ErrorCategory.InvalidArgument,
                    Message = $"unknown log level: '{args.Level}'. valid: trace/debug/info/warning/error/critical",
                    Operation = "get-log",
                    Phase = ErrorPhase.Parse,
                });
                yield break;
            }
            minLevel = parsed;
        }

        var filter = new LogFilter
        {
            MinLevel = minLevel,
            Category = args.Category,
            MessageContains = args.Contains,
        };

        // 取过滤后的全部结果, 再取最后 Count 条。
        var all = store.Filter(filter);
        var take = args.Count > 0 ? args.Count : 50;
        var recent = all.Count <= take ? all : all.Skip(all.Count - take).ToArray();

        if (recent.Count == 0)
        {
            await ctx.Host.WriteOutputLineAsync("(no matching log entries)", ct).ConfigureAwait(false);
            yield break;
        }

        // 表头: 时间 + Level + Category + Message。
        await ctx.Host.WriteOutputLineAsync(
            "  Time".PadRight(24) + "Level".PadRight(10) + "Category".PadRight(24) + "Message", ct).ConfigureAwait(false);

        foreach (var entry in recent)
        {
            var time = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var level = LevelToShort(entry.Level);
            await ctx.Host.WriteOutputLineAsync(
                $"  {time,-22}{level,-10}{entry.Category,-24}{entry.Message}", ct).ConfigureAwait(false);

            if (entry.Exception is { } ex)
            {
                await ctx.Host.WriteOutputLineAsync(
                    $"  {"",22}{"",10}{"exception",-24}{ex.GetType().Name}: {ex.Message}", ct).ConfigureAwait(false);
            }
        }

        yield break;
    }

    private static string LevelToShort(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT",
        _ => level.ToString().ToUpperInvariant(),
    };
}
