using OpenShell.Errors;

namespace OpenShell.Commands;

/// <summary>
/// Default <see cref="IAliasRegistry"/> implementation. Per ADR-0024.
/// Maintains four alias tiers (Builtin &lt; Project &lt; UserGlobal &lt; Session) and
/// three function tiers (Project &lt; UserGlobal &lt; Session). Functions always take
/// precedence over aliases when both define the same name.
/// Cycle detection runs on every configuration reload and on every session mutation;
/// circular aliases raise <see cref="ConfigurationErrorException"/>.
/// </summary>
public sealed class AliasRegistry : IAliasRegistry
{
    private const int MaxCycleDepth = 16;

    private readonly Dictionary<string, string> _builtins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AliasDefinition> _project = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AliasDefinition> _userGlobal = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AliasDefinition> _session = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, UserFunction> _projectFunctions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UserFunction> _userGlobalFunctions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UserFunction> _sessionFunctions = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _userGlobalDir;
    private readonly string _projectDir;

    /// <summary>
    /// Construct an alias registry. Directories default to <c>~/.openshell</c>
    /// (user-global) and <c>&lt;cwd&gt;/.openshell</c> (project). Both can be overridden
    /// for testing or host-supplied paths.
    /// </summary>
    /// <param name="userGlobalDir">User-global config directory (contains <c>aliases.toml</c> / <c>functions.toml</c>).</param>
    /// <param name="projectDir">Project config directory.</param>
    public AliasRegistry(string? userGlobalDir = null, string? projectDir = null)
    {
        _userGlobalDir = userGlobalDir ?? DefaultUserGlobalDir();
        _projectDir = projectDir ?? DefaultProjectDir();
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Builtins => _builtins;

    /// <inheritdoc />
    public AliasDefinition? Resolve(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_session.TryGetValue(name, out var s)) return s;
        if (_userGlobal.TryGetValue(name, out var u)) return u;
        if (_project.TryGetValue(name, out var p)) return p;
        if (_builtins.TryGetValue(name, out var b))
        {
            return new AliasDefinition
            {
                Name = name,
                Command = b,
                Source = AliasSource.Builtin,
            };
        }
        return null;
    }

    /// <inheritdoc />
    public UserFunction? ResolveFunction(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_sessionFunctions.TryGetValue(name, out var s)) return s;
        if (_userGlobalFunctions.TryGetValue(name, out var u)) return u;
        if (_projectFunctions.TryGetValue(name, out var p)) return p;
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<AliasDefinition> List()
    {
        var result = new Dictionary<string, AliasDefinition>(StringComparer.OrdinalIgnoreCase);
        // Iterate lowest → highest priority so higher tiers overwrite lower ones.
        foreach (var kv in _builtins)
        {
            result[kv.Key] = new AliasDefinition
            {
                Name = kv.Key,
                Command = kv.Value,
                Source = AliasSource.Builtin,
            };
        }
        foreach (var kv in _project) result[kv.Key] = kv.Value;
        foreach (var kv in _userGlobal) result[kv.Key] = kv.Value;
        foreach (var kv in _session) result[kv.Key] = kv.Value;
        return result.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<UserFunction> ListFunctions()
    {
        var result = new Dictionary<string, UserFunction>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _projectFunctions) result[kv.Key] = kv.Value;
        foreach (var kv in _userGlobalFunctions) result[kv.Key] = kv.Value;
        foreach (var kv in _sessionFunctions) result[kv.Key] = kv.Value;
        return result.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <inheritdoc />
    public void SetSessionAlias(string name, string command, string? description = null)
    {
        ValidateName(name, "alias");
        if (string.IsNullOrWhiteSpace(command))
            throw new ConfigurationErrorException($"Alias '{name}' command cannot be empty.");
        _session[name] = new AliasDefinition
        {
            Name = name,
            Command = command,
            Description = description,
            Source = AliasSource.Session,
        };
        DetectCycles();
    }

    /// <inheritdoc />
    public bool RemoveSessionAlias(string name) => _session.Remove(name);

    /// <inheritdoc />
    public void SetSessionFunction(UserFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        ValidateName(function.Name, "function");
        if (string.IsNullOrWhiteSpace(function.Body))
            throw new ConfigurationErrorException($"Function '{function.Name}' body cannot be empty.");
        ValidateFunctionBody(function.Body);
        _sessionFunctions[function.Name] = function with { Source = AliasSource.Session };
    }

    /// <inheritdoc />
    public bool RemoveSessionFunction(string name) => _sessionFunctions.Remove(name);

    /// <inheritdoc />
    public void ReloadFromConfiguration()
    {
        _userGlobal.Clear();
        _project.Clear();
        _userGlobalFunctions.Clear();
        _projectFunctions.Clear();

        // User-global aliases / functions.
        var userAliases = AliasConfigLoader.LoadAliases(Path.Combine(_userGlobalDir, "aliases.toml"));
        foreach (var a in userAliases) _userGlobal[a.Name] = a with { Source = AliasSource.UserGlobal };
        var userFunctions = AliasConfigLoader.LoadFunctions(Path.Combine(_userGlobalDir, "functions.toml"));
        foreach (var f in userFunctions) _userGlobalFunctions[f.Name] = f with { Source = AliasSource.UserGlobal };

        // Project aliases / functions (lower priority than user-global).
        var projectAliases = AliasConfigLoader.LoadAliases(Path.Combine(_projectDir, "aliases.toml"));
        foreach (var a in projectAliases) _project[a.Name] = a with { Source = AliasSource.Project };
        var projectFunctions = AliasConfigLoader.LoadFunctions(Path.Combine(_projectDir, "functions.toml"));
        foreach (var f in projectFunctions) _projectFunctions[f.Name] = f with { Source = AliasSource.Project };

        DetectCycles();
    }

    /// <summary>
    /// Populate the builtin alias tier from the command registry. Built-in aliases are
    /// derived from <c>[Verb(Aliases)]</c> attributes; each alias maps to the command's
    /// full name (e.g. <c>ls</c> → <c>get-childitem</c>). Invoke after command discovery.
    /// </summary>
    /// <param name="commands">Command registry populated by assembly scanning.</param>
    public void PopulateBuiltinsFrom(ICommandRegistry commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _builtins.Clear();
        foreach (var desc in commands.Registered)
        {
            foreach (var alias in desc.Aliases)
            {
                if (string.IsNullOrEmpty(alias)) continue;

                // 部分别名约定俗成带默认参数：
                //   mkdir → new-item -type directory
                //   touch → new-item -type file
                // 修复 M1-8：mkdir 别名默认创建目录而非文件。
                // 其余别名直接映射到命令全名。
                _builtins[alias] = BuiltinAliasExpansion(alias, desc.FullName);
            }
        }
    }

    /// <summary>
    /// Compute the expansion string for a builtin alias. Most aliases map 1:1 to the
    /// command full name, but a few Unix-flavoured aliases carry a default parameter
    /// to match user expectations (mkdir creates a directory, touch creates a file).
    /// </summary>
    private static string BuiltinAliasExpansion(string alias, string commandFullName)
        => alias switch
        {
            // D-315: 使用 -type:directory 冒号形式（TokenKind.NamedParameter），
            // 避免 -type directory 被解析为 SwitchParameter(-type) + 位置参数(directory)，
            // 导致 directory 绑定到 Path、project 绑定到 Type → "Unknown item type 'project'"。
            "mkdir" => $"{commandFullName} -type:directory",
            "touch" => $"{commandFullName} -type:file",
            _ => commandFullName,
        };

    /// <summary>
    /// Read-only access to user-global + project + session aliases (excludes builtins).
    /// Used by <c>Export-Alias</c> to persist user-defined aliases only.
    /// </summary>
    /// <returns>User-defined aliases (no builtins).</returns>
    public IReadOnlyList<AliasDefinition> ListUserDefined()
    {
        var result = new Dictionary<string, AliasDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _project) result[kv.Key] = kv.Value;
        foreach (var kv in _userGlobal) result[kv.Key] = kv.Value;
        foreach (var kv in _session) result[kv.Key] = kv.Value;
        return result.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Read-only access to user-global + project + session functions (excludes any builtin).
    /// Used by <c>Set-Function</c> when persisting to <c>functions.toml</c>.
    /// </summary>
    /// <returns>User-defined functions.</returns>
    public IReadOnlyList<UserFunction> ListUserDefinedFunctions()
    {
        var result = new Dictionary<string, UserFunction>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _projectFunctions) result[kv.Key] = kv.Value;
        foreach (var kv in _userGlobalFunctions) result[kv.Key] = kv.Value;
        foreach (var kv in _sessionFunctions) result[kv.Key] = kv.Value;
        return result.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Default user-global config directory: <c>~/.openshell</c>.
    /// Falls back to <c>&lt;cwd&gt;/.openshell</c> when the user profile directory is unavailable.
    /// </summary>
    /// <returns>Absolute path to the user-global config directory.</returns>
    public static string DefaultUserGlobalDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.CurrentDirectory;
        return Path.Combine(home, ".openshell");
    }

    /// <summary>
    /// Default project config directory: <c>&lt;cwd&gt;/.openshell</c>.
    /// </summary>
    /// <returns>Absolute path to the project config directory.</returns>
    public static string DefaultProjectDir()
        => Path.Combine(Environment.CurrentDirectory, ".openshell");

    private void DetectCycles()
    {
        foreach (var alias in List())
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var chain = new List<string> { alias.Name };
            var current = alias.Name;
            for (int i = 0; i < MaxCycleDepth; i++)
            {
                if (!visited.Add(current))
                {
                    chain.Add(current);
                    throw new ConfigurationErrorException(
                        $"Circular alias detected: {string.Join(" -> ", chain)}");
                }
                var resolved = Resolve(current);
                if (resolved is null) break;
                current = ExtractFirstToken(resolved.Command);
                if (current is null) break;
                chain.Add(current);
            }
        }
    }

    private static string? ExtractFirstToken(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var trimmed = command.TrimStart();
        var end = 0;
        while (end < trimmed.Length
               && !char.IsWhiteSpace(trimmed[end])
               && trimmed[end] != '|')
        {
            end++;
        }
        return end == 0 ? null : trimmed[..end];
    }

    private static void ValidateName(string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ConfigurationErrorException($"{kind} name cannot be empty.");
        if (name.Contains('-'))
            throw new ConfigurationErrorException(
                $"{kind} name '{name}' cannot contain '-' (per ADR-0024; would clash with Verb-Noun).");
        if (char.IsDigit(name[0]))
            throw new ConfigurationErrorException(
                $"{kind} name '{name}' cannot start with a digit (per ADR-0024).");
    }

    private static void ValidateFunctionBody(string body)
    {
        if (string.IsNullOrEmpty(body)) return;
        var separators = new[] { ' ', '\t', '\n', '\r', ';', '|' };
        foreach (var raw in body.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('"', '\'').ToLowerInvariant();
            if (token is "exit" or "return")
                throw new ConfigurationErrorException(
                    $"Function body cannot contain '{token}' (per ADR-0024 §10; control flow is not supported).");
        }
    }
}
