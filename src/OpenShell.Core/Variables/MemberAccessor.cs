using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using OpenShell.Errors;
using OpenShell.Formatting;
using OpenShell.Items;

namespace OpenShell.Variables;

/// <summary>
/// 成员访问反射器。Per ADR-0047 §4.
/// 实现 $var.Property / $var[index] / $var.Method(args) 求值, 按优先级:
/// IItem (ItemValueAccessor) > IDictionary > IList/array > CLR Property > CLR Field.
/// PropertyInfo / MethodInfo 缓存在 ConcurrentDictionary 提升性能。
/// </summary>
public static class MemberAccessor
{
    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo?> PropertyCache = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), FieldInfo?> FieldCache = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), MethodInfo[]> MethodCache = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo?> IndexerCache = new();

    private const BindingFlags InstancePublicIgnoreCase = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

    /// <summary>
    /// 读取属性值 (优先级: IItem > IDictionary > IList/array.Count/Length > CLR Property > CLR Field).
    /// </summary>
    /// <param name="target">目标对象 (null 时抛 MemberNotFoundException).</param>
    /// <param name="name">属性名。</param>
    /// <returns>属性值。</returns>
    /// <exception cref="MemberNotFoundException">属性不存在。</exception>
    /// <exception cref="RuntimeBinderException">target 为 null。</exception>
    public static object? GetProperty(object? target, string name)
    {
        if (target is null)
        {
            throw new RuntimeBinderException(
                $"Cannot get property '{name}' on null target.");
        }

        // 1. IItem: 走 ItemValueAccessor.
        if (target is IItem item)
        {
            return ItemValueAccessor.GetValue(item, name);
        }

        // 2. IDictionary: 按 key 索引 (大小写不敏感).
        if (target is IDictionary dict)
        {
            // 先精确匹配, 再不敏感匹配.
            if (dict.Contains(name)) return dict[name];
            foreach (DictionaryEntry kv in dict)
            {
                if (kv.Key is string key && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value;
                }
            }
            // 对 IDictionary 的 Count / Keys / Values 走 CLR 反射回退.
        }

        // 3. IList / Array: 支持 Count / Length / LongLength.
        if (target is IList or Array)
        {
            if (string.Equals(name, "Count", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Length", StringComparison.OrdinalIgnoreCase))
            {
                return target switch
                {
                    Array arr => arr.LongLength == arr.Length ? arr.Length : (long)arr.LongLength,
                    ICollection col => col.Count,
                    _ => null,
                };
            }
            if (string.Equals(name, "LongLength", StringComparison.OrdinalIgnoreCase) && target is Array a)
            {
                return a.LongLength;
            }
        }

        // 4. CLR Property.
        var prop = LookupProperty(target.GetType(), name);
        if (prop is not null && prop.CanRead)
        {
            return prop.GetValue(target);
        }

        // 5. CLR Field.
        var field = LookupField(target.GetType(), name);
        if (field is not null)
        {
            return field.GetValue(target);
        }

        throw new MemberNotFoundException(target.GetType(), name);
    }

    /// <summary>
    /// 索引访问 $var[index]。优先级: IDictionary > IList/array (含负索引) > 默认索引器.
    /// </summary>
    /// <param name="target">目标对象 (null 时抛 RuntimeBinderException).</param>
    /// <param name="index">索引 (IDictionary 接受任意 key, IList/array 接受 int).</param>
    /// <returns>索引值。</returns>
    /// <exception cref="RuntimeBinderException">target 为 null 或不支持索引。</exception>
    /// <exception cref="IndexOutOfRangeException">数组索引越界。</exception>
    public static object? GetIndex(object? target, object index)
    {
        if (target is null)
        {
            throw new RuntimeBinderException(
                $"Cannot index null target with [{index}].");
        }

        // 1. IDictionary.
        if (target is IDictionary dict)
        {
            // 字符串 key 不敏感匹配.
            if (dict.Contains(index))
            {
                return dict[index];
            }
            if (index is string s)
            {
                foreach (DictionaryEntry kv in dict)
                {
                    if (kv.Key is string key && string.Equals(key, s, StringComparison.OrdinalIgnoreCase))
                    {
                        return kv.Value;
                    }
                }
            }
            throw new KeyNotFoundException($"Key '{index}' not found in dictionary.");
        }

        // 2. IList / Array.
        if (target is IList list)
        {
            var i = CoerceToIntIndex(index);
            // 负索引: 从末尾计数.
            if (i < 0) i += list.Count;
            if (i < 0 || i >= list.Count)
            {
                throw new IndexOutOfRangeException($"Index {index} out of range for list of size {list.Count}.");
            }
            return list[i];
        }

        if (target is Array arr)
        {
            var i = CoerceToIntIndex(index);
            if (i < 0) i += arr.Length;
            if (i < 0 || i >= arr.Length)
            {
                throw new IndexOutOfRangeException($"Index {index} out of range for array of size {arr.Length}.");
            }
            return arr.GetValue(i);
        }

        // 3. 默认索引器 (反射查找 "Item" 属性).
        var indexer = LookupIndexer(target.GetType());
        if (indexer is not null && indexer.GetIndexParameters() is { Length: 1 } p)
        {
            var converted = TypeCoercer.Coerce(index, p[0].ParameterType);
            return indexer.GetValue(target, new[] { converted });
        }

        throw new RuntimeBinderException(
            $"Type '{target.GetType().FullName}' does not support indexing.");
    }

    /// <summary>
    /// 调用方法 $var.Method(args)。按参数数量与类型做重载解析 (简化版).
    /// </summary>
    /// <param name="target">目标对象 (null 时抛 RuntimeBinderException).</param>
    /// <param name="name">方法名。</param>
    /// <param name="args">参数列表。</param>
    /// <returns>方法返回值 (void 方法返回 null).</returns>
    /// <exception cref="MemberNotFoundException">方法不存在。</exception>
    /// <exception cref="RuntimeBinderException">target 为 null 或调用失败。</exception>
    public static object? InvokeMethod(object? target, string name, params object?[] args)
    {
        if (target is null)
        {
            throw new RuntimeBinderException(
                $"Cannot invoke method '{name}' on null target.");
        }

        var methods = LookupMethods(target.GetType(), name);
        if (methods.Length == 0)
        {
            throw new MemberNotFoundException(target.GetType(), name, isMethod: true);
        }

        // 简化重载解析: 先匹配参数数量, 再选第一个能成功转换的.
        var nonNullArgs = args ?? Array.Empty<object?>();
        MethodInfo? matched = null;
        object?[]? convertedArgs = null;
        foreach (var m in methods)
        {
            var p = m.GetParameters();
            if (p.Length != nonNullArgs.Length) continue;

            try
            {
                var converted = new object?[p.Length];
                for (var i = 0; i < p.Length; i++)
                {
                    converted[i] = TypeCoercer.Coerce(nonNullArgs[i], p[i].ParameterType);
                }
                matched = m;
                convertedArgs = converted;
                break;
            }
            catch (InvalidCastException)
            {
                continue;
            }
        }

        if (matched is null)
        {
            throw new MemberNotFoundException(target.GetType(), name, isMethod: true);
        }

        try
        {
            return matched.Invoke(target, convertedArgs);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // 解包 InnerException 透传.
            throw new RuntimeBinderException(
                $"Method '{name}' on '{target.GetType().FullName}' threw: {tie.InnerException.Message}",
                tie.InnerException);
        }
    }

    /// <summary>判断 target 是否含有指定成员 (属性 / 字段 / 方法)。</summary>
    public static bool HasMember(object? target, string name)
    {
        if (target is null) return false;
        var t = target.GetType();
        return LookupProperty(t, name) is not null
            || LookupField(t, name) is not null
            || LookupMethods(t, name).Length > 0;
    }

    private static int CoerceToIntIndex(object index) => index switch
    {
        int i => i,
        long l => (int)l,
        short s => s,
        byte b => b,
        sbyte sb => sb,
        uint u => (int)u,
        ulong ul => (int)ul,
        ushort us => us,
        string s => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new RuntimeBinderException($"Index must be int, got {index?.GetType().FullName}."),
    };

    private static PropertyInfo? LookupProperty(Type type, string name)
        => PropertyCache.GetOrAdd((type, name), static (key, t) =>
            t.GetProperty(key.Name, InstancePublicIgnoreCase)
                ?? t.GetProperty(key.Name, BindingFlags.Public | BindingFlags.Instance),
            type);

    private static FieldInfo? LookupField(Type type, string name)
        => FieldCache.GetOrAdd((type, name), static (key, t) =>
            t.GetField(key.Name, InstancePublicIgnoreCase)
                ?? t.GetField(key.Name, BindingFlags.Public | BindingFlags.Instance),
            type);

    private static MethodInfo[] LookupMethods(Type type, string name)
        => MethodCache.GetOrAdd((type, name), static (key, t) =>
            t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => string.Equals(m.Name, key.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            type);

    private static PropertyInfo? LookupIndexer(Type type)
        => IndexerCache.GetOrAdd((type, "Item"), static (key, t) =>
            t.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance),
            type);
}

/// <summary>
/// 成员不存在时抛出。Per ADR-0047 §4.1 (RuntimeBinderException 等价).
/// </summary>
public sealed class MemberNotFoundException : OpenShellException
{
    /// <summary>目标类型。</summary>
    public Type TargetType { get; }

    /// <summary>成员名。</summary>
    public string MemberName { get; }

    /// <summary>是否为方法 (false 表示属性 / 字段)。</summary>
    public bool IsMethod { get; }

    public MemberNotFoundException(Type targetType, string memberName, bool isMethod = false)
        : base(
            isMethod
                ? $"Method '{memberName}' not found on type '{targetType.FullName}'."
                : $"Property or field '{memberName}' not found on type '{targetType.FullName}'.")
    {
        TargetType = targetType;
        MemberName = memberName;
        IsMethod = isMethod;
    }

    public override ErrorCategory Category => ErrorCategory.ItemNotFound;
}

/// <summary>
/// 运行时绑定失败 (target 为 null / 类型不支持索引 / 方法调用异常)。Per ADR-0047 §4.1.
/// 替代 Microsoft.CSharp.RuntimeBinder.RuntimeBinderException (避免依赖 Microsoft.CSharp 程序集).
/// </summary>
public sealed class RuntimeBinderException : OpenShellException
{
    public RuntimeBinderException(string message) : base(message) { }
    public RuntimeBinderException(string message, Exception innerException) : base(message, innerException) { }

    public override ErrorCategory Category => ErrorCategory.InvalidArgument;
}
