namespace OpenShell.Commands;

/// <summary>
/// Alias registry combining three tiers: built-in (compiled), user-global (TOML), session (in-memory).
/// Per ADR-0024. Functions live on the same registry; functions take precedence over aliases.
/// </summary>
public interface IAliasRegistry
{
    /// <summary>Resolve a token to its expansion. Returns null if not an alias.</summary>
    AliasDefinition? Resolve(string name);

    /// <summary>Resolve a token to a user function. Returns null if not a function.</summary>
    UserFunction? ResolveFunction(string name);

    /// <summary>List all currently active aliases (session > user > built-in).</summary>
    IReadOnlyList<AliasDefinition> List();

    /// <summary>List all user functions.</summary>
    IReadOnlyList<UserFunction> ListFunctions();

    /// <summary>Set a session-scoped alias.</summary>
    void SetSessionAlias(string name, string command, string? description = null);

    /// <summary>Remove a session-scoped alias. Returns false if not a session alias.</summary>
    bool RemoveSessionAlias(string name);

    /// <summary>Set a session-scoped function.</summary>
    void SetSessionFunction(UserFunction function);

    /// <summary>Remove a session-scoped function by name.</summary>
    bool RemoveSessionFunction(string name);

    /// <summary>Reload user-global + project aliases from TOML files.</summary>
    void ReloadFromConfiguration();

    /// <summary>Built-in aliases keyed by name. Populated by command discovery (from <c>[Verb(Aliases)]</c>).</summary>
    IReadOnlyDictionary<string, string> Builtins { get; }

    /// <summary>
    /// Read-only access to user-global + project + session aliases (excludes builtins).
    /// Used by <c>Export-Alias</c> to persist user-defined aliases only. Per ADR-0024 §7.
    /// </summary>
    /// <returns>User-defined aliases (no builtins).</returns>
    IReadOnlyList<AliasDefinition> ListUserDefined();

    /// <summary>
    /// Read-only access to user-global + project + session functions (excludes any builtin).
    /// Used by <c>Set-Function</c> when persisting to <c>functions.toml</c>. Per ADR-0024 §8.
    /// </summary>
    /// <returns>User-defined functions.</returns>
    IReadOnlyList<UserFunction> ListUserDefinedFunctions();
}

/// <summary>An alias definition.</summary>
public sealed record AliasDefinition
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public AliasSource Source { get; init; } = AliasSource.Builtin;
    public string? Description { get; init; }
}

/// <summary>A user-defined function with named parameters.</summary>
public sealed record UserFunction
{
    public required string Name { get; init; }
    public required string Body { get; init; }   // template with $1, $2, $name, $input
    public IReadOnlyList<string> Parameters { get; init; } = Array.Empty<string>();
    public string? Description { get; init; }
    public AliasSource Source { get; init; } = AliasSource.Session;
}

public enum AliasSource { Builtin, UserGlobal, Project, Session }
