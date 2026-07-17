using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Pipeline;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Get-Member</c> 命令：反射输入对象的属性 / 方法 / 字段。Per ADR-0048 §4.1.
/// <para>
/// 管道输入：从管道接收 <see cref="IItem"/>，取第一个非 null 项进行反射。
/// 输出 <c>MemberDefinition</c> 风格的 <see cref="IItem"/> 列表，含
/// <c>TypeName</c> / <c>Name</c> / <c>MemberType</c> / <c>Definition</c> 属性。
/// </para>
/// <para>
/// 反射基于 <see cref="IItem"/> 标准字段（Name/Path/Kind/Size）+ <see cref="IItem.Properties"/>
/// 字典 key + Properties 中 CLR 对象值的方法 / 属性反射。
/// </para>
/// </summary>
[Verb("Get", Noun = "Member", Aliases = ["gm"], PipelineOnly = true)]
[Description("Lists the properties and methods of input objects.")]
public sealed class GetMemberCommand : IPipelineTransform<GetMemberCommand.Args>
{
    /// <summary>Arguments for <c>Get-Member</c>.</summary>
    /// <param name="MemberType">过滤类型：<c>Property</c>/<c>Method</c>/<c>Field</c>/<c>All</c>（默认 All）。</param>
    /// <param name="Name">按名称过滤。</param>
    /// <param name="Static">列出静态成员（对 Properties 中 CLR 对象值生效）。</param>
    public record Args(
        [property: Parameter] string? MemberType = null,
        [property: Parameter] string[]? Name = null,
        [property: Parameter] bool Static = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Get-Member 是 buffering 节点：取第一个项决定类型，输出成员列表后忽略后续项（与 PowerShell 一致）。
        IItem? first = null;
        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
        {
            first = item;
            break;
        }

        if (first is null)
            yield break;

        var memberType = ParseMemberType(args.MemberType);
        var nameFilter = args.Name is { Length: > 0 } names
            ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
            : null;
        var typeName = first.Kind.ToString();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        // 1. 标准字段作为 Property 成员。
        foreach (var (propName, clrTypeName) in GetStandardMembers(first))
        {
            if (nameFilter is not null && !nameFilter.Contains(propName))
                continue;
            if (!MatchesMemberType("Property", memberType))
                continue;

            var key = propName + "|Property";
            if (emitted.Add(key))
                yield return MakeMemberItem(typeName, propName, "Property", clrTypeName);
        }

        // 2. Properties 字典中的 key 作为 Property 成员，并对其 CLR 值做方法反射。
        foreach (var key in first.Properties.Values.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (nameFilter is not null && !nameFilter.Contains(key))
                continue;

            var value = first.Properties[key];
            var clrType = value?.GetType();
            var definition = clrType?.Name ?? "Object";

            var memberKey = key + "|Property";
            if (MatchesMemberType("Property", memberType) && emitted.Add(memberKey))
                yield return MakeMemberItem(typeName, key, "Property", definition);

            // 对 CLR 对象值做方法 / 属性反射。
            if (clrType is not null && value is not null)
            {
                foreach (var member in ReflectClrMembers(value, clrType, memberType, nameFilter, args.Static, emitted))
                    yield return member;
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>不支持非管道调用。</summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Get-Member is pipeline-only, use it after |");

    private static string ParseMemberType(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? "All" : raw!;

    private static bool MatchesMemberType(string memberType, string filter)
        => filter.Equals("All", StringComparison.OrdinalIgnoreCase)
           || filter.Equals(memberType, StringComparison.OrdinalIgnoreCase);

    /// <summary>提取 IItem 标准字段作为 (名称, 类型名) 元组。</summary>
    private static IEnumerable<(string Name, string TypeName)> GetStandardMembers(IItem item)
    {
        yield return ("Name", item.Name?.GetType().Name ?? "String");
        yield return ("Path", "String");
        yield return ("Kind", "ItemKind");
        yield return ("Size", "Nullable`1");
        yield return ("ContentType", "String");
    }

    /// <summary>对 CLR 对象值反射其公开属性与方法。</summary>
    private static IEnumerable<IItem> ReflectClrMembers(
        object value, Type type, string memberType, HashSet<string>? nameFilter,
        bool includeStatic, HashSet<string> emitted)
    {
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
        if (includeStatic) flags |= System.Reflection.BindingFlags.Static;
        var typeName = type.Name;

        foreach (var prop in type.GetProperties(flags))
        {
            if (nameFilter is not null && !nameFilter.Contains(prop.Name))
                continue;
            if (!MatchesMemberType("Property", memberType))
                continue;

            var key = prop.Name + "|Property";
            if (emitted.Add(key))
            {
                var def = $"{prop.PropertyType.Name} {prop.Name} {{ get; set; }}";
                yield return MakeMemberItem(typeName, prop.Name, "Property", def);
            }
        }

        foreach (var method in type.GetMethods(flags))
        {
            if (method.IsSpecialName) continue;
            if (nameFilter is not null && !nameFilter.Contains(method.Name))
                continue;
            if (!MatchesMemberType("Method", memberType))
                continue;

            var key = method.Name + "|Method";
            if (emitted.Add(key))
            {
                var def = FormatMethod(method);
                yield return MakeMemberItem(typeName, method.Name, "Method", def);
            }
        }
    }

    private static string FormatMethod(System.Reflection.MethodInfo method)
    {
        var ret = method.ReturnType.Name;
        var paramList = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        return $"{ret} {method.Name}({paramList})";
    }

    private static IItem MakeMemberItem(string typeName, string name, string memberType, string definition)
        => new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = name },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty
                .With("TypeName", typeName)
                .With("Name", name)
                .With("MemberType", memberType)
                .With("Definition", definition),
        };
}
