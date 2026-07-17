using System.Collections;

namespace OpenShell.Variables;

/// <summary>
/// 哈希表字面量运行时辅助。Per ADR-0047 §6.1.
/// 解析器解析 @{ k = v; k2 = v2 } 后, 用本工具构造大小写不敏感的 System.Collections.Hashtable.
/// 嵌套字面量递归处理 (parser 责任, 这里仅接受已求值的 entries)。
/// </summary>
public static class HashLiteral
{
    /// <summary>
    /// 创建大小写不敏感键的 Hashtable, 键统一 ToString 为 string。
    /// </summary>
    /// <param name="entries">键值对序列。</param>
    /// <returns>大小写不敏感的 Hashtable。</returns>
    public static Hashtable Create(IEnumerable<(string Key, object? Value)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var ht = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            if (key is null) throw new ArgumentException("Hash literal key cannot be null.", nameof(entries));
            ht[key] = value;
        }
        return ht;
    }

    /// <summary>
    /// 创建空哈希表 (大小写不敏感)。
    /// </summary>
    public static Hashtable Empty() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 把任意 IDictionary 转为大小写不敏感的 Hashtable (浅拷贝键值)。
    /// </summary>
    public static Hashtable From(IDictionary source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var ht = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry kv in source)
        {
            ht[kv.Key?.ToString() ?? ""] = kv.Value;
        }
        return ht;
    }
}
