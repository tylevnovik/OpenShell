using OpenShell.Providers;

namespace OpenShell.Packaging.Installation;

/// <summary>
/// Provider 依赖解析器。Per ADR-0039 §7.
/// 实现两个职责:
/// <list type="bullet">
///   <item>拓扑排序: 把多个 Provider manifest 的依赖图排成线性加载顺序 (被依赖者在前)。</item>
///   <item>版本范围解析: 简化版 NuGet 版本范围 (>= / [a,b) 等) 与已安装版本的匹配。</item>
/// </list>
/// 不引入 NuGet.Versioning 依赖, 自实现简化版语义。
/// </summary>
public sealed class DependencyResolver
{
    /// <summary>
    /// 对给定 manifest 集合做拓扑排序。Per ADR-0039 §7.
    /// 仅考虑 provider kind 依赖; external 依赖不参与排序 (运行时由 NuGet 缓存解析)。
    /// 检测到循环依赖时抛 <see cref="OspPackageException"/>。
    /// </summary>
    /// <param name="manifests">待排序的 manifest 集合 (含已安装与新装)。</param>
    /// <returns>拓扑序后的 manifest 列表 (被依赖者在前)。</returns>
    public IReadOnlyList<ProviderManifest> TopologicalSort(IEnumerable<ProviderManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        var list = manifests.ToList();
        var byName = list.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=未访问, 1=访问中, 2=已完成
        var result = new List<ProviderManifest>(list.Count);

        void Visit(ProviderManifest m, Stack<string> path)
        {
            if (visited.TryGetValue(m.Name, out var st))
            {
                if (st == 1)
                {
                    var cycle = string.Join(" -> ", path.Reverse().Append(m.Name));
                    throw new OspPackageException($"Circular dependency detected: {cycle}.");
                }
                return;
            }
            visited[m.Name] = 1;
            path.Push(m.Name);
            foreach (var dep in m.Dependencies.Where(d => string.Equals(d.Kind, "provider", StringComparison.OrdinalIgnoreCase)))
            {
                if (byName.TryGetValue(dep.Name, out var depM))
                {
                    Visit(depM, path);
                }
            }
            path.Pop();
            visited[m.Name] = 2;
            result.Add(m);
        }

        foreach (var m in list)
        {
            Visit(m, new Stack<string>());
        }
        return result;
    }

    /// <summary>
    /// 解析单个 manifest 的所有依赖 (含 provider + external)。Per ADR-0039 §7 / §2.
    /// 对每个 provider 依赖, 标注是否已被已安装版本满足 (Satisfied)。
    /// </summary>
    /// <param name="manifest">要安装的包的清单。</param>
    /// <param name="installedProviders">已安装的 Provider 名 → 版本映射。</param>
    /// <returns>按 manifest.Dependencies 顺序的解析结果。</returns>
    public IReadOnlyList<ResolvedDependency> Resolve(
        ProviderManifest manifest,
        IReadOnlyDictionary<string, string> installedProviders)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(installedProviders);

        var result = new List<ResolvedDependency>(manifest.Dependencies.Count);
        foreach (var dep in manifest.Dependencies)
        {
            var satisfied = false;
            var resolved = (string?)null;
            if (string.Equals(dep.Kind, "provider", StringComparison.OrdinalIgnoreCase)
                && installedProviders.TryGetValue(dep.Name, out var installedVer))
            {
                if (IsSatisfied(installedVer, dep.Version))
                {
                    satisfied = true;
                    resolved = installedVer;
                }
            }
            result.Add(new ResolvedDependency
            {
                Name = dep.Name,
                RequestedVersion = dep.Version,
                ResolvedVersion = resolved,
                Kind = dep.Kind,
                Satisfied = satisfied,
            });
        }
        return result;
    }

    /// <summary>
    /// 检查给定具体版本是否满足版本范围。Per ADR-0039 §7 / §2.
    /// 支持的语法 (简化版 NuGet):
    /// <list type="bullet">
    ///   <item><c>"1.2.0"</c> — 精确匹配</item>
    ///   <item><c>"&gt;= 1.0.0"</c> — 大于等于</item>
    ///   <item><c>"&gt; 1.0.0"</c> — 大于</item>
    ///   <item><c>"&gt;= 1.0.0 &lt; 2.0.0"</c> — 区间 (AND)</item>
    ///   <item><c>"[1.0,2.0)"</c> — NuGet 区间语法: [ 含, ) 不含</item>
    ///   <item><c>"*" / 空字符串</c> — 任意</item>
    /// </list>
    /// 不支持的语法视为满足 (宽容策略, 避免过度拒绝)。
    /// </summary>
    public static bool IsSatisfied(string concreteVersion, string range)
    {
        if (string.IsNullOrWhiteSpace(range) || range.Trim() == "*") return true;
        if (!TryNormalizeVersion(concreteVersion, out var v)) return false;
        range = range.Trim();

        // NuGet 区间: [1.0,2.0) / [1.0,2.0] / (1.0,2.0) / (1.0,2.0]
        if (range.StartsWith('[') || range.StartsWith('('))
        {
            return IsIntervalSatisfied(range, v);
        }

        // 多条件 AND 形式: ">= 1.0.0 < 2.0.0"
        // 把每个 "operator version" 对作为一个条件。
        var conditions = ParseConditions(range);
        foreach (var cond in conditions)
        {
            if (!IsSingleSatisfied(cond, v)) return false;
        }
        return true;
    }

    /// <summary>把 "&gt;= 1.0.0 &lt; 2.0.0" 拆成 ["&gt;= 1.0.0", "&lt; 2.0.0"] 条件列表。</summary>
    private static List<string> ParseConditions(string range)
    {
        var result = new List<string>();
        // 匹配 operator (>=, <=, >, <, ==, =) 后跟可选空格 + 版本号。
        var regex = new System.Text.RegularExpressions.Regex(
            @"(>=|<=|==|>|<|=)\s*([0-9][^\s\[\](),]*)",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var matches = regex.Matches(range);
        if (matches.Count == 0)
        {
            // 无 operator → 整个 range 是一个精确版本号。
            result.Add(range.Trim());
            return result;
        }
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            result.Add(m.Groups[1].Value + m.Groups[2].Value.Trim());
        }
        return result;
    }

    private static bool IsIntervalSatisfied(string range, Version v)
    {
        // 简化解析: [low,high) 形式。
        var inner = range.TrimStart('[', '(').TrimEnd(']', ')');
        var inclusiveLow = range.StartsWith('[');
        var inclusiveHigh = range.EndsWith(']');
        var parts = inner.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            if (!TryNormalizeVersion(parts[0].Trim(), out var low)) return true;
            if (!TryNormalizeVersion(parts[1].Trim(), out var high)) return true;
            var okLow = inclusiveLow ? v.CompareTo(low) >= 0 : v.CompareTo(low) > 0;
            var okHigh = inclusiveHigh ? v.CompareTo(high) <= 0 : v.CompareTo(high) < 0;
            return okLow && okHigh;
        }
        if (parts.Length == 1)
        {
            // [1.0] 精确匹配
            if (!TryNormalizeVersion(parts[0].Trim(), out var exact)) return true;
            return v.CompareTo(exact) == 0;
        }
        return true;
    }

    private static bool IsSingleSatisfied(string token, Version v)
    {
        // token 形如 ">=1.0.0" (operator 已与 version 拼接, 无空格) 或纯版本号 "1.0.0"。
        if (token.StartsWith(">=", StringComparison.Ordinal))
            return TryNormalizeVersion(token[2..].Trim(), out var t) && v.CompareTo(t) >= 0;
        if (token.StartsWith("<=", StringComparison.Ordinal))
            return TryNormalizeVersion(token[3..].Trim(), out var t) && v.CompareTo(t) <= 0;
        if (token.StartsWith("==", StringComparison.Ordinal))
            return TryNormalizeVersion(token[2..].Trim(), out var t) && v.CompareTo(t) == 0;
        if (token.StartsWith(">", StringComparison.Ordinal))
            return TryNormalizeVersion(token[1..].Trim(), out var t) && v.CompareTo(t) > 0;
        if (token.StartsWith("<", StringComparison.Ordinal))
            return TryNormalizeVersion(token[1..].Trim(), out var t) && v.CompareTo(t) < 0;
        if (token.StartsWith("=", StringComparison.Ordinal))
            return TryNormalizeVersion(token[1..].Trim(), out var t) && v.CompareTo(t) == 0;
        // 纯版本号 → 精确匹配
        if (TryNormalizeVersion(token, out var exact)) return v.CompareTo(exact) == 0;
        // 未知语法: 宽容视为满足
        return true;
    }

    /// <summary>
    /// 把版本字符串解析为 4-组件的 <see cref="Version"/> (Major.Minor.Build.Revision)。
    /// 缺省的 Build / Revision 视为 0, 这样 "1.0" 与 "1.0.0" / "1.0.0.0" 比较时相等。
    /// <see cref="Version.CompareTo(Version?)"/> 默认把 -1 (未指定) 当作不同的值, 这会导致 "1.0" 与 "1.0.0" 不相等。
    /// 此处规范化避免该问题。
    /// </summary>
    private static bool TryNormalizeVersion(string text, out Version v)
    {
        if (!Version.TryParse(text, out var parsed))
        {
            v = new Version(0, 0, 0, 0);
            return false;
        }
        v = new Version(
            parsed.Major,
            parsed.Minor,
            parsed.Build == -1 ? 0 : parsed.Build,
            parsed.Revision == -1 ? 0 : parsed.Revision);
        return true;
    }
}
