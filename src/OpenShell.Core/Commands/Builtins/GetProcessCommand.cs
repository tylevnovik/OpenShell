using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Get-Process</c> 命令：列出进程。Per ADR-0048 §7.1.
/// <para>
/// 输出 <see cref="IItem"/> 列表，含 Id / Name / CPU / WS / PM / Path 等属性。
/// 支持 <c>-Name</c>（通配符）和 <c>-Id</c> 过滤。
/// </para>
/// </summary>
[Verb("Get", Noun = "Process", Aliases = ["ps", "gps"])]
[Description("Lists processes running on the system.")]
public sealed class GetProcessCommand : ICommand<GetProcessCommand.Args>, OpenShell.Pipeline.IPipelineSource
{
    /// <summary>Arguments for <c>Get-Process</c>.</summary>
    /// <param name="Name">进程名过滤（支持通配符，如 "powershell*"）。</param>
    /// <param name="Id">PID 过滤。</param>
    public record Args(
        [property: Parameter(Position = 0)] string[]? Name = null,
        [property: Parameter] int[]? Id = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var processes = Process.GetProcesses();

        foreach (var proc in processes)
        {
            ct.ThrowIfCancellationRequested();

            if (!MatchesFilters(proc, args))
            {
                proc.Dispose();
                continue;
            }

            yield return ToItem(proc);
        }

        await Task.CompletedTask;
    }

    private static bool MatchesFilters(Process proc, Args args)
    {
        if (args.Id is { Length: > 0 } ids)
        {
            var matched = false;
            foreach (var id in ids)
            {
                if (proc.Id == id) { matched = true; break; }
            }
            if (!matched) return false;
        }

        if (args.Name is { Length: > 0 } names)
        {
            var matched = false;
            foreach (var pattern in names)
            {
                if (MatchesGlob(proc.ProcessName, pattern))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched) return false;
        }

        return true;
    }

    /// <summary>简单通配符匹配（* 匹配任意，? 匹配单个字符，不区分大小写）。</summary>
    private static bool MatchesGlob(string name, string pattern)
    {
        if (pattern == "*") return true;
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);

        // 转换为正则：* → .*，? → .，其余转义。
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>把 <see cref="Process"/> 转为 <see cref="IItem"/>。</summary>
    private static IItem ToItem(Process proc)
    {
        // CPU 和内存属性可能抛异常（如进程已退出 / 权限不足），best-effort 读取。
        double? cpu = null;
        long? ws = null;
        long? pm = null;
        string? path = null;

        try { cpu = proc.TotalProcessorTime.TotalMilliseconds; } catch { }
        try { ws = proc.WorkingSet64; } catch { }
        try { pm = proc.PagedMemorySize64; } catch { }
        try { path = proc.MainModule?.FileName; } catch { }

        var props = PropertyBag.Empty
            .With("Id", proc.Id)
            .With("Name", proc.ProcessName)
            .With("CPU", cpu)
            .With("WS", ws)
            .With("PM", pm)
            .With("Path", path);

        return new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = proc.ProcessName },
            Kind = ItemKind.Property,
            Properties = props,
        };
    }
}
