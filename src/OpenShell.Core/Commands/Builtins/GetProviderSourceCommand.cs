using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Packaging.Registry;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-ProviderSource</c> command. Per ADR-0039 §5 / §3 / §11.
/// 列出所有已注册的 Provider 注册源 (名称/URL/优先级/trusted/auth)。
/// <c>-RefreshIndices</c> 开关强制刷新每个源的包索引到磁盘缓存 (跳过 ETag/Last-Modified 条件请求),
/// 用于主机升级或源切换后获取最新包列表。Per ADR-0039 §11.
/// </summary>
[Verb("Get", Noun = "ProviderSource", Aliases = ["gpsrc"])]
[Description("Lists registered provider sources. Use -RefreshIndices to force-refresh package indices.")]
public sealed class GetProviderSourceCommand : ICommand<GetProviderSourceCommand.Args>
{
    /// <summary>Arguments for <c>Get-ProviderSource</c>.</summary>
    public record Args
    {
        /// <summary>可选源名过滤 (大小写不敏感)。</summary>
        [Parameter(Position = 0)]
        public string? Name { get; init; }

        /// <summary>
        /// 强制刷新包索引到磁盘缓存。Per ADR-0039 §11.
        /// 跳过 ETag/Last-Modified 条件请求, 始终发起无条件 GET 并写回
        /// <c>~/.openshell/cache/indices/</c>。用于确保后续 ListPackages 命中最新数据。
        /// </summary>
        [Parameter(Aliases = ["refresh-indices", "refresh"])]
        public bool RefreshIndices { get; init; }
    }

    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var registry = ctx.Host.Services.GetService(typeof(ProviderSourceRegistry)) as ProviderSourceRegistry;
        if (registry is null)
        {
            await ctx.Host.WriteOutputLineAsync("[get-providersource] ProviderSourceRegistry not registered.", ct);
            yield break;
        }

        // ADR-0039 §11: -RefreshIndices 时解析 RegistryClient 并对每个 (或指定) 源强制刷新索引。
        RegistryClient? client = null;
        if (args.RefreshIndices)
        {
            client = ctx.Host.Services.GetService(typeof(RegistryClient)) as RegistryClient;
            if (client is null)
            {
                await ctx.Host.WriteOutputLineAsync("[get-providersource] RegistryClient not registered; cannot refresh indices.", ct);
                yield break;
            }
        }

        foreach (var s in registry.Sources)
        {
            if (!string.IsNullOrEmpty(args.Name) &&
                !string.Equals(s.Name, args.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int? packageCount = null;
            if (args.RefreshIndices && client is not null)
            {
                try
                {
                    var packages = await client.RefreshIndexAsync(s, ct).ConfigureAwait(false);
                    packageCount = packages.Count;
                }
                catch (Exception)
                {
                    // 刷新失败不阻断命令: 仍输出源信息, 但 PackageCount 留空。
                    packageCount = null;
                }
            }

            var props = PropertyBag.Empty
                .With("Name", s.Name)
                .With("Url", s.Url)
                .With("Priority", s.Priority)
                .With("Trusted", s.Trusted)
                .With("Auth", s.Auth ?? string.Empty);
            if (packageCount is int count)
            {
                props = props.With("PackageCount", count);
            }

            yield return new Item
            {
                Path = new ItemPath { Provider = "src", InternalPath = "/" + s.Name },
                Kind = ItemKind.Unknown,
                Properties = props,
            };
        }
    }
}
