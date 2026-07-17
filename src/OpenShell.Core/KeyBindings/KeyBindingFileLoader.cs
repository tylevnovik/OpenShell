using System.Collections;
using Tomlyn;
using Tomlyn.Model;

namespace OpenShell.KeyBindings;

/// <summary>
/// A user-defined keybinding entry loaded from keybindings.toml. Per ADR-0027 section 4.
/// Entries with Unbind set remove a matching default binding.
/// </summary>
/// <param name="GestureText">Raw gesture text e.g. Ctrl+Shift+F.</param>
/// <param name="Command">Command full name; null for unbind entries.</param>
/// <param name="Args">Optional command arguments.</param>
/// <param name="When">Optional When expression source.</param>
/// <param name="Description">Optional human-readable description.</param>
/// <param name="Unbind">True to remove a matching binding instead of adding one.</param>
public sealed record UserKeyBinding(
    string GestureText,
    string? Command,
    IReadOnlyDictionary<string, string>? Args,
    string? When,
    string? Description,
    bool Unbind);

/// <summary>
/// Loads and persists user keybinding customizations from keybindings.toml.
/// Per ADR-0027 section 4. Missing files yield an empty list (graceful degradation).
/// Parse failures are logged to stderr and never thrown.
/// </summary>
public sealed class KeyBindingFileLoader
{
    private readonly string _filePath;

    /// <summary>
    /// Construct a loader for the given file path, defaulting to the
    /// user-global keybindings file when null.
    /// </summary>
    /// <param name="filePath">Optional explicit path to keybindings.toml.</param>
    public KeyBindingFileLoader(string? filePath = null)
    {
        _filePath = filePath ?? OpenShellPaths.KeyBindingsFile;
    }

    /// <summary>The resolved file path.</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Load user bindings from the TOML file. Returns an empty list if the
    /// file is missing or invalid (parse failures are logged to stderr).
    /// </summary>
    /// <returns>Parsed user bindings, or empty list on missing or invalid file.</returns>
    public IReadOnlyList<UserKeyBinding> Load()
    {
        if (!File.Exists(_filePath)) return Array.Empty<UserKeyBinding>();
        try
        {
            var text = File.ReadAllText(_filePath);
            var root = Toml.ToModel(text, _filePath);
            var result = new List<UserKeyBinding>();
            if (root.TryGetValue("binding", out var bindingVal))
            {
                // Tomlyn parses [[binding]] array-of-tables as TomlTableArray and
                // inline arrays as TomlArray; handle both for robustness.
                IEnumerable? entries = bindingVal switch
                {
                    TomlTableArray tta => tta,
                    TomlArray ta => ta,
                    _ => null,
                };
                if (entries is not null)
                {
                    foreach (var item in entries)
                    {
                        if (item is not TomlTable table) continue;
                        var gesture = TryGetString(table, "gesture");
                        if (string.IsNullOrEmpty(gesture)) continue;
                        result.Add(new UserKeyBinding(
                            GestureText: gesture!,
                            Command: TryGetString(table, "command"),
                            Args: TryGetArgs(table),
                            When: TryGetString(table, "when"),
                            Description: TryGetString(table, "description"),
                            Unbind: TryGetBool(table, "unbind")));
                    }
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warn] failed to parse keybindings from {_filePath}: {ex.Message}");
            return Array.Empty<UserKeyBinding>();
        }
    }

    /// <summary>
    /// Persist user bindings to the TOML file in binding-table format.
    /// Creates the parent directory if missing.
    /// </summary>
    /// <param name="bindings">Bindings to write.</param>
    public void Save(IReadOnlyList<UserKeyBinding> bindings)
    {
        var root = new TomlTable();
        var arr = new TomlTableArray();
        foreach (var b in bindings)
        {
            var entry = new TomlTable();
            entry["gesture"] = b.GestureText;
            if (b.Unbind)
            {
                entry["unbind"] = true;
            }
            else
            {
                if (!string.IsNullOrEmpty(b.Command)) entry["command"] = b.Command!;
                if (b.Args is { Count: > 0 })
                {
                    var argsTable = new TomlTable();
                    foreach (var kv in b.Args) argsTable[kv.Key] = kv.Value;
                    entry["args"] = argsTable;
                }
                if (!string.IsNullOrEmpty(b.When)) entry["when"] = b.When!;
                if (!string.IsNullOrEmpty(b.Description)) entry["description"] = b.Description!;
            }
            arr.Add(entry);
        }
        root["binding"] = arr;

        EnsureParentDirectory(_filePath);
        File.WriteAllText(_filePath, Toml.FromModel(root));
    }

    private static string? TryGetString(TomlTable table, string key)
        => table.TryGetValue(key, out var v) ? v as string : null;

    private static bool TryGetBool(TomlTable table, string key)
        => table.TryGetValue(key, out var v) && v is bool b && b;

    private static IReadOnlyDictionary<string, string>? TryGetArgs(TomlTable table)
    {
        if (!table.TryGetValue("args", out var v) || v is not TomlTable argsTable) return null;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in argsTable)
        {
            if (kv.Value is string s) dict[kv.Key] = s;
        }
        return dict.Count == 0 ? null : dict;
    }

    private static void EnsureParentDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
}
