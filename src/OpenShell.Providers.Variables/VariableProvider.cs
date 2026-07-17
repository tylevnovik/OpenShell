using System.Runtime.CompilerServices;
using System.Text;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Variables;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Providers.Variables;

/// <summary>
/// Variable provider: exposes the current variable registry as a virtual drive.
/// Per ADR-0047 §10. Path: <c>variable::Name</c>.
/// Implements Item / Container / Navigation / Content / ContentWrite / ItemMutator / Drive capabilities.
/// </summary>
public sealed class VariableProvider :
    IProvider,
    IItemProvider,
    IContainerProvider,
    INavigationProvider,
    IContentProvider,
    IContentWriterProvider,
    IItemMutatorProvider,
    IDriveProvider
{
    private readonly IVariableRegistry _variables;

    public ProviderInfo Info { get; } = new()
    {
        Name = "variable",
        Version = new Version(1, 0, 0),
        Description = "Variable drive provider (exposes IVariableRegistry as a virtual drive)",
        Author = "OpenShell",
    };

    public IReadOnlySet<ProviderCapability> Capabilities { get; } = new HashSet<ProviderCapability>
    {
        ProviderCapability.Item,
        ProviderCapability.Container,
        ProviderCapability.Navigation,
        ProviderCapability.Content,
        ProviderCapability.ContentWrite,
        ProviderCapability.Drive,
    };

    public VariableProvider(IVariableRegistry variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        _variables = variables;
    }

    // ---- IDriveProvider ----

    public ValueTask<IReadOnlyList<ProviderDrive>> GetDrivesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProviderDrive> drives = new[]
        {
            new ProviderDrive
            {
                Name = "Variable:",
                Root = ItemPath.Root("variable"),
                DisplayLabel = "OpenShell variables",
                IsMounted = true,
            },
        };
        return ValueTask.FromResult(drives);
    }

    // ---- IItemProvider ----

    public ValueTask<IItem?> GetItemAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = ExtractName(path);
        if (string.IsNullOrEmpty(name))
        {
            // Root listing: treat as directory
            return ValueTask.FromResult<IItem?>(new Item
            {
                Path = ItemPath.Root("variable"),
                Kind = ItemKind.Directory,
                Properties = PropertyBag.Empty.With("Name", "Variable:"),
            });
        }

        var value = _variables.Resolve(name);
        if (value is null && !_variables.List().Any(k => string.Equals(k.Key, name, StringComparison.OrdinalIgnoreCase)))
            return ValueTask.FromResult<IItem?>(null);

        return ValueTask.FromResult<IItem?>(ToItem(path, name, value));
    }

    // ---- IContainerProvider ----

    public async IAsyncEnumerable<IItem> GetChildrenAsync(
        ItemPath path,
        EnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var kv in _variables.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(options.Filter)
                && !kv.Key.Contains(options.Filter, options.FilterComparison))
                continue;

            var childPath = new ItemPath
            {
                Provider = "variable",
                InternalPath = kv.Key,
            };
            yield return ToItem(childPath, kv.Key, kv.Value);
        }
    }

    // ---- INavigationProvider ----

    public bool IsValidPath(ItemPath path) => path.Provider == "variable";

    public ItemPath NormalizePath(ItemPath path)
    {
        // Trim leading/trailing slashes from internal path
        var internalPath = path.InternalPath.Trim('/').Trim('\\');
        return new ItemPath { Provider = "variable", InternalPath = internalPath };
    }

    // ---- IContentProvider ----

    public ValueTask<Stream> OpenReadAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = ExtractName(path);
        var value = _variables.Resolve(name);
        var text = value?.ToString() ?? "";
        return ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(text)));
    }

    // ---- IContentWriterProvider ----

    public ValueTask<Stream> OpenWriteAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Return a writable stream that commits to the variable on Dispose
        return ValueTask.FromResult<Stream>(new VariableWriteStream(_variables, ExtractName(path)));
    }

    public ValueTask<bool> CanWriteAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        var name = ExtractName(path);
        return ValueTask.FromResult(!_variables.IsReadOnly(name));
    }

    // ---- IItemMutatorProvider ----

    public ValueTask CreateDirectoryAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        // Variables have no hierarchy; creating a "directory" is a no-op
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(ItemPath path, bool recurse, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = ExtractName(path);
        _variables.Remove(name);
        return ValueTask.CompletedTask;
    }

    public ValueTask RenameAsync(ItemPath path, string newName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = ExtractName(path);
        var value = _variables.Resolve(name);
        _variables.Remove(name);
        _variables.Set(newName, value!);
        return ValueTask.CompletedTask;
    }

    public ValueTask SetTimestampsAsync(
        ItemPath path,
        DateTimeOffset? modified,
        DateTimeOffset? accessed,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask; // Variables have no timestamps

    // ---- Helpers ----

    private static string ExtractName(ItemPath path)
    {
        var internalPath = path.InternalPath;
        return internalPath.Trim('/').Trim('\\');
    }

    private Item ToItem(ItemPath path, string name, object? value)
    {
        var isReadOnly = _variables.IsReadOnly(name);
        return new Item
        {
            Path = path,
            Kind = ItemKind.File,
            Properties = PropertyBag.Empty
                .With("Name", name)
                .With("Value", value)
                .With("Type", value?.GetType())
                .With("Options", isReadOnly ? "ReadOnly" : "None")
                .With("Scope", "Session"),
        };
    }

    /// <summary>Stream that commits its content to a variable on Dispose.</summary>
    private sealed class VariableWriteStream : MemoryStream
    {
        private readonly IVariableRegistry _vars;
        private readonly string _name;

        public VariableWriteStream(IVariableRegistry vars, string name)
        {
            _vars = vars;
            _name = name;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Position = 0;
                using var reader = new StreamReader(this, Encoding.UTF8, leaveOpen: true);
                var text = reader.ReadToEnd();
                _vars.Set(_name, text);
            }
            base.Dispose(disposing);
        }
    }
}
