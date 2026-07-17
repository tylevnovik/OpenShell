using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Packaging.Registry;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Find-Provider</c> command. Per ADR-0039 §5.
/// 在所有已注册的注册源中按关键词搜索 Provider 包, 输出 <see cref="PackageInfo"/> 流。
/// </summary>
[Verb("Find", Noun = "Provider", Aliases = ["fp"])]
[Description("Searches registered provider sources for matching packages.")]
public sealed class FindProviderCommand : ICommand<FindProviderCommand.Args>
{
    /// <summary>Arguments for <c>Find-Provider</c>.</summary>
    public record Args
    {
        /// <summary>搜索关键词 (匹配包名/描述/tags)。必填。</summary>
        [Parameter(Position = 0)]
        public string? Query { get; init; }

        /// <summary>限定搜索的注册源名 (缺省遍历所有源)。</summary>
        [Parameter(Aliases = ["src"])]
        public string? Source { get; init; }
    }

    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(args.Query))
        {
            await ctx.Host.WriteOutputLineAsync("usage: find-provider <query> [-Source <name>]", ct);
            yield break;
        }

        var registry = ctx.Host.Services.GetService(typeof(ProviderSourceRegistry)) as ProviderSourceRegistry;
        var client = ctx.Host.Services.GetService(typeof(RegistryClient)) as RegistryClient;
        if (registry is null || client is null)
        {
            await ctx.Host.WriteOutputLineAsync("[find-provider] packaging services not registered.", ct);
            yield break;
        }

        var sources = registry.Sources;
        if (!string.IsNullOrEmpty(args.Source))
        {
            var s = sources.FirstOrDefault(x => string.Equals(x.Name, args.Source, StringComparison.OrdinalIgnoreCase));
            if (s is null)
            {
                await ctx.Host.WriteOutputLineAsync($"[find-provider] source '{args.Source}' not registered.", ct);
                yield break;
            }
            sources = new[] { s };
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources)
        {
            IReadOnlyList<PackageInfo> results;
            try
            {
                results = await client.SearchAsync(s, args.Query, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ctx.Host.WriteOutputLineAsync($"[find-provider] source '{s.Name}' query failed: {ex.Message}", ct);
                continue;
            }

            foreach (var info in results)
            {
                if (!seen.Add(info.Name)) continue;
                yield return new Item
                {
                    Path = new ItemPath { Provider = "registry", InternalPath = "/" + info.Name },
                    Kind = ItemKind.Unknown,
                    Properties = PropertyBag.Empty
                        .With("Name", info.Name)
                        .With("Latest", info.Latest ?? string.Empty)
                        .With("VersionCount", info.Versions.Count)
                        .With("Description", info.Description ?? string.Empty)
                        .With("Downloads", info.Downloads ?? 0L)
                        .With("Source", s.Name),
                };
            }
        }
    }
}
