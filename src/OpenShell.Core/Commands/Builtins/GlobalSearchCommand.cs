using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Pipeline;
using OpenShell.Preview;
using OpenShell.Providers;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Search-Global</c> 命令。Per ADR-0030 §6.
/// 全局搜索 (Ctrl+Shift+F): 文件名 + 内容组合查询。
/// 优先用长期索引 (<see cref="FileIndexStore"/>) 实现快速全文匹配 (per ADR-0030 §8);
/// 索引未注入时回退到 provider 实时枚举 + 文件名子串匹配 (per ADR-0030 §4 简单匹配)。
/// 结果聚合为 <see cref="SearchResultItem"/>, 双击跳转 (per ADR-0030 §6)。
/// </summary>
[Verb("Search", Noun = "Global", Aliases = ["search-global", "gsearch"])]
[Description("Global search (Ctrl+Shift+F): searches file names (and contents) across the index.")]
public sealed class GlobalSearchCommand : ICommand<GlobalSearchCommand.Args>, IPipelineSource
{
    /// <summary>Arguments for <c>Search-Global</c>.</summary>
    /// <param name="Query">搜索查询 (文件名子串 / FTS5 全文, 必填)。</param>
    /// <param name="Path">搜索范围根路径 (默认当前位置)。</param>
    /// <param name="IncludeContents">是否同时搜索文件内容 (默认 false, 仅文件名; true 时调用 <see cref="IContentProvider"/> 流式 grep)。</param>
    /// <param name="MaxResults">最大结果数 (默认 1000)。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Query = "",
        [property: Parameter(Aliases = new[] { "-Path" })] ItemPath? Path = null,
        [property: Parameter(Aliases = new[] { "-Contents" })] bool IncludeContents = false,
        [property: Parameter(Aliases = new[] { "-Top" })] int MaxResults = 1000);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(args.Query))
            yield break;

        var root = args.Path ?? ctx.CurrentLocation;
        // 解析裸路径: 继承当前位置的 provider (与 GetChildItemCommand / SearchContentCommand 同款)。
        if (root.Provider != "fs" || !root.IsRooted)
        {
            if (ctx.CurrentLocation.Provider != "fs")
            {
                root = new ItemPath { Provider = ctx.CurrentLocation.Provider, InternalPath = root.InternalPath };
            }
            else if (!root.IsRooted)
            {
                root = ctx.CurrentLocation.Combine(root.InternalPath);
            }
        }

        // 1. 优先用长期索引 (per ADR-0030 §6 + §8: 全局搜索 + 索引)。
        var indexStore = ctx.Host.Services.GetService(typeof(FileIndexStore)) as FileIndexStore;
        if (indexStore is not null)
        {
            var ftsQuery = ToFts5Query(args.Query);
            var rows = indexStore.SearchByName(ftsQuery, limit: args.MaxResults);
            var emitted = 0;
            foreach (var row in rows)
            {
                if (emitted >= args.MaxResults) yield break;
                ct.ThrowIfCancellationRequested();
                yield return ToSearchResultItem(row.Path, row.Name, row.Size, row.Modified, score: 1.0);
                emitted++;
            }
            yield break;
        }

        // 2. 回退: provider 实时枚举 + 文件名子串匹配。
        var container = ctx.Providers.ResolveCapability<IContainerProvider>(root);
        if (container is null) yield break;

        IContentProvider? contentProvider = null;
        if (args.IncludeContents)
        {
            contentProvider = ctx.Providers.ResolveCapability<IContentProvider>(root);
        }

        var enumOpts = new EnumerationOptions
        {
            Recurse = true,
            IncludeHidden = false,
            IncludeSystem = false,
        };

        var emittedFallback = 0;
        await foreach (var item in container.GetChildrenAsync(root, enumOpts, ct).ConfigureAwait(false))
        {
            if (emittedFallback >= args.MaxResults) yield break;
            ct.ThrowIfCancellationRequested();

            if (item.Kind != ItemKind.File) continue;

            var nameMatch = item.Name.IndexOf(args.Query, StringComparison.OrdinalIgnoreCase) >= 0;
            IReadOnlyList<MatchedLine>? contentMatches = null;

            if (!nameMatch && contentProvider is not null && args.IncludeContents)
            {
                contentMatches = await GrepFileAsync(contentProvider, item, args.Query, ct).ConfigureAwait(false);
                if (contentMatches.Count == 0) continue;
            }
            else if (!nameMatch)
            {
                continue;
            }

            emittedFallback++;
            yield return new SearchResultItem(item, score: nameMatch ? 1.0 : 0.5, matchedLines: contentMatches);
        }
    }

    /// <summary>将用户查询转换为 FTS5 query: 用 * 后缀启前缀匹配; 单引号包裹避免特殊字符注入。</summary>
    private static string ToFts5Query(string query)
    {
        // 转义 FTS5 特殊字符 (per FTS5 spec: 双引号转义为 "" ).
        var escaped = query.Replace("\"", "\"\"");
        // 前缀匹配 (per ADR-0030 §4: 简单子串)。
        return $"\"{escaped}\"*";
    }

    /// <summary>构造 <see cref="SearchResultItem"/> (索引命中, 不持有 IItem)。</summary>
    private static IItem ToSearchResultItem(string path, string name, long size, long modified, double score)
    {
        var itemPath = new ItemPath
        {
            Provider = "fs",
            InternalPath = path.Replace('\\', '/'),
        };
        var inner = new Item
        {
            Path = itemPath,
            Kind = ItemKind.File,
            Size = size,
            Timestamps = new ItemTimestamps(null, new DateTimeOffset(modified, TimeSpan.Zero), null),
        };
        return new SearchResultItem(inner, score: score);
    }

    /// <summary>轻量 grep (与 <see cref="SearchContentCommand"/> 相同逻辑的简化版)。</summary>
    private static async Task<IReadOnlyList<MatchedLine>> GrepFileAsync(
        IContentProvider content, IItem file, string pattern, CancellationToken ct)
    {
        await using var stream = await content.OpenReadAsync(file.Path, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var matches = new List<MatchedLine>();
        var lineNo = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            lineNo++;
            if (line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                matches.Add(new MatchedLine(lineNo, line));
                if (matches.Count >= 20) break; // 限制每文件最多 20 行匹配
            }
        }
        return matches;
    }
}
