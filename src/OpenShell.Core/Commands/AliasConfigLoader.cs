using Tomlyn;
using Tomlyn.Model;

namespace OpenShell.Commands;

/// <summary>
/// Loads and persists alias/function TOML configuration per ADR-0024.
/// Reads <c>aliases.toml</c> and <c>functions.toml</c> from the user-global and project
/// configuration directories. Missing files yield empty lists (no exception) so that
/// a fresh install with no configuration starts cleanly.
/// </summary>
public static class AliasConfigLoader
{
    /// <summary>
    /// Load alias definitions from a TOML file. Returns an empty list if the file does
    /// not exist or fails to parse (parse failures are logged to stderr, never thrown).
    /// </summary>
    /// <param name="path">Absolute path to <c>aliases.toml</c>.</param>
    /// <returns>Parsed alias definitions, or empty list on missing/invalid file.</returns>
    public static IReadOnlyList<AliasDefinition> LoadAliases(string path)
    {
        if (!File.Exists(path)) return Array.Empty<AliasDefinition>();
        try
        {
            var text = File.ReadAllText(path);
            var root = Toml.ToModel(text, path);
            var result = new List<AliasDefinition>();
            if (root.TryGetValue("alias", out var aliasVal) && aliasVal is TomlArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not TomlTable table) continue;
                    var name = TryGetString(table, "name");
                    var command = TryGetString(table, "command");
                    if (name is null || command is null) continue;
                    var description = TryGetString(table, "description");
                    result.Add(new AliasDefinition
                    {
                        Name = name,
                        Command = command,
                        Description = description,
                        Source = AliasSource.Builtin,
                    });
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warn] failed to parse aliases from {path}: {ex.Message}");
            return Array.Empty<AliasDefinition>();
        }
    }

    /// <summary>
    /// Load user functions from a TOML file. Returns an empty list if the file does
    /// not exist or fails to parse (parse failures are logged to stderr, never thrown).
    /// </summary>
    /// <param name="path">Absolute path to <c>functions.toml</c>.</param>
    /// <returns>Parsed user functions, or empty list on missing/invalid file.</returns>
    public static IReadOnlyList<UserFunction> LoadFunctions(string path)
    {
        if (!File.Exists(path)) return Array.Empty<UserFunction>();
        try
        {
            var text = File.ReadAllText(path);
            var root = Toml.ToModel(text, path);
            var result = new List<UserFunction>();
            if (root.TryGetValue("function", out var funcVal) && funcVal is TomlArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not TomlTable table) continue;
                    var name = TryGetString(table, "name");
                    var body = TryGetString(table, "body");
                    if (name is null || body is null) continue;
                    var description = TryGetString(table, "description");
                    var parameters = new List<string>();
                    if (table.TryGetValue("parameters", out var paramsVal) && paramsVal is TomlArray paramsArr)
                    {
                        foreach (var p in paramsArr)
                        {
                            if (p is string s) parameters.Add(s);
                        }
                    }
                    result.Add(new UserFunction
                    {
                        Name = name,
                        Body = body,
                        Parameters = parameters,
                        Description = description,
                        Source = AliasSource.Session,
                    });
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warn] failed to parse functions from {path}: {ex.Message}");
            return Array.Empty<UserFunction>();
        }
    }

    /// <summary>
    /// Persist alias definitions to a TOML file in <c>[[alias]]</c> format.
    /// Creates the parent directory if missing.
    /// </summary>
    /// <param name="path">Absolute destination path.</param>
    /// <param name="aliases">Aliases to write.</param>
    public static void SaveAliases(string path, IReadOnlyList<AliasDefinition> aliases)
    {
        var root = new TomlTable();
        var arr = new TomlArray();
        foreach (var a in aliases)
        {
            var entry = new TomlTable();
            entry["name"] = a.Name;
            entry["command"] = a.Command;
            if (!string.IsNullOrEmpty(a.Description)) entry["description"] = a.Description;
            arr.Add(entry);
        }
        root["alias"] = arr;

        EnsureParentDirectory(path);
        File.WriteAllText(path, Toml.FromModel(root));
    }

    /// <summary>
    /// Persist user functions to a TOML file in <c>[[function]]</c> format.
    /// Creates the parent directory if missing.
    /// </summary>
    /// <param name="path">Absolute destination path.</param>
    /// <param name="functions">Functions to write.</param>
    public static void SaveFunctions(string path, IReadOnlyList<UserFunction> functions)
    {
        var root = new TomlTable();
        var arr = new TomlArray();
        foreach (var f in functions)
        {
            var entry = new TomlTable();
            entry["name"] = f.Name;
            entry["body"] = f.Body;
            if (f.Parameters.Count > 0)
            {
                var paramsArr = new TomlArray();
                foreach (var p in f.Parameters) paramsArr.Add(p);
                entry["parameters"] = paramsArr;
            }
            if (!string.IsNullOrEmpty(f.Description)) entry["description"] = f.Description;
            arr.Add(entry);
        }
        root["function"] = arr;

        EnsureParentDirectory(path);
        File.WriteAllText(path, Toml.FromModel(root));
    }

    private static string? TryGetString(TomlTable table, string key)
        => table.TryGetValue(key, out var v) ? v as string : null;

    private static void EnsureParentDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
}
