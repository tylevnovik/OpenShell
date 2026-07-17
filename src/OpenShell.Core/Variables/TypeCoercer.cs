using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using OpenShell.Parsing;
using OpenShell.Parsing.Ast;
using OpenShell.Runtime;

namespace OpenShell.Variables;

/// <summary>
/// 类型强制转换工具。Per ADR-0047 §3 + §11.2.
/// 实现字符串→int / int→bool / array→string[] 等转换规则表,
/// 全部使用 InvariantCulture 保证跨区域设置行为一致。
/// 失败抛 InvalidCastException (含源类型与目标类型信息)。
/// <para>
/// Per ADR-0047 §11.2: 内置 <see cref="ConverterCache"/> 缓存常见 (Source, Target) 转换器委托,
/// 命中后跳过 switch 分发直接调用委托, 命中率 > 99% 后转换耗时 &lt; 0.5μs。
/// 热路径 (1..1000 | % { [int]$_ }) 通过 <see cref="CoerceCached"/> 苿路。
/// </para>
/// </summary>
public static class TypeCoercer
{
    /// <summary>
    /// 转换器缓存。Per ADR-0047 §11.2.
    /// 缓存键 (SourceType, TargetType), 缓存值 Func&lt;object?, object?&gt; 委托。
    /// 常见转换预热见静态构造函数。
    /// </summary>
    private static readonly ConcurrentDictionary<(Type Source, Type Target), Func<object?, object?>> ConverterCache = new();

    static TypeCoercer()
    {
        // 预热常见转换 (per §11.2 常见转换预热列表)。
        ConverterCache[(typeof(string), typeof(int))] = v => int.Parse((string)v!, CultureInfo.InvariantCulture);
        ConverterCache[(typeof(string), typeof(long))] = v => long.Parse((string)v!, CultureInfo.InvariantCulture);
        ConverterCache[(typeof(string), typeof(double))] = v => double.Parse((string)v!, CultureInfo.InvariantCulture);
        ConverterCache[(typeof(string), typeof(float))] = v => float.Parse((string)v!, CultureInfo.InvariantCulture);
        ConverterCache[(typeof(string), typeof(decimal))] = v => decimal.Parse((string)v!, CultureInfo.InvariantCulture);
        ConverterCache[(typeof(string), typeof(bool))] = v => ParseBoolString((string)v!);
        ConverterCache[(typeof(string), typeof(char))] = v =>
        {
            var s = (string)v!;
            return s.Length == 1 ? s[0] : (s.Length == 0 ? '\0' : throw new FormatException($"Cannot parse '{s}' as char."));
        };
        ConverterCache[(typeof(int), typeof(bool))] = v => (int)v! != 0;
        ConverterCache[(typeof(long), typeof(bool))] = v => (long)v! != 0;
        ConverterCache[(typeof(double), typeof(bool))] = v => (double)v! != 0.0;
        ConverterCache[(typeof(double), typeof(int))] = v => (int)(double)v!;
        ConverterCache[(typeof(double), typeof(long))] = v => (long)(double)v!;
        ConverterCache[(typeof(int), typeof(long))] = v => (long)(int)v!;
        ConverterCache[(typeof(int), typeof(double))] = v => (double)(int)v!;
        ConverterCache[(typeof(long), typeof(int))] = v => (int)(long)v!;
        ConverterCache[(typeof(bool), typeof(int))] = v => (bool)v! ? 1 : 0;
        ConverterCache[(typeof(bool), typeof(long))] = v => (bool)v! ? 1L : 0L;
        ConverterCache[(typeof(char), typeof(int))] = v => (int)(char)v!;
    }

    /// <summary>
    /// 缓存版本的 <see cref="Coerce(object?, Type)"/>。热路径首选 (per ADR-0047 §11.2)。
    /// 命中缓存时跳过 switch 分发, 直接调用预编译委托。
    /// 未命中时回退到 <see cref="Coerce(object?, Type)"/> 并缓存委托 (常见转换后续命中)。
    /// </summary>
    public static object? CoerceCached(object? value, Type targetType)
    {
        if (value is null || targetType == typeof(object)) return Coerce(value, targetType);
        var sourceType = value.GetType();
        if (targetType.IsAssignableFrom(sourceType)) return value;

        if (ConverterCache.TryGetValue((sourceType, targetType), out var converter))
        {
            try { return converter(value); }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw InvalidCast(sourceType, targetType, value, ex);
            }
        }

        // 未命中: 走完整 Coerce 路径。不缓存 (避免低频转换浪费内存)。
        return Coerce(value, targetType);
    }

    /// <summary>
    /// 把 value 强制转换为 targetType。null → 引用类型返回 null, 值类型抛 InvalidCastException。
    /// </summary>
    /// <param name="value">源值。</param>
    /// <param name="targetType">目标类型 (不能为 null)。</param>
    /// <returns>转换后的值。</returns>
    /// <exception cref="InvalidCastException">转换失败 (含详细信息)。</exception>
    public static object? Coerce(object? value, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        // 透传: object / 同类型.
        if (targetType == typeof(object)) return value;
        if (value is not null && targetType.IsAssignableFrom(value.GetType())) return value;

        // null 处理. Per ADR-0047 §3.1: null → string = "", null → bool = false.
        if (value is null)
        {
            if (targetType == typeof(string)) return "";
            if (targetType == typeof(bool)) return false;
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
            {
                throw new InvalidCastException(
                    $"Cannot convert null to non-nullable value type '{targetType.Name}'.");
            }
            return null;
        }

        var sourceType = value.GetType();

        try
        {
            // bool 优先于数值 (bool→int 等规则).
            if (targetType == typeof(bool)) return CoerceToBool(value);
            if (targetType == typeof(int)) return CoerceToInt(value);
            if (targetType == typeof(long)) return CoerceToLong(value);
            if (targetType == typeof(double)) return CoerceToDouble(value);
            if (targetType == typeof(float)) return CoerceToFloat(value);
            if (targetType == typeof(decimal)) return CoerceToDecimal(value);
            if (targetType == typeof(string)) return CoerceToString(value);
            if (targetType == typeof(char)) return CoerceToChar(value);
            if (targetType == typeof(DateTimeOffset)) return CoerceToDateTimeOffset(value);
            if (targetType == typeof(DateTime)) return CoerceToDateTime(value);

            // 数组转换.
            if (targetType == typeof(string[])) return CoerceToStringArray(value);
            if (targetType == typeof(int[])) return CoerceToIntArray(value);
            if (targetType == typeof(object[])) return CoerceToObjectArray(value);

            // Hashtable.
            if (targetType == typeof(Hashtable)) return CoerceToHashtable(value);

            // Type.
            if (targetType == typeof(Type))
            {
                if (value is Type t) return t;
                if (value is string name) return ResolveTypeAnnotation(name) ?? throw InvalidCast(sourceType, targetType, value);
            }

            // IConvertible 兜底 (短路径).
            if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(targetType))
            {
                return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
            }
        }
        catch (InvalidCastException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw InvalidCast(sourceType, targetType, value, ex);
        }

        throw InvalidCast(sourceType, targetType, value);
    }

    /// <summary>
    /// 解析类型注解字符串为 System.Type。Per ADR-0047 §3 + 任务说明.
    /// 支持 int/long/string/bool/double/float/decimal/datetime/string[]/int[]/object[]/hashtable/scriptblock/PSCustomObject/switch。
    /// </summary>
    /// <param name="annotation">类型注解 (如 "int" / "string[]")。</param>
    /// <returns>对应的 System.Type, 未识别返回 null。</returns>
    public static Type? ResolveTypeAnnotation(string annotation)
    {
        if (string.IsNullOrWhiteSpace(annotation)) return null;
        var trimmed = annotation.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "int" or "integer" => typeof(int),
            "long" => typeof(long),
            "string" or "str" => typeof(string),
            "bool" or "boolean" => typeof(bool),
            "double" => typeof(double),
            "float" or "single" => typeof(float),
            "decimal" => typeof(decimal),
            "datetime" => typeof(DateTimeOffset),
            "string[]" => typeof(string[]),
            "int[]" => typeof(int[]),
            "object[]" => typeof(object[]),
            "hashtable" => typeof(Hashtable),
            // Per ADR-0046 §2: [scriptblock] 类型注解映射到 ScriptBlock CLR 类型。
            "scriptblock" => typeof(ScriptBlock),
            // PSCustomObject 在 OpenShell 中暂用 object（运行时按 PropertyBag 构造动态对象）。
            "pscustomobject" => typeof(object),
            "switch" => typeof(bool),
            "char" => typeof(char),
            "byte" => typeof(byte),
            "sbyte" => typeof(sbyte),
            "short" => typeof(short),
            "ushort" => typeof(ushort),
            "uint" => typeof(uint),
            "ulong" => typeof(ulong),
            "object" => typeof(object),
            "type" => typeof(Type),
            _ => TryResolveTypeByName(trimmed),
        };
    }

    // ------------------------------------------------------------------------
    // ADR-0052: 复合类型注解（Union / Generic / Optional）解析与强制。
    // ------------------------------------------------------------------------

    /// <summary>
    /// 解析类型注解字符串为 <see cref="TypeAnnotation"/> 树。Per ADR-0052 §3.
    /// 支持 int / int? / int|string / List&lt;int&gt; / Dict&lt;string, int&gt; / List&lt;int&gt;? 等组合。
    /// 未识别返回 null。
    /// </summary>
    public static TypeAnnotation? ParseTypeAnnotation(string annotation)
    {
        if (string.IsNullOrWhiteSpace(annotation)) return null;
        var span = SourceSpan.Empty;
        int pos = 0;
        var result = ParseUnionType(annotation, ref pos, span);
        return result;
    }

    /// <summary>按类型注解强制转换值。Per ADR-0052 §3.</summary>
    public static object? Coerce(object? value, TypeAnnotation annotation)
    {
        switch (annotation)
        {
            case PrimitiveTypeAnnotation p:
                var t = ResolveTypeAnnotation(p.Name);
                return t is null ? value : Coerce(value, t);
            case OptionalTypeAnnotation o:
                return value is null ? null : Coerce(value, o.Inner);
            case UnionTypeAnnotation u:
                // 依次尝试每个 Option，第一个成功者返回；全部失败抛 InvalidCastException。
                foreach (var opt in u.Options)
                {
                    try { return Coerce(value, opt); }
                    catch (InvalidCastException) { /* 尝试下一个 */ }
                }
                throw new InvalidCastException(
                    $"Cannot convert value \"{value}\" to union type {FormatAnnotation(annotation)}.");
            case GenericTypeAnnotation g:
                return CoerceGeneric(value, g);
            default:
                return value;
        }
    }

    /// <summary>值是否匹配类型注解（不抛异常）。用于 `is` 运算符与严格模式兼容性检查。Per ADR-0052 §3.</summary>
    public static bool MatchesTypeAnnotation(object? value, TypeAnnotation annotation)
    {
        switch (annotation)
        {
            case PrimitiveTypeAnnotation p:
                if (value is null) return false;
                var t = ResolveTypeAnnotation(p.Name);
                return t is not null && t.IsAssignableFrom(value.GetType());
            case OptionalTypeAnnotation o:
                return value is null || MatchesTypeAnnotation(value, o.Inner);
            case UnionTypeAnnotation u:
                foreach (var opt in u.Options)
                    if (MatchesTypeAnnotation(value, opt)) return true;
                return false;
            case GenericTypeAnnotation g:
                if (value is null) return false;
                var nameLower = g.Name.ToLowerInvariant();
                if (nameLower is "list" or "array" or "seq") return value is IEnumerable;
                if (nameLower is "dict" or "map" or "hashtable") return value is IDictionary;
                var t2 = ResolveTypeAnnotation(g.Name);
                return t2 is not null && t2.IsAssignableFrom(value.GetType());
            default:
                return false;
        }
    }

    /// <summary>格式化类型注解为可读字符串（错误信息用）。</summary>
    public static string FormatAnnotation(TypeAnnotation annotation) => annotation switch
    {
        PrimitiveTypeAnnotation p => p.Name,
        OptionalTypeAnnotation o => FormatAnnotation(o.Inner) + "?",
        UnionTypeAnnotation u => string.Join(" | ", u.Options.Select(FormatAnnotation)),
        GenericTypeAnnotation g => g.Name + "<" + string.Join(", ", g.Args.Select(FormatAnnotation)) + ">",
        _ => annotation.ToString() ?? "?",
    };

    private static object? CoerceGeneric(object? value, GenericTypeAnnotation g)
    {
        if (value is null) return null;
        var nameLower = g.Name.ToLowerInvariant();
        if (nameLower is "list" or "array" or "seq" && g.Args.Count >= 1)
        {
            if (value is IEnumerable e)
            {
                var result = new List<object?>();
                foreach (var item in e) result.Add(Coerce(item, g.Args[0]));
                return result;
            }
            return value;
        }
        if (nameLower is "dict" or "map" or "hashtable" && g.Args.Count >= 2)
        {
            if (value is IDictionary dict)
            {
                var ht = new Hashtable(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry kv in dict)
                    ht[kv.Key ?? ""] = Coerce(kv.Value, g.Args[1]);
                return ht;
            }
            return value;
        }
        // 其他泛型名：尝试按基础类型名解析，失败透传。
        var t = ResolveTypeAnnotation(g.Name);
        return t is not null ? Coerce(value, t) : value;
    }

    // --- 类型注解字符串递归下降解析（union | postfix ? | primary Name<...>） ---

    private static TypeAnnotation? ParseUnionType(string s, ref int pos, SourceSpan span)
    {
        SkipTypeWs(s, ref pos);
        var first = ParsePostfixType(s, ref pos, span);
        if (first is null) return null;
        var options = new List<TypeAnnotation> { first };
        while (true)
        {
            SkipTypeWs(s, ref pos);
            if (pos >= s.Length || s[pos] != '|') break;
            pos++; // 消费 '|'
            var next = ParsePostfixType(s, ref pos, span);
            if (next is null) break;
            options.Add(next);
        }
        return options.Count == 1 ? first : new UnionTypeAnnotation(options, span);
    }

    private static TypeAnnotation? ParsePostfixType(string s, ref int pos, SourceSpan span)
    {
        var prim = ParsePrimaryType(s, ref pos, span);
        if (prim is null) return null;
        SkipTypeWs(s, ref pos);
        if (pos < s.Length && s[pos] == '?')
        {
            pos++; // 消费 '?'
            return new OptionalTypeAnnotation(prim, span);
        }
        return prim;
    }

    private static TypeAnnotation? ParsePrimaryType(string s, ref int pos, SourceSpan span)
    {
        SkipTypeWs(s, ref pos);
        var name = ReadTypeName(s, ref pos);
        if (string.IsNullOrEmpty(name)) return null;
        SkipTypeWs(s, ref pos);
        if (pos < s.Length && s[pos] == '<')
        {
            pos++; // 消费 '<'
            var args = new List<TypeAnnotation>();
            while (true)
            {
                var arg = ParseUnionType(s, ref pos, span);
                if (arg is null) break;
                args.Add(arg);
                SkipTypeWs(s, ref pos);
                if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                break;
            }
            SkipTypeWs(s, ref pos);
            if (pos < s.Length && s[pos] == '>') pos++; // 消费 '>'
            return new GenericTypeAnnotation(name, args, span);
        }
        return new PrimitiveTypeAnnotation(name, span);
    }

    private static string ReadTypeName(string s, ref int pos)
    {
        int start = pos;
        while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_' || s[pos] == '.')) pos++;
        return s.Substring(start, pos - start);
    }

    private static void SkipTypeWs(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    private static Type? TryResolveTypeByName(string name)
    {
        var t = Type.GetType(name, throwOnError: false);
        if (t is not null) return t;
        // 在所有已加载程序集中查找公开类型.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var found = asm.GetType(name);
                if (found is not null) return found;
            }
            catch (Exception)
            {
                // 部分程序集可能加载失败, 跳过.
            }
        }
        return null;
    }

    private static bool CoerceToBool(object value) => value switch
    {
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        short s => s != 0,
        byte b2 => b2 != 0,
        sbyte sb => sb != 0,
        uint u => u != 0,
        ulong ul => ul != 0,
        ushort us => us != 0,
        double d => d != 0.0,
        float f => f != 0.0f,
        decimal dec => dec != 0m,
        char c => c != '\0',
        string s => ParseBoolString(s),
        _ => true, // 非空引用类型 → true.
    };

    private static bool ParseBoolString(string s)
    {
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
        if (bool.TryParse(s, out var b)) return b;
        // 尝试按数值解析.
        if (double.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) return n != 0.0;
        throw new FormatException($"Cannot parse '{s}' as bool.");
    }

    private static int CoerceToInt(object value) => value switch
    {
        int i => i,
        long l => (int)l, // 截断向零 (C# 默认).
        short s => s,
        byte b => b,
        sbyte sb => sb,
        uint u => (int)u,
        ulong ul => (int)ul,
        ushort us => us,
        double d => (int)d, // 截断向零.
        float f => (int)f,
        decimal dec => (int)dec,
        char c => c,
        bool b => b ? 1 : 0,
        string s => int.Parse(s, CultureInfo.InvariantCulture),
        _ => throw InvalidCast(value.GetType(), typeof(int), value),
    };

    private static long CoerceToLong(object value) => value switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        sbyte sb => sb,
        uint u => u,
        ulong ul => (long)ul,
        ushort us => us,
        double d => (long)d,
        float f => (long)f,
        decimal dec => (long)dec,
        char c => c,
        bool b => b ? 1L : 0L,
        string s => long.Parse(s, CultureInfo.InvariantCulture),
        _ => throw InvalidCast(value.GetType(), typeof(long), value),
    };

    private static double CoerceToDouble(object value) => value switch
    {
        double d => d,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        sbyte sb => sb,
        uint u => u,
        ulong ul => ul,
        ushort us => us,
        float f => f,
        decimal dec => (double)dec,
        char c => c,
        bool b => b ? 1.0 : 0.0,
        string s => double.Parse(s, CultureInfo.InvariantCulture),
        _ => throw InvalidCast(value.GetType(), typeof(double), value),
    };

    private static float CoerceToFloat(object value) => value switch
    {
        float f => f,
        int i => i,
        long l => l,
        double d => (float)d,
        decimal dec => (float)dec,
        bool b => b ? 1f : 0f,
        char c => c,
        string s => float.Parse(s, CultureInfo.InvariantCulture),
        _ => throw InvalidCast(value.GetType(), typeof(float), value),
    };

    private static decimal CoerceToDecimal(object value) => value switch
    {
        decimal d => d,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        sbyte sb => sb,
        uint u => u,
        ulong ul => ul,
        ushort us => us,
        double dd => (decimal)dd,
        float f => (decimal)f,
        char c => c,
        bool b => b ? 1m : 0m,
        string s => decimal.Parse(s, CultureInfo.InvariantCulture),
        _ => throw InvalidCast(value.GetType(), typeof(decimal), value),
    };

    private static string CoerceToString(object value) => value switch
    {
        string s => s,
        null => "",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static char CoerceToChar(object value) => value switch
    {
        char c => c,
        int i when i is >= 0 and <= 0xFFFF => (char)i,
        long l when l is >= 0 and <= 0xFFFF => (char)l,
        string s when s.Length == 1 => s[0],
        string s when s.Length == 0 => '\0',
        byte b => (char)b,
        short sh when sh >= 0 => (char)sh,
        _ => throw InvalidCast(value.GetType(), typeof(char), value),
    };

    private static DateTimeOffset CoerceToDateTimeOffset(object value) => value switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
        string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
        _ => throw InvalidCast(value.GetType(), typeof(DateTimeOffset), value),
    };

    private static DateTime CoerceToDateTime(object value) => value switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        string s => DateTime.Parse(s, CultureInfo.InvariantCulture),
        _ => throw InvalidCast(value.GetType(), typeof(DateTime), value),
    };

    private static string[] CoerceToStringArray(object value)
    {
        if (value is string[] arr) return arr;
        if (value is string s) return new[] { s };
        if (value is IEnumerable enumerable)
        {
            var list = new List<string>();
            foreach (var item in enumerable)
            {
                list.Add(item?.ToString() ?? "");
            }
            return list.ToArray();
        }
        return new[] { value.ToString() ?? "" };
    }

    private static int[] CoerceToIntArray(object value)
    {
        if (value is int[] arr) return arr;
        if (value is IEnumerable enumerable)
        {
            var list = new List<int>();
            foreach (var item in enumerable)
            {
                list.Add(CoerceToInt(item!));
            }
            return list.ToArray();
        }
        return new[] { CoerceToInt(value) };
    }

    private static object[] CoerceToObjectArray(object value)
    {
        if (value is object[] arr) return arr;
        if (value is Array array)
        {
            var result = new object[array.Length];
            for (var i = 0; i < array.Length; i++)
            {
                result[i] = array.GetValue(i)!;
            }
            return result;
        }
        if (value is IEnumerable enumerable)
        {
            return enumerable.OfType<object>().ToArray();
        }
        return new[] { value };
    }

    private static Hashtable CoerceToHashtable(object value)
    {
        if (value is Hashtable ht) return ht;
        if (value is IDictionary dict)
        {
            var result = new Hashtable(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry kv in dict)
            {
                result[kv.Key?.ToString() ?? ""] = kv.Value;
            }
            return result;
        }
        throw InvalidCast(value.GetType(), typeof(Hashtable), value);
    }

    private static InvalidCastException InvalidCast(Type sourceType, Type targetType, object? value, Exception? inner = null)
    {
        var msg = $"Cannot convert value \"{value}\" ({sourceType.FullName}) to type \"{targetType.FullName}\".";
        return inner is null
            ? new InvalidCastException(msg)
            : new InvalidCastException(msg, inner);
    }
}
