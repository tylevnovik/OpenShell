using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>New-Item</c> command. Per ADR-0023 M1. Creates a new file or
/// directory at the specified path. When <c>Content</c> is supplied for a file,
/// the content is written via the provider's <see cref="IContentWriterProvider"/>.
/// </summary>
[Verb("New", Noun = "Item", Aliases = ["ni", "mkdir", "touch"])]
[Description("Creates a new file or directory.")]
public sealed class NewItemCommand : ICommand<NewItemCommand.Args>
{
    /// <summary>Arguments for <c>New-Item</c>.</summary>
    /// <param name="Path">Path of the item to create.</param>
    /// <param name="Type">Item type: <c>file</c> (default) or <c>directory</c>.</param>
    /// <param name="Content">Optional initial content for a file item.</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter(Position = 1)] string Type = "file",
        [property: Parameter] string? Content = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var engine = ctx.Operations;
        if (engine is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Operation engine is not available in this context.",
                Operation = "new-item",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var path = ResolvePath(args.Path, ctx);
        var type = (args.Type ?? "file").Trim().ToLowerInvariant();

        if (type == "directory")
        {
            var result = await engine.CreateDirectoryAsync(path, null, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                await ctx.Host.WriteOutputLineAsync(
                    $"Created directory: {path.Display}", ct).ConfigureAwait(false);
            }
            else
            {
                WriteOperationErrors(ctx, result, "new-item");
            }
        }
        else if (type == "file")
        {
            if (!string.IsNullOrEmpty(args.Content))
            {
                var writer = ctx.Providers.ResolveCapability<IContentWriterProvider>(path);
                if (writer is null)
                {
                    ctx.Errors?.Write(new ErrorRecord
                    {
                        Category = ErrorCategory.CapabilityNotSupported,
                        Message = $"Provider '{path.Provider}' does not support writing content.",
                        TargetPath = path,
                        Operation = "new-item",
                        Phase = ErrorPhase.ProviderResolution,
                    });
                    yield break;
                }

                await using var stream = await writer.OpenWriteAsync(path, ct).ConfigureAwait(false);
                await using var sw = new StreamWriter(stream);
                await sw.WriteAsync(args.Content.AsMemory(), ct).ConfigureAwait(false);
                await sw.FlushAsync(ct).ConfigureAwait(false);

                await ctx.Host.WriteOutputLineAsync(
                    $"Wrote {args.Content.Length} chars to: {path.Display}", ct).ConfigureAwait(false);
            }
            else
            {
                var result = await engine.TouchAsync(path, null, ct).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    await ctx.Host.WriteOutputLineAsync(
                        $"Created file: {path.Display}", ct).ConfigureAwait(false);
                }
                else
                {
                    WriteOperationErrors(ctx, result, "new-item");
                }
            }
        }
        else
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = $"Unknown item type '{args.Type}'. Expected 'file' or 'directory'.",
                TargetPath = path,
                Operation = "new-item",
                Phase = ErrorPhase.ArgumentBinding,
            });
        }

        yield break;
    }

    private static void WriteOperationErrors(CommandContext ctx, OperationResult result, string operation)
    {
        foreach (var err in result.Errors)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = err.Message,
                TargetPath = err.Path,
                Operation = operation,
                Phase = ErrorPhase.Operation,
                Exception = err.Exception,
            });
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
}
