using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Clear-Content</c> 命令：清空文件内容。Per ADR-0048 §5.4.
/// <para>
/// 截断文件为 0 字节，保留文件 / inode / 元数据（与 <c>Remove-Item</c> 区别）。
/// </para>
/// <para>
/// <see cref="SupportsShouldProcessAttribute"/>：文件修改 Medium impact。
/// </para>
/// </summary>
[Verb("Clear", Noun = "Content", Aliases = ["clc"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Medium)]
[Description("Clears the content of a file, keeping the file itself.")]
public sealed class ClearContentCommand : ICommand<ClearContentCommand.Args>
{
    /// <summary>Arguments for <c>Clear-Content</c>.</summary>
    /// <param name="Path">目标文件路径。</param>
    /// <param name="Force">覆盖只读文件。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter] bool Force = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = ResolvePath(args.Path, ctx);

        // ShouldProcess 确认（Medium impact）。
        if (!ctx.ShouldProcess(path.Display, "Clear content", ConfirmImpact.Medium))
            yield break;

        if (path.Provider != "fs")
        {
            // 非 fs provider：尝试 IContentWriterProvider（写入空流）。
            var writer = ctx.Providers.ResolveCapability<IContentWriterProvider>(path);
            if (writer is null)
            {
                ctx.Errors?.Write(new ErrorRecord
                {
                    Category = ErrorCategory.CapabilityNotSupported,
                    Message = $"Provider '{path.Provider}' does not support writing content.",
                    TargetPath = path,
                    Operation = "clear-content",
                    Phase = ErrorPhase.ProviderResolution,
                });
                yield break;
            }

            await using var stream = await writer.OpenWriteAsync(path, ct).ConfigureAwait(false);
            stream.SetLength(0);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var fsPath = path.InternalPath;

            if (!File.Exists(fsPath))
            {
                ctx.Errors?.Write(new ErrorRecord
                {
                    Category = ErrorCategory.ItemNotFound,
                    Message = $"File '{fsPath}' not found.",
                    TargetPath = path,
                    Operation = "clear-content",
                    Phase = ErrorPhase.ProviderResolution,
                });
                yield break;
            }

            // -Force 覆盖只读。
            if (args.Force)
            {
                var attrs = File.GetAttributes(fsPath);
                if ((attrs & System.IO.FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(fsPath, attrs & ~System.IO.FileAttributes.ReadOnly);
            }

            // 截断为 0 字节。
            await using var stream = new FileStream(fsPath, FileMode.Truncate, FileAccess.Write, FileShare.None);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Cleared content of {path.Display}.", ct).ConfigureAwait(false);

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
