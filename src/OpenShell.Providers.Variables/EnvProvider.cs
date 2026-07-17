using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;
using OpenShell.Items;
using OpenShell.Paths;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Providers.Variables;

/// <summary>
/// Environment variable provider: exposes system environment variables as a virtual drive.
/// Per ADR-0042 §10.3 / ADR-0047 §10.5. Path: <c>env::NAME</c>.
/// </summary>
public sealed class EnvProvider :
    IProvider,
    IItemProvider,
    IContainerProvider,
    INavigationProvider,
    IContentProvider,
    IContentWriterProvider,
    IItemMutatorProvider,
    IDriveProvider
{
    public ProviderInfo Info { get; } = new()
    {
        Name = "env",
        Version = new Version(1, 0, 0),
        Description = "Environment variable drive provider",
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

    // ---- IDriveProvider ----

    public ValueTask<IReadOnlyList<ProviderDrive>> GetDrivesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProviderDrive> drives = new[]
        {
            new ProviderDrive
            {
                Name = "Env:",
                Root = ItemPath.Root("env"),
                DisplayLabel = "Environment variables",
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
            return ValueTask.FromResult<IItem?>(new Item
            {
                Path = ItemPath.Root("env"),
                Kind = ItemKind.Directory,
                Properties = PropertyBag.Empty.With("Name", "Env:"),
            });
        }

        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
            return ValueTask.FromResult<IItem?>(null);

        return ValueTask.FromResult<IItem?>(ToItem(path, name, value));
    }

    // ---- IContainerProvider ----

    public async IAsyncEnumerable<IItem> GetChildrenAsync(
        ItemPath path,
        EnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.Key?.ToString() ?? "";
            if (string.IsNullOrEmpty(name)) continue;

            if (!string.IsNullOrEmpty(options.Filter)
                && !name.Contains(options.Filter, options.FilterComparison))
                continue;

            var childPath = new ItemPath
            {
                Provider = "env",
                InternalPath = name,
            };
            yield return ToItem(childPath, name, entry.Value);
        }
    }

    // ---- INavigationProvider ----

    public bool IsValidPath(ItemPath path) => path.Provider == "env";

    public ItemPath NormalizePath(ItemPath path)
    {
        var internalPath = path.InternalPath.Trim('/').Trim('\\');
        return new ItemPath { Provider = "env", InternalPath = internalPath };
    }

    // ---- IContentProvider ----

    public ValueTask<Stream> OpenReadAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = ExtractName(path);
        var value = Environment.GetEnvironmentVariable(name) ?? "";
        return ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(value)));
    }

    // ---- IContentWriterProvider ----

    public ValueTask<Stream> OpenWriteAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new EnvWriteStream(ExtractName(path)));
    }

    public ValueTask<bool> CanWriteAsync(ItemPath path, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(true);

    // ---- IItemMutatorProvider ----

    public ValueTask CreateDirectoryAsync(ItemPath path, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask DeleteAsync(ItemPath path, bool recurse, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = ExtractName(path);
        Environment.SetEnvironmentVariable(name, null);
        return ValueTask.CompletedTask;
    }

    public ValueTask RenameAsync(ItemPath path, string newName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = ExtractName(path);
        var value = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, null);
        Environment.SetEnvironmentVariable(newName, value);
        return ValueTask.CompletedTask;
    }

    public ValueTask SetTimestampsAsync(
        ItemPath path,
        DateTimeOffset? modified,
        DateTimeOffset? accessed,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    // ---- Helpers ----

    private static string ExtractName(ItemPath path) => path.InternalPath.Trim('/').Trim('\\');

    private static Item ToItem(ItemPath path, string name, object? value)
    {
        return new Item
        {
            Path = path,
            Kind = ItemKind.File,
            Properties = PropertyBag.Empty
                .With("Name", name)
                .With("Value", value),
        };
    }

    /// <summary>Stream that commits its content to an env var on Dispose.</summary>
    private sealed class EnvWriteStream : MemoryStream
    {
        private readonly string _name;

        public EnvWriteStream(string name) => _name = name;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Position = 0;
                using var reader = new StreamReader(this, Encoding.UTF8, leaveOpen: true);
                var text = reader.ReadToEnd();
                Environment.SetEnvironmentVariable(_name, text);
            }
            base.Dispose(disposing);
        }
    }
}
