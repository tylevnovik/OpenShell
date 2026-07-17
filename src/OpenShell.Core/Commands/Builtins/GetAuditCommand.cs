using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Security;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-Audit</c> command. Per ADR-0036 §15.
/// 查询审计日志, 输出 AuditEntry 列表 (时间 / 用户 / 命令 / 风险 / 是否批准 / 来源)。
/// </summary>
[Verb("Get", Noun = "Audit", Aliases = ["gaudit", "audit"])]
[Description("Queries the operation audit log.")]
[Help(
    Synopsis = "Queries the operation audit log (High+ risk operations recorded by the security service).",
    Examples = new[]
    {
        "get-audit                       # all entries",
        "get-audit -Since 2026-07-01     # entries since given date",
        "get-audit -Count 50             # last 50 entries (newest first)",
    },
    RelatedLinks = new[] { "clear-audit" })]
public sealed class GetAuditCommand : ICommand<GetAuditCommand.Args>
{
    /// <summary>Arguments for <c>Get-Audit</c>.</summary>
    /// <param name="Since">仅返回该时间之后的记录 (ISO-8601, 如 2026-07-01 或 2026-07-01T00:00:00Z)。</param>
    /// <param name="Count">返回的最大条数 (默认 100, 按时间倒序)。</param>
    public record Args(
        [property: Parameter] DateTimeOffset? Since = null,
        [property: Parameter] int Count = 100);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var audit = ctx.Host.Services.GetService(typeof(IAuditService)) as IAuditService;
        if (audit is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Audit service is not available in this context.",
                Operation = "get-audit",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var entries = await audit.QueryAsync(args.Since, ct).ConfigureAwait(false);

        // 按时间倒序 + 取最后 Count 条 (即最新的 N 条)。
        var ordered = entries.OrderByDescending(e => e.Timestamp).ToList();
        var take = args.Count > 0 ? args.Count : 100;
        var recent = ordered.Count <= take ? ordered : ordered.Take(take).ToList();

        if (recent.Count == 0)
        {
            await ctx.Host.WriteOutputLineAsync("(no audit entries)", ct).ConfigureAwait(false);
            yield break;
        }

        // 表头。
        await ctx.Host.WriteOutputLineAsync(
            "  Time".PadRight(24)
            + "User".PadRight(16)
            + "Risk".PadRight(12)
            + "Approved".PadRight(10)
            + "By".PadRight(10)
            + "Command", ct).ConfigureAwait(false);

        foreach (var entry in recent)
        {
            var time = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var approved = entry.Approved ? "yes" : "no";
            await ctx.Host.WriteOutputLineAsync(
                $"  {time,-22}{entry.User,-14}{entry.Risk,-12}{approved,-10}{entry.ApprovedBy,-10}{entry.Command} {entry.Args}",
                ct).ConfigureAwait(false);

            yield return new Item
            {
                Path = OpenShell.Paths.ItemPath.Parse($"audit::{entry.Timestamp.ToUnixTimeSeconds()}"),
                Kind = ItemKind.Property,
                Properties = PropertyBag.Empty
                    .With("Timestamp", entry.Timestamp)
                    .With("User", entry.User)
                    .With("Command", entry.Command)
                    .With("Args", entry.Args)
                    .With("Risk", entry.Risk.ToString())
                    .With("Approved", entry.Approved)
                    .With("ApprovedBy", entry.ApprovedBy),
            };
        }
    }
}
