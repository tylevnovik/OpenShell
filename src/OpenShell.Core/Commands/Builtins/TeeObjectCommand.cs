using System.Runtime.CompilerServices;
using System.Text;
using OpenShell.Items;
using OpenShell.Pipeline;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Tee-Object</c> 命令：分流管道到文件 / 变量，同时继续传下游。Per ADR-0048 §1.8.
/// <para>
/// 流式 sink + 透传：既写入目标（文件 / 变量）又 yield 到下游。
/// <c>-FilePath</c> 写入文件（<c>-Append</c> 追加）；<c>-Variable</c> 写入变量。
/// </para>
/// </summary>
[Verb("Tee", Noun = "Object", Aliases = ["tee"], PipelineOnly = true)]
[Description("Splits pipeline output to a file or variable while passing through.")]
public sealed class TeeObjectCommand : IPipelineTransform<TeeObjectCommand.Args>
{
    /// <summary>Arguments for <c>Tee-Object</c>.</summary>
    /// <param name="FilePath">写入文件路径（alias -Path）。</param>
    /// <param name="Variable">写入变量名。</param>
    /// <param name="Append">追加到文件（仅与 -FilePath 共用）。</param>
    public record Args(
        [property: Parameter(Aliases = new[] { "-Path" })] ItemPath? FilePath = null,
        [property: Parameter] string? Variable = null,
        [property: Parameter] bool Append = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        StreamWriter? fileWriter = null;
        FileStream? fileStream = null;
        List<IItem>? varItems = null;

        var useVariable = !string.IsNullOrEmpty(args.Variable);

        if (useVariable)
            varItems = new List<IItem>();

        // 模式匹配取出非空 ItemPath（避免对 Nullable<T>.Value 的访问告警）。
        if (args.FilePath is { } filePath)
        {
            var path = ResolvePath(filePath, ctx);
            if (path.Provider != "fs")
                goto passthrough; // 非 fs provider 走透传（错误已在 host 记录）

            var fsPath = path.InternalPath;
            var mode = args.Append ? FileMode.Append : FileMode.Create;
            fileStream = new FileStream(fsPath, mode, FileAccess.Write, FileShare.None);
            fileWriter = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = false };
        }

        try
        {
            await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                // 写文件。
                if (fileWriter is not null)
                {
                    var line = item.Properties["Value"]?.ToString() ?? item.Name;
                    await fileWriter.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                }

                // 收集到变量。
                varItems?.Add(item);

                // 透传下游。
                yield return item;
            }

            if (fileWriter is not null)
                await fileWriter.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            fileWriter?.Dispose();
            fileStream?.Dispose();
        }

        // 写入变量（Global 作用域，per ADR-0048 §Tee-Object 约束）。
        if (useVariable && varItems is not null && ctx.Variables is not null)
        {
            ctx.Variables.Set(args.Variable!, varItems, scope: OpenShell.Variables.VariableScope.Global);
        }

        await Task.CompletedTask;
        yield break;

    passthrough:
        // 错误路径：非 fs provider，仅透传。
        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    /// <summary>不支持非管道调用。</summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Tee-Object is pipeline-only, use it after |");

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
}
