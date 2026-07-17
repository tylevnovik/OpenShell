using System.Runtime.CompilerServices;
using System.Threading.Channels;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Pipeline;
using OpenShell.Preview;
using OpenShell.Providers;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Search-Content</c> 命令。Per ADR-0030 §5.
/// grep 风格搜索文件内容。流式枚举文件, 用 <see cref="StreamReader"/> 扫描每文件内容,
/// 二进制跳过 (前 8KB 含 \0), 大文件不全加载 (限制 10MB), 匹配行收集为 <see cref="SearchResultItem"/>。
/// </summary>
/// <remarks>
/// 并发扫描 (默认 4 线程, Per ADR-0030 §5) 通过 <c>Parallel.ForEachAsync</c> +
/// <c>Channel&lt;T&gt;</c> 实现, 保持流式输出语义。结果顺序不保证 (并发完成顺序)。
/// 进度通过 <see cref="IHost.Progress"/> 报告 (Per ADR-0030 §5: 进度更新),
/// 节流 50ms (Per ADR-0044 §10)。
/// </remarks>
[Verb("Search", Noun = "Content", Aliases = ["search-content"])]
[Description("Searches file contents for a pattern (grep-style).")]
public sealed class SearchContentCommand : ICommand<SearchContentCommand.Args>, IPipelineSource
{
    private const long MaxFileSize = 10 * 1024 * 1024; // 10MB
    private const int BinaryCheckBytes = 8 * 1024;     // 8KB
    /// <summary>并发扫描线程数。Per ADR-0030 §5: 默认 4 线程。</summary>
    private const int MaxConcurrentScans = 4;
    /// <summary>进度报告节流间隔。Per ADR-0044 §10: 50ms (≤20Hz)。</summary>
    private const long ProgressThrottleTicks = 50 * TimeSpan.TicksPerMillisecond;

    /// <summary>Arguments for <c>Search-Content</c>.</summary>
    /// <param name="Path">搜索根路径 (默认当前路径)。</param>
    /// <param name="Pattern">搜索模式 (子串, 必填)。</param>
    /// <param name="Include">glob 过滤, 如 <c>*.cs</c>。</param>
    public record Args(
        [property: Parameter(Aliases = new[] { "-Path" })] ItemPath? Path = null,
        [property: Parameter(Position = 0, Mandatory = true)] string Pattern = "",
        [property: Parameter(Aliases = new[] { "-Include" })] string? Include = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var root = args.Path ?? ctx.CurrentLocation;

        // 解析裸路径: 继承当前位置的 provider。
        if (root.Provider == "fs" && !root.IsRooted && ctx.CurrentLocation.Provider != "fs")
        {
            root = new ItemPath { Provider = ctx.CurrentLocation.Provider, InternalPath = root.InternalPath };
        }
        else if (!root.IsRooted)
        {
            root = ctx.CurrentLocation.Combine(root.InternalPath);
        }

        var container = ctx.ResolveContainer(root);
        var contentProvider = ctx.Providers.ResolveCapability<IContentProvider>(root);

        if (contentProvider is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.CapabilityNotSupported,
                Message = $"Provider '{root.Provider}' does not support reading content.",
                TargetPath = root,
                Operation = "search-content",
                Phase = ErrorPhase.ProviderResolution,
            });
            yield break;
        }

        var enumOpts = new EnumerationOptions
        {
            Recurse = true,
            Filter = args.Include,
            IncludeHidden = false,
            IncludeSystem = false,
        };

        // 进度报告 (Per ADR-0030 §5: 进度更新)。
        var progress = ctx.Host.Progress;
        progress.Report(new OperationProgress(0, null, "scanning", false));

        // 并发扫描: Per ADR-0030 §5 (默认 4 线程)。
        // 文件枚举是 IAsyncEnumerable, Parallel.ForEachAsync 接受该类型并并发处理。
        // 结果通过 unbounded Channel 流式回传, 保持 IAsyncEnumerable 语义。
        var channel = Channel.CreateUnbounded<IItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false, // 多个并发 worker 写入
        });

        int scannedCount = 0;
        long lastReportTicks = 0;

        var producerTask = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    container.GetChildrenAsync(root, enumOpts, ct),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MaxConcurrentScans,
                        CancellationToken = ct,
                    },
                    async (file, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (file.Kind != ItemKind.File) return;

                        // 大文件不全加载 (per ADR-0030 §5: 限制 10MB)。
                        if (file.Size is { } size && size > MaxFileSize) return;

                        var matches = await GrepFileAsync(contentProvider, file, args.Pattern, token).ConfigureAwait(false);

                        // 进度报告 (节流 50ms, Per ADR-0044 §10): 用 Interlocked 保证线程安全。
                        var count = Interlocked.Increment(ref scannedCount);
                        var now = DateTime.UtcNow.Ticks;
                        var last = Interlocked.Read(ref lastReportTicks);
                        if (now - last > ProgressThrottleTicks &&
                            Interlocked.CompareExchange(ref lastReportTicks, now, last) == last)
                        {
                            progress.Report(new OperationProgress(count, null, $"scanning: {file.Name}", false));
                        }

                        if (matches.Count > 0)
                        {
                            await channel.Writer.WriteAsync(
                                new SearchResultItem(file, score: 1.0, matchedLines: matches),
                                token).ConfigureAwait(false);
                        }
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 取消是预期行为, 不向 channel 写入错误。
                progress.Report(new OperationProgress(scannedCount, null, "cancelled", true));
                channel.Writer.TryComplete();
                return;
            }
            catch (Exception ex)
            {
                // 生产者异常通过 channel 传播给消费者。
                channel.Writer.Complete(ex);
                return;
            }

            // 完成: 报告最终进度 (Per ADR-0030 §5)。
            progress.Report(new OperationProgress(scannedCount, scannedCount, "done", true));
            channel.Writer.TryComplete();
        }, ct);

        // 流式输出结果。若消费者提前退出, ct 取消会传播到生产者。
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            // 确保 producerTask 在消费者提前退出时也终止 (通过 ct 取消)。
            await producerTask.ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<MatchedLine>> GrepFileAsync(
        IContentProvider content, IItem file, string pattern, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pattern))
            return Array.Empty<MatchedLine>();

        await using var stream = await content.OpenReadAsync(file.Path, ct).ConfigureAwait(false);

        // 二进制检测: 前 8KB 含 \0 跳过 (per ADR-0030 §5)。
        var probe = new byte[BinaryCheckBytes];
        int probeRead = 0;
        while (probeRead < BinaryCheckBytes)
        {
            var n = await stream.ReadAsync(probe.AsMemory(probeRead, BinaryCheckBytes - probeRead), ct).ConfigureAwait(false);
            if (n == 0) break;
            probeRead += n;
        }

        for (int i = 0; i < probeRead; i++)
        {
            if (probe[i] == 0)
                return Array.Empty<MatchedLine>();
        }

        // 构造读取流: seekable 直接 reset, 否则用 ConcatStream 拼接预读字节。
        Stream readStream;
        if (stream.CanSeek)
        {
            stream.Position = 0;
            readStream = stream;
        }
        else
        {
            var prefix = new MemoryStream(probe, 0, probeRead, writable: false);
            readStream = new ConcatStream(prefix, stream);
        }

        using var reader = new StreamReader(readStream);
        var matches = new List<MatchedLine>();
        var lineNo = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            lineNo++;
            if (line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                matches.Add(new MatchedLine(lineNo, line));
        }

        return matches;
    }
}
