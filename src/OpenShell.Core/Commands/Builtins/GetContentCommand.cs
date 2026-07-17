using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-Content</c> command. Per ADR-0023 M1. Reads text content
/// line by line from the provider's <see cref="IContentProvider"/> and writes
/// each line to the host output. Supports <c>-Total</c> (first N lines) and
/// <c>-Tail</c> (last N lines) filtering.
/// </summary>
[Verb("Get", Noun = "Content", Aliases = ["cat", "gc", "type"])]
[Description("Reads text content from an item, line by line.")]
public sealed class GetContentCommand : ICommand<GetContentCommand.Args>, OpenShell.Pipeline.IPipelineSource
{
    /// <summary>Arguments for <c>Get-Content</c>.</summary>
    /// <param name="Path">Path of the item to read.</param>
    /// <param name="TotalCount">Number of leading lines to read. Null reads all.</param>
    /// <param name="Tail">Number of trailing lines to read. Null reads all.</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] ItemPath Path,
        [property: Parameter(Aliases = new[] { "-Total" })] int? TotalCount = null,
        [property: Parameter(Aliases = new[] { "-Tail" })] int? Tail = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (args.TotalCount is not null && args.Tail is not null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "-Total and -Tail are mutually exclusive.",
                Operation = "get-content",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        var path = ResolvePath(args.Path, ctx);

        var contentProvider = ctx.Providers.ResolveCapability<IContentProvider>(path);
        if (contentProvider is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.CapabilityNotSupported,
                Message = $"Provider '{path.Provider}' does not support reading content.",
                TargetPath = path,
                Operation = "get-content",
                Phase = ErrorPhase.ProviderResolution,
            });
            yield break;
        }

        await using var stream = await contentProvider.OpenReadAsync(path, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        if (args.TotalCount is { } total)
        {
            var emitted = 0;
            while (emitted < total)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                await ctx.Host.WriteOutputLineAsync(line, ct).ConfigureAwait(false);
                emitted++;
            }
        }
        else if (args.Tail is { } tail)
        {
            var buffer = new Queue<string>(tail + 1);
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                buffer.Enqueue(line);
                if (buffer.Count > tail) buffer.Dequeue();
            }

            foreach (var l in buffer)
            {
                await ctx.Host.WriteOutputLineAsync(l, ct).ConfigureAwait(false);
            }
        }
        else
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                await ctx.Host.WriteOutputLineAsync(line, ct).ConfigureAwait(false);
            }
        }

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
