using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Pipeline;
using OpenShell.Providers;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in Get-ChildItem command. Per ADR-0004, declares Verb-Noun + Args + async streaming.
/// Resolves the container provider for the requested path and streams children.
/// </summary>
[Verb("Get", Noun = "ChildItem", Aliases = ["ls", "dir", "gci"])]
[Description("Lists items in a container.")]
public sealed class GetChildItemCommand : ICommand<GetChildItemCommand.Args>, IPipelineSource
{
    public record Args(
        [property: Parameter(Position = 0)] ItemPath? Path = null,
        [property: Parameter(Aliases = new[]{"-f"})] string? Filter = null,
        [property: Parameter(Aliases = new[]{"-r"})] bool Recurse = false);

    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = args.Path ?? ctx.CurrentLocation;

        // 非 fs provider 的路径：不与 fs CurrentLocation 组合（跨 provider 路径不互通）。
        if (path.Provider != "fs" || path.IsRooted)
        {
            // path 已确定，直接使用。
        }
        else if (ctx.CurrentLocation.Provider == "fs")
        {
            // fs 相对路径：在 fs CurrentLocation 下组合。
            path = ctx.CurrentLocation.Combine(path.InternalPath);
        }

        var container = ctx.Providers.ResolveCapability<IContainerProvider>(path)
            ?? throw new InvalidOperationException(
                $"Provider '{path.Provider}' does not support enumeration.");

        var opts = new EnumerationOptions
        {
            Recurse = args.Recurse,
            Filter = args.Filter,
        };

        await foreach (var item in container.GetChildrenAsync(path, opts, ct).ConfigureAwait(false))
            yield return item;
    }
}
