---
topic: about_aliases
synopsis: Describes OpenShell aliases and user-defined functions.
---

# about_aliases

## SHORT DESCRIPTION

OpenShell supports user-defined aliases (short text substitutions) and
user-defined functions (named parameter blocks). Both can be persisted across
sessions or scoped to the current session only.

## LONG DESCRIPTION

There are two extensibility mechanisms: aliases and functions. Aliases are
plain text substitutions; functions support named parameters and multi-statement
bodies.

### Alias Tiers

Aliases are resolved in priority order (highest first):

1. **Session** — set via `Set-Alias`, lives only for the current process.
2. **User global** — persisted to `~/.openshell/aliases.toml`.
3. **Project** — loaded from `<cwd>/.openshell/aliases.toml`.
4. **Builtin** — declared on commands via `[Verb(Aliases = new[]{"ls"})]`.

Higher-priority tiers override lower ones. The `Get-Alias` command lists the
currently active alias for each name with its source tier.

### Alias Expansion

Aliases are pure text replacement, applied to whole tokens only:

    alias ll="get-childitem -l"
    ll -r        # expands to: get-childitem -l -r

Aliases may contain pipes:

    alias ll="get-childitem | sort by name"
    ll | where size > 1MB
    # expands to: get-childitem | sort by name | where size > 1MB

### User Functions

Functions live in `~/.openshell/functions.toml` (or `.openshell/functions.toml`
for project scope) and support named parameters:

    [[function]]
    name = "find-large"
    parameters = ["path", "sizeMB"]
    body = """
    get-childitem -r $path | where size > ($sizeMB * 1MB) | sort by size desc
    """
    description = "Find files larger than N MB"

Invoke with positional or named arguments:

    find-large fs::C:/Users 100

### Function vs Alias

| Feature           | Alias | Function |
|-------------------|-------|----------|
| Named parameters  | no    | yes      |
| Multi-statement   | no    | yes      |
| Recursion          | no    | no       |
| Performance        | free  | minimal  |
| Use case          | short | composed |

### Naming Rules

- Alias and function names cannot contain `-` (would clash with Verb-Noun).
- Names cannot start with a digit.
- Names must match whole tokens; `lsa` does not expand `ls`.

### Cycle Detection

On configuration load and on every session mutation, the registry detects
circular aliases (`a -> b -> a`) and raises a `ConfigurationErrorException`.

### Management Commands

| Command          | Purpose                              |
|------------------|--------------------------------------|
| `Get-Alias`      | List aliases, optional `-Name` glob. |
| `Set-Alias`      | Set a session-scoped alias.          |
| `Remove-Alias`   | Remove a session alias.             |
| `Get-Function`   | List user functions.                |
| `Set-Function`   | Define a session-scoped function.   |
| `Remove-Function`| Remove a session function.          |

## SEE ALSO

- `about_functions`
- `set-alias`
- `get-alias`
- `export-alias`
