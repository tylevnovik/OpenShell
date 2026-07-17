using OpenShell.Paths;

namespace OpenShell.Commands;

/// <summary>
/// Registry of commands. Per ADR-0004, scanned at startup from assemblies, shared by CLI and GUI hosts.
/// Per ADR-0016: 也支持插件运行时按类型注册/反注册。
/// </summary>
public interface ICommandRegistry
{
    IReadOnlyCollection<CommandDescriptor> Registered { get; }

    void Register(CommandDescriptor descriptor);

    /// <summary>Resolve a command by full name (e.g. "get-childitem") or alias.</summary>
    CommandDescriptor? Resolve(string nameOrAlias);

    /// <summary>Scan an assembly for [Verb]-decorated classes and register them.</summary>
    int RegisterFromAssembly(System.Reflection.Assembly assembly);

    /// <summary>
    /// 按类型批量注册命令。Per ADR-0016: 插件加载时调用。每个类型必须带 [Verb] 特性。
    /// 返回成功注册的数量。
    /// </summary>
    int RegisterTypes(System.Collections.Generic.IEnumerable<Type> commandTypes);

    /// <summary>
    /// 按类型批量反注册命令。Per ADR-0016: 插件卸载时调用。
    /// 必须可重入（重复调用安全）。返回成功移除的数量。
    /// </summary>
    int UnregisterTypes(System.Collections.Generic.IEnumerable<Type> commandTypes);
}

/// <summary>Thrown when duplicate commands are registered. Per ADR-0004, this is fail-fast.</summary>
public sealed class DuplicateCommandException(string fullName)
    : InvalidOperationException($"Command '{fullName}' is already registered.");
