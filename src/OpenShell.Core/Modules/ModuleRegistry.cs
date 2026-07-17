#nullable enable
// ADR-0056 ESM 风格模块注册表。
// 设计：
//   1. 缓存已加载的脚本模块（文件绝对路径 → ModuleObject）。
//   2. ModuleObject 持有导出函数 / 常量 / 默认导出。
//   3. 首次 import 加载并缓存；后续 import 返回缓存实例。
//   4. Get-Module / Remove-Module 命令通过 IServiceProvider 查询此注册表，
//      与 IPluginLoader（ADR-0016 插件模块）并列展示。

using System.Collections.Concurrent;

namespace OpenShell.Modules;

/// <summary>
/// 脚本模块注册表。Per ADR-0056 §3.
/// <para>
/// 维护 .osh 脚本模块的加载缓存。每个文件按绝对路径去重，首次 import 触发解析+求值，
/// 后续 import 命中缓存。Remove-Module 可显式移除缓存项以触发重新加载。
/// </para>
/// </summary>
public sealed class ModuleRegistry
{
    private readonly ConcurrentDictionary<string, ModuleObject> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按文件绝对路径查找已缓存模块。未找到返回 false。</summary>
    public bool TryGet(string absolutePath, out ModuleObject? module)
        => _cache.TryGetValue(absolutePath, out module);

    /// <summary>注册或覆盖模块缓存项。Per ADR-0056 §3.</summary>
    public void Register(ModuleObject module)
        => _cache[NormalizePath(module.FilePath)] = module;

    /// <summary>移除模块缓存项。返回是否实际移除。</summary>
    public bool Remove(string absolutePath)
        => _cache.TryRemove(NormalizePath(absolutePath), out _);

    /// <summary>当前已加载的所有脚本模块（快照）。</summary>
    public IReadOnlyCollection<ModuleObject> Loaded => _cache.Values.ToArray();

    /// <summary>清空所有缓存项。</summary>
    public void Clear() => _cache.Clear();

    private static string NormalizePath(string path) =>
        System.IO.Path.GetFullPath(path);
}

/// <summary>
/// 已加载的脚本模块对象。Per ADR-0056 §3.
/// <para>
/// 持有模块文件路径、导出函数表（name → ScriptBlock）、导出常量表（name → value）、
/// 以及可选的默认导出值。由 Evaluator 在求值 export 声明时填充。
/// </para>
/// </summary>
public sealed record ModuleObject
{
    /// <summary>模块名（默认为文件名不含后缀）。</summary>
    public required string Name { get; init; }

    /// <summary>模块文件绝对路径（缓存键）。</summary>
    public required string FilePath { get; init; }

    /// <summary>导出函数表：name → ScriptBlock（可调用）。</summary>
    public IReadOnlyDictionary<string, object?> ExportedFunctions { get; init; }
        = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>导出常量表：name → value。</summary>
    public IReadOnlyDictionary<string, object?> ExportedConstants { get; init; }
        = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>默认导出值（export default expr）。无则为 null。</summary>
    public object? DefaultExport { get; init; }

    /// <summary>加载时间戳。</summary>
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;
}
