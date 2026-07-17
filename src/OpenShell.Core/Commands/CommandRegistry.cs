using System.Collections.Concurrent;
using System.Reflection;

namespace OpenShell.Commands;

/// <summary>Default <see cref="ICommandRegistry"/>. Thread-safe.</summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly ConcurrentDictionary<string, CommandDescriptor> _byFullName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CommandDescriptor> _byAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Type, CommandDescriptor> _byType = new();

    public IReadOnlyCollection<CommandDescriptor> Registered => _byFullName.Values.ToList();

    public void Register(CommandDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!_byFullName.TryAdd(descriptor.FullName, descriptor))
            throw new DuplicateCommandException(descriptor.FullName);

        _byType[descriptor.CommandType] = descriptor;

        foreach (var alias in descriptor.Aliases)
        {
            var key = alias.StartsWith("-") ? alias[1..] : alias;
            _byAlias[key] = descriptor;
        }

        // Common short aliases: "ls" for get-childitem, etc. — these can be declared per command
        // via [Verb(Aliases = new[] { "ls" })]; no special-casing here.
    }

    public CommandDescriptor? Resolve(string nameOrAlias)
    {
        if (string.IsNullOrWhiteSpace(nameOrAlias)) return null;
        var key = nameOrAlias.Trim();
        if (key.StartsWith("-")) key = key[1..];

        return _byFullName.TryGetValue(key, out var byFull)
            ? byFull
            : _byAlias.TryGetValue(key, out var byAlias)
                ? byAlias
                : null;
    }

    public int RegisterFromAssembly(Assembly assembly)
    {
        var registered = 0;
        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract) continue;
            if (type.GetCustomAttribute<VerbAttribute>() is null) continue;
            var descriptor = CommandDescriptor.FromType(type);
            Register(descriptor);
            registered++;
        }
        return registered;
    }

    /// <summary>
    /// 按类型批量注册命令。Per ADR-0016. 单个类型注册失败时跳过并继续后续类型，
    /// 不抛异常（与 RegisterFromAssembly 的 fail-fast 行为不同，因为插件类型可能未带 [Verb]）。
    /// </summary>
    public int RegisterTypes(IEnumerable<Type> commandTypes)
    {
        ArgumentNullException.ThrowIfNull(commandTypes);
        var registered = 0;
        foreach (var type in commandTypes)
        {
            if (type is null) continue;
            if (!type.IsClass || type.IsAbstract) continue;
            if (type.GetCustomAttribute<VerbAttribute>() is null) continue;
            try
            {
                var descriptor = CommandDescriptor.FromType(type);
                Register(descriptor);
                registered++;
            }
            catch (DuplicateCommandException)
            {
                // 已注册过则跳过；插件重复加载是合法场景（manifest 重入）。
            }
        }
        return registered;
    }

    /// <summary>
    /// 按类型批量反注册命令。Per ADR-0016. 可重入：找不到的类型直接跳过。
    /// 同时移除对应的 alias 索引。
    /// </summary>
    public int UnregisterTypes(IEnumerable<Type> commandTypes)
    {
        ArgumentNullException.ThrowIfNull(commandTypes);
        var removed = 0;
        foreach (var type in commandTypes)
        {
            if (type is null) continue;
            if (!_byType.TryRemove(type, out var descriptor) || descriptor is null) continue;
            removed++;

            // 移除 full name 索引。
            _byFullName.TryRemove(descriptor.FullName, out _);

            // 移除该 descriptor 关联的所有 alias 索引。
            foreach (var alias in descriptor.Aliases)
            {
                var key = alias.StartsWith("-") ? alias[1..] : alias;
                // 只在 alias 仍指向该 descriptor 时移除（避免误删被后续注册覆盖的同名 alias）。
                if (_byAlias.TryGetValue(key, out var current) && current == descriptor)
                {
                    _byAlias.TryRemove(key, out _);
                }
            }
        }
        return removed;
    }
}
