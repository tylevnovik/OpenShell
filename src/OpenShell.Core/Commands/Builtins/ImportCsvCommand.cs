using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Pipeline;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Import-Csv</c> 命令：从 CSV 文件读取对象。Per ADR-0048 §6.5.
/// <para>
/// 读取 CSV 文件，每行转为一个 <see cref="IItem"/>（PSCustomObject 风格）。
/// 第一行视为表头（除非 <c>-Header</c> 指定）。
/// </para>
/// <para>
/// 流式输出，逐行解析逐行 yield。
/// </para>
/// </summary>
[Verb("Import", Noun = "Csv", Aliases = ["ipcsv"])]
[Description("Imports objects from a CSV file.")]
public sealed class ImportCsvCommand : ICommand<ImportCsvCommand.Args>, OpenShell.Pipeline.IPipelineSource
{
    /// <summary>Arguments for <c>Import-Csv</c>.</summary>
    /// <param name="Path">CSV 文件路径。</param>
    /// <param name="Header">自定义列名（无表头时）。</param>
    /// <param name="Delimiter">分隔符（默认逗号）。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter] string[]? Header = null,
        [property: Parameter] char? Delimiter = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var delimiter = args.Delimiter ?? ',';
        var path = ResolvePath(args.Path, ctx);

        var contentProvider = ctx.Providers.ResolveCapability<IContentProvider>(path);
        if (contentProvider is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.CapabilityNotSupported,
                Message = $"Provider '{path.Provider}' does not support reading content.",
                TargetPath = path,
                Operation = "import-csv",
                Phase = ErrorPhase.ProviderResolution,
            });
            yield break;
        }

        await using var stream = await contentProvider.OpenReadAsync(path, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        List<string>? headers = args.Header is { Length: > 0 } h ? h.ToList() : null;
        var firstLine = true;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;

            // 跳过 #TYPE 行。
            if (line.StartsWith("#TYPE ", StringComparison.OrdinalIgnoreCase))
                continue;

            if (firstLine)
            {
                firstLine = false;
                if (headers is null)
                {
                    headers = ConvertFromCsvCommand.ParseCsvLine(line, delimiter);
                    continue;
                }
            }

            var fields = ConvertFromCsvCommand.ParseCsvLine(line, delimiter);
            yield return MakeObjectItem(headers ?? new List<string>(), fields);
        }
    }

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

    private static IItem MakeObjectItem(List<string> headers, List<string> fields)
    {
        var props = PropertyBag.Empty;
        for (int i = 0; i < headers.Count && i < fields.Count; i++)
        {
            props = props.With(headers[i], fields[i]);
        }
        return new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = "csv-row" },
            Kind = ItemKind.Property,
            Properties = props,
        };
    }
}
