using System.Runtime.CompilerServices;
using System.Text;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Add-Content</c> 命令：追加内容到文件。Per ADR-0048 §5.3.
/// <para>
/// 以 <c>Append</c> 模式打开文件写入；若文件不存在则创建。
/// <c>-Force</c> 覆盖只读；<c>-NoNewline</c> 不追加末尾换行。
/// </para>
/// <para>
/// <see cref="SupportsShouldProcessAttribute"/>：文件修改 Low impact。
/// </para>
/// </summary>
[Verb("Add", Noun = "Content", Aliases = ["ac"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Low)]
[Description("Appends content to a file.")]
public sealed class AddContentCommand : ICommand<AddContentCommand.Args>
{
    /// <summary>Arguments for <c>Add-Content</c>.</summary>
    /// <param name="Path">目标文件路径。</param>
    /// <param name="Value">要追加的文本内容。</param>
    /// <param name="Force">覆盖只读文件。</param>
    /// <param name="NoNewline">不追加末尾换行。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter(Position = 1, Mandatory = true)] string Value,
        [property: Parameter] bool Force = false,
        [property: Parameter] bool NoNewline = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = ResolvePath(args.Path, ctx);

        // ShouldProcess 确认（Low impact）。
        if (!ctx.ShouldProcess(path.Display, "Add content", ConfirmImpact.Low))
            yield break;

        // 走 fs provider 直接 IO（Append 模式）。
        if (path.Provider != "fs")
        {
            // 非 fs provider：尝试 IContentWriterProvider（但不支持 append，等价 Set）。
            var writer = ctx.Providers.ResolveCapability<IContentWriterProvider>(path);
            if (writer is null)
            {
                ctx.Errors?.Write(new ErrorRecord
                {
                    Category = ErrorCategory.CapabilityNotSupported,
                    Message = $"Provider '{path.Provider}' does not support writing content.",
                    TargetPath = path,
                    Operation = "add-content",
                    Phase = ErrorPhase.ProviderResolution,
                });
                yield break;
            }

            await using var stream = await writer.OpenWriteAsync(path, ct).ConfigureAwait(false);
            await using var sw = new StreamWriter(stream);
            await sw.WriteAsync(args.Value.AsMemory(), ct).ConfigureAwait(false);
            if (!args.NoNewline)
                await sw.WriteLineAsync();
            await sw.FlushAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var fsPath = path.InternalPath;

            // -Force 覆盖只读。
            if (args.Force && File.Exists(fsPath))
            {
                var attrs = File.GetAttributes(fsPath);
                if ((attrs & System.IO.FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(fsPath, attrs & ~System.IO.FileAttributes.ReadOnly);
            }

            await using var stream = new FileStream(fsPath, FileMode.Append, FileAccess.Write, FileShare.None);
            await using var sw = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };
            await sw.WriteAsync(args.Value.AsMemory(), ct).ConfigureAwait(false);
            if (!args.NoNewline)
                await sw.WriteLineAsync();
            await sw.FlushAsync(ct).ConfigureAwait(false);
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Added {args.Value.Length} chars.", ct).ConfigureAwait(false);

        await Task.CompletedTask;
        yield break;
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
}
