using System.Text;
using OpenShell.Commands;
using OpenShell.Errors;
using OpenShell.Formatting;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Out-File</c> 命令：把上游 IItem 流渲染到文件。Per ADR-0011 §1.
/// 支持 <c>-Format</c> 选择 IFormatter（table/list/json/csv/md），<c>-Encoding</c>（utf-8/ascii/utf-16），
/// <c>-Append</c> 追加模式。Path 默认走 fs provider 的 InternalPath 直接 FileStream。
/// </summary>
[Verb("Out", Noun = "File", Aliases = ["of", "file"])]
[Description("Writes items to a file, optionally formatting as table/list/json/csv/md.")]
public sealed class OutFileCommand : IPipelineSink<OutFileCommand.Args>
{
    /// <param name="Path">目标文件路径。</param>
    /// <param name="Encoding">编码：utf-8 (默认), ascii, utf-16。</param>
    /// <param name="Append">true 追加，false 覆盖。</param>
    /// <param name="Format">格式：table (默认), list, json, csv, md/markdown。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter] string? Encoding = null,
        [property: Parameter] bool Append = false,
        [property: Parameter] string? Format = null);

    public async ValueTask Consume(IAsyncEnumerable<IItem> input, Args args, CommandContext ctx, CancellationToken cancellationToken)
    {
        var path = ResolvePath(args.Path, ctx);

        if (path.Provider != "fs")
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ProviderNotFound,
                Message = $"Out-File only supports fs provider, got '{path.Provider}'.",
                TargetPath = path,
                Operation = "out-file",
                Phase = ErrorPhase.ArgumentBinding,
            });
            return;
        }

        var encoding = ParseEncoding(args.Encoding);
        var mode = args.Append ? FileMode.Append : FileMode.Create;

        var formatter = ResolveFormatter(args.Format);
        var spec = new ViewSpec
        {
            Columns = Array.Empty<ColumnSpec>(),
            Kind = formatter.SupportedKind,
        };

        // 用 FileOutputHost 包装 StreamWriter，让 formatter 通过 IHost 接口写入文件。
        // FileStream 与 StreamWriter 的释放由 await using 负责；出错时不 Flush 已写入部分被丢弃。
        await using var stream = new FileStream(path.InternalPath, mode, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, encoding) { AutoFlush = false };
        var fileHost = new FileOutputHost(ctx.Host, writer);

        await formatter.FormatAsync(input, spec, fileHost, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Out-File is pipeline-only.");

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

    private static Encoding ParseEncoding(string? name)
        => (name?.ToLowerInvariant()) switch
        {
            "ascii" => Encoding.ASCII,
            "utf-16" => Encoding.Unicode,
            "utf-8" or null or "" => Encoding.UTF8,
            _ => Encoding.UTF8,
        };

    private static IFormatter ResolveFormatter(string? format)
        => (format?.ToLowerInvariant()) switch
        {
            "list" => new ListFormatter(),
            "json" => new JsonFormatter(),
            "csv" => new CsvFormatter(),
            "md" or "markdown" => new MarkdownFormatter(),
            _ => new TableFormatter(),
        };

    /// <summary>
    /// 把 StreamWriter 包装为 IHost，仅 WriteOutputLineAsync 走文件，
    /// 其余成员委派给原始 host（用于 Services / CurrentLocation 等读取）。
    /// </summary>
    private sealed class FileOutputHost : IHost
    {
        private readonly IHost _inner;
        private readonly StreamWriter _writer;

        public FileOutputHost(IHost inner, StreamWriter writer)
        {
            _inner = inner;
            _writer = writer;
        }

        public HostKind Kind => _inner.Kind;
        public ItemPath CurrentLocation { get => _inner.CurrentLocation; set => _inner.CurrentLocation = value; }
        public IObservable<IReadOnlyList<IItem>> Selection => _inner.Selection;
        public IProgress<OperationProgress> Progress => _inner.Progress;
        public IServiceProvider Services => _inner.Services;

        public async Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        public Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default)
            => _inner.WriteItemsAsync(items, cancellationToken);
    }
}
