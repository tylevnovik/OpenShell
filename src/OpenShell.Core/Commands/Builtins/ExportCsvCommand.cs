using System.Text;
using OpenShell.Commands;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Pipeline;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Export-Csv</c> 命令：对象写入 CSV 文件。Per ADR-0048 §6.6.
/// <para>
/// 管道 sink：消费 <see cref="IItem"/> 流，写入 CSV 文件。
/// 首行写表头，后续行写值。支持 <c>-Append</c> 追加、<c>-NoTypeInformation</c>（默认 true）。
/// </para>
/// <para>
/// <see cref="SupportsShouldProcessAttribute"/>：覆盖已存在文件时需确认。
/// </para>
/// </summary>
[Verb("Export", Noun = "Csv", Aliases = ["epcsv"], PipelineOnly = true)]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Low)]
[Description("Exports objects to a CSV file.")]
public sealed class ExportCsvCommand : IPipelineSink<ExportCsvCommand.Args>
{
    /// <summary>Arguments for <c>Export-Csv</c>.</summary>
    /// <param name="Path">目标 CSV 文件路径。</param>
    /// <param name="NoTypeInformation">去掉首行 #TYPE 头（默认 true，与 PS 6+ 一致）。</param>
    /// <param name="Delimiter">分隔符（默认逗号）。</param>
    /// <param name="Append">追加到已存在文件。</param>
    /// <param name="Force">覆盖只读文件。</param>
    /// <param name="NoClobber">不覆盖已存在文件。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter] bool NoTypeInformation = true,
        [property: Parameter] char? Delimiter = null,
        [property: Parameter] bool Append = false,
        [property: Parameter] bool Force = false,
        [property: Parameter] bool NoClobber = false);

    public async ValueTask Consume(IAsyncEnumerable<IItem> input, Args args, CommandContext ctx, CancellationToken cancellationToken)
    {
        var delimiter = args.Delimiter ?? ',';
        var path = ResolvePath(args.Path, ctx);

        if (path.Provider != "fs")
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ProviderNotFound,
                Message = $"Export-Csv only supports fs provider, got '{path.Provider}'.",
                TargetPath = path,
                Operation = "export-csv",
                Phase = ErrorPhase.ArgumentBinding,
            });
            return;
        }

        var fsPath = path.InternalPath;

        // -NoClobber 检查。
        if (args.NoClobber && File.Exists(fsPath))
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemAlreadyExists,
                Message = $"File '{fsPath}' already exists and -NoClobber was specified.",
                TargetPath = path,
                Operation = "export-csv",
                Phase = ErrorPhase.ArgumentBinding,
            });
            return;
        }

        // ShouldProcess 确认（覆盖时 Low impact）。
        var fileExists = File.Exists(fsPath);
        if (!args.Append && fileExists)
        {
            if (!ctx.ShouldProcess(fsPath, "Export CSV (overwrite)", ConfirmImpact.Low))
                return;
        }

        var mode = args.Append ? FileMode.Append : FileMode.Create;
        var encoding = Encoding.UTF8;

        await using var stream = new FileStream(fsPath, mode, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, encoding) { AutoFlush = false };

        var first = true;
        List<string>? columns = null;
        var hasWrittenHeader = args.Append && fileExists;

        await foreach (var item in input.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (first)
            {
                first = false;
                columns = DiscoverColumns(item);

                // 表头（仅非追加或新文件时写）。
                if (!hasWrittenHeader)
                {
                    if (!args.NoTypeInformation)
                        await writer.WriteLineAsync($"#TYPE {item.Kind}").ConfigureAwait(false);
                    await writer.WriteLineAsync(BuildHeader(columns, delimiter)).ConfigureAwait(false);
                    hasWrittenHeader = true;
                }
            }

            if (columns is null) continue;

            await writer.WriteLineAsync(BuildRow(item, columns, delimiter)).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Export-Csv is pipeline-only.");

    private static ItemPath ResolvePath(ItemPath path, CommandContext ctx)
    {
        // 非 fs provider 的路径：不与 fs CurrentLocation 组合（跨 provider 路径不互通）。
        if (path.Provider != "fs" || path.IsRooted)
            return path;
        // fs 相对路径：在 fs CurrentLocation 下组合。
        return ctx.CurrentLocation.Provider == "fs"
            ? ctx.CurrentLocation.Combine(path.InternalPath)
            : new ItemPath { Provider = "fs", InternalPath = path.InternalPath };
    }

    private static List<string> DiscoverColumns(IItem sample)
    {
        var columns = new List<string> { "Name", "Path", "Kind", "Size" };
        foreach (var key in sample.Properties.Values.Keys.Order(StringComparer.Ordinal))
        {
            if (!columns.Contains(key))
                columns.Add(key);
        }
        return columns;
    }

    private static string BuildHeader(List<string> columns, char delimiter)
        => string.Join(delimiter, columns.Select(EscapeField));

    private static string BuildRow(IItem item, List<string> columns, char delimiter)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0) sb.Append(delimiter);
            var value = GetColumnValue(item, columns[i]);
            sb.Append(EscapeField(value ?? string.Empty));
        }
        return sb.ToString();
    }

    private static string? GetColumnValue(IItem item, string columnName)
        => columnName switch
        {
            "Name" => item.Name,
            "Path" => item.Path.Display,
            "Kind" => item.Kind.ToString(),
            "Size" => item.Size?.ToString() ?? "",
            _ => item.Properties[columnName]?.ToString() ?? "",
        };

    private static string EscapeField(string field)
    {
        if (field.Length == 0) return string.Empty;
        if (field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return field;

        var sb = new StringBuilder(field.Length + 2);
        sb.Append('"');
        foreach (var ch in field)
        {
            if (ch == '"') sb.Append("\"\"");
            else sb.Append(ch);
        }
        sb.Append('"');
        return sb.ToString();
    }
}
