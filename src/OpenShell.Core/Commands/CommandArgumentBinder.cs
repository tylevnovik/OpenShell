using System.Reflection;
using OpenShell.Errors;

namespace OpenShell.Commands;

/// <summary>
/// 统一把命令行 token 绑定到命令 Args record。
/// CLI、GUI host 和 pipeline 必须共用这套规则，避免某个入口静默吞掉未知参数或缺失参数。
/// </summary>
public static class CommandArgumentBinder
{
    /// <summary>按照命令描述绑定参数并构造 Args 实例。</summary>
    public static object Bind(
        CommandDescriptor descriptor,
        IReadOnlyList<string> tokens,
        Func<Type, string, object?> convertValue)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(convertValue);

        var byName = new Dictionary<string, ParameterDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in descriptor.Parameters)
        {
            AddName(byName, parameter.Name, parameter);
            foreach (var alias in parameter.Aliases)
                AddName(byName, alias, parameter);
        }

        var positional = new List<string>();
        var named = new Dictionary<string, (ParameterDescriptor Parameter, string? Value)>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!IsNamedToken(token))
            {
                positional.Add(token);
                continue;
            }

            var keyAndValue = token[1..];
            var separator = keyAndValue.IndexOf(':');
            string key;
            string? inlineValue;
            if (separator >= 0)
            {
                key = keyAndValue[..separator];
                inlineValue = keyAndValue[(separator + 1)..];
            }
            else
            {
                key = keyAndValue;
                inlineValue = null;
            }

            if (!byName.TryGetValue(key, out var parameter))
                throw new CommandArgumentException(
                    $"Unknown parameter '-{key}' for command '{descriptor.FullName}'.");

            var canonicalName = parameter.Name;
            if (named.ContainsKey(canonicalName))
                throw new CommandArgumentException(
                    $"Parameter '-{canonicalName}' was specified more than once for command '{descriptor.FullName}'.");

            string? value;
            if (parameter.Type == typeof(bool) && inlineValue is null)
            {
                // 开关参数不消费下一个 token；显式 false 使用 -Name:$false 或 -Name:false。
                value = "true";
            }
            else if (inlineValue is not null)
            {
                value = inlineValue;
            }
            else if (i + 1 >= tokens.Count)
            {
                throw new CommandArgumentException(
                    $"Parameter '-{canonicalName}' requires a value.");
            }
            else
            {
                // 值可以以 '-' 开头（例如负数或以短横线开头的字符串），不能用前缀判断是否缺失。
                value = tokens[++i];
            }

            named.Add(canonicalName, (parameter, value));
        }

        var constructor = descriptor.ArgsType.GetConstructors().FirstOrDefault()
            ?? throw new CommandArgumentException(
                $"Command '{descriptor.FullName}' does not expose an Args constructor.");
        var parameters = constructor.GetParameters();
        var values = new object?[parameters.Length];
        var consumedPositional = new HashSet<int>();

        for (var parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
        {
            var constructorParameter = parameters[parameterIndex];
            var descriptorParameter = descriptor.Parameters.FirstOrDefault(p =>
                string.Equals(p.Name, constructorParameter.Name, StringComparison.OrdinalIgnoreCase));

            if (descriptorParameter is null)
            {
                if (constructorParameter.HasDefaultValue)
                {
                    values[parameterIndex] = constructorParameter.DefaultValue;
                    continue;
                }

                throw MissingParameter(descriptor, constructorParameter.Name ?? constructorParameter.Position.ToString());
            }

            var hasNamedValue = named.TryGetValue(descriptorParameter.Name, out var namedValue);
            var matched = false;

            if (!hasNamedValue && descriptorParameter.Position >= 0)
            {
                for (var positionalIndex = descriptorParameter.Position;
                     positionalIndex < positional.Count;
                     positionalIndex++)
                {
                    if (consumedPositional.Contains(positionalIndex))
                        continue;

                    try
                    {
                        var converted = convertValue(constructorParameter.ParameterType, positional[positionalIndex]);
                        if (converted is null && constructorParameter.ParameterType != typeof(object))
                            continue;

                        values[parameterIndex] = converted;
                        consumedPositional.Add(positionalIndex);
                        matched = true;
                        break;
                    }
                    catch
                    {
                        // 该位置的值可能属于后续参数；最终若无参数可接收，再给出明确错误。
                    }
                }
            }

            if (hasNamedValue)
            {
                try
                {
                    var converted = convertValue(constructorParameter.ParameterType, namedValue.Value ?? string.Empty);
                    if (converted is null && constructorParameter.ParameterType != typeof(object))
                        throw new InvalidOperationException("conversion returned null");

                    values[parameterIndex] = converted;
                    matched = true;
                }
                catch (Exception ex) when (ex is not CommandArgumentException)
                {
                    throw new CommandArgumentException(
                        $"Invalid value for parameter '-{descriptorParameter.Name}' on command '{descriptor.FullName}': {ex.Message}", ex);
                }
            }

            if (matched)
                continue;

            if (descriptorParameter.Mandatory || !constructorParameter.HasDefaultValue)
                throw MissingParameter(descriptor, descriptorParameter.Name);

            values[parameterIndex] = constructorParameter.DefaultValue;
        }

        if (consumedPositional.Count != positional.Count)
        {
            var unexpected = positional
                .Where((_, index) => !consumedPositional.Contains(index))
                .FirstOrDefault() ?? string.Empty;
            throw new CommandArgumentException(
                $"Unexpected positional argument '{unexpected}' for command '{descriptor.FullName}'.");
        }

        try
        {
            return constructor.Invoke(values)
                ?? throw new CommandArgumentException($"Command '{descriptor.FullName}' produced null Args.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new CommandArgumentException(
                $"Invalid arguments for command '{descriptor.FullName}': {ex.InnerException.Message}", ex.InnerException);
        }
        catch (ArgumentException ex)
        {
            throw new CommandArgumentException(
                $"Invalid arguments for command '{descriptor.FullName}': {ex.Message}", ex);
        }
    }

    private static bool IsNamedToken(string token)
        => token.Length > 1 && token[0] == '-';

    private static void AddName(
        IDictionary<string, ParameterDescriptor> byName,
        string name,
        ParameterDescriptor parameter)
    {
        var normalized = name.TrimStart('-');
        if (normalized.Length == 0)
            return;

        if (byName.TryGetValue(normalized, out var existing) && !ReferenceEquals(existing, parameter))
            throw new CommandArgumentException(
                $"Parameter name '{normalized}' is ambiguous for command metadata.");

        byName[normalized] = parameter;
    }

    private static CommandArgumentException MissingParameter(CommandDescriptor descriptor, string parameterName)
        => new($"Parameter '-{parameterName}' is required for command '{descriptor.FullName}'.");
}
