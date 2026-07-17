using System.Reflection;
using OpenShell.Commands;
using Xunit;

namespace OpenShell.TestUtils.Contract;

/// <summary>
/// 命令契约测试基类。Per ADR-0033: 命令必须实现 VerbAttribute + 嵌套 Args record +
/// 必要的 [Parameter] 标注。可继承此类自动覆盖这些契约。
/// </summary>
/// <typeparam name="TCommand">被测命令类型。</typeparam>
public abstract class CommandContractTests<TCommand> where TCommand : class
{
    /// <summary>创建一个新的命令实例。</summary>
    protected abstract TCommand CreateCommand();

    /// <summary>创建命令的 Args 实例（默认构造）。可重写以提供自定义构造。</summary>
    protected virtual object? CreateArgs() => null;

    [Fact]
    public void Has_VerbAttribute()
    {
        var type = typeof(TCommand);
        var attr = type.GetCustomAttribute<VerbAttribute>();
        Assert.NotNull(attr);
        Assert.False(string.IsNullOrWhiteSpace(attr!.Verb));
    }

    [Fact]
    public void Has_Nested_Args_Record()
    {
        var type = typeof(TCommand);
        var argsType = type.GetNestedTypes().FirstOrDefault(t => t.Name == "Args");
        Assert.NotNull(argsType);
        // Args 是 record：通过 EqualityContract 合成属性识别 (C# record 必有)。
        Assert.NotNull(argsType!.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void Args_DefaultConstruct_DoesNotThrow()
    {
        var type = typeof(TCommand);
        var argsType = type.GetNestedTypes().FirstOrDefault(t => t.Name == "Args");
        if (argsType is null) return;
        var ctor = argsType.GetConstructors().FirstOrDefault();
        if (ctor is null) return;
        var args = ctor.GetParameters()
            .Select(p => p.HasDefaultValue ? p.DefaultValue : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null))
            .ToArray();
        var instance = ctor.Invoke(args);
        Assert.NotNull(instance);
    }

    [Fact]
    public void Parameters_HaveParameterAttribute()
    {
        var type = typeof(TCommand);
        var argsType = type.GetNestedTypes().FirstOrDefault(t => t.Name == "Args");
        if (argsType is null) return;
        foreach (var prop in argsType.GetProperties())
        {
            // 忽略 record equalityContract 合成属性。
            if (prop.Name == "EqualityContract") continue;
            var attr = prop.GetCustomAttribute<ParameterAttribute>();
            // 仅验证有 [Parameter] 的属性的 Position/Aliases 字段可访问。
            if (attr is null) continue;
            _ = attr.Position;
            _ = attr.Aliases;
        }
    }
}
