using System.Runtime.CompilerServices;
using System.Text;
using OpenShell.Commands;
using OpenShell.Items;
using OpenShell.Paths;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Providers.Variables;

/// <summary>
/// Function provider: exposes user-defined functions as a virtual drive.
/// Per ADR-0024. Path: <c>function::Name</c>.
/// Functions are stored in the <see cref="IAliasRegistry"/>; reading returns the function body,
/// writing creates/updates a session function, deleting removes it.
/// </summary>
public sealed class FunctionProvider :
    IProvider,
    IItemProvider,
    IContainerProvider,
    INavigationProvider,
    IContentProvider,
    IContentWriterProvider,
    IItemMutatorProvider,
    IDriveProvider
{
    private readonly IAliasRegistry _aliases;

    public ProviderInfo Info { get; } = new()
    {
        Name = "function",
        Version = new Version(1, 0, 0),
        Description = "Function drive provider (exposes user functions as a virtual drive)",
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

    public FunctionProvider(IAliasRegistry aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        _aliases = aliases;
    }

    // ---- IDriveProvider ----

    public ValueTask<IReadOnlyList<ProviderDrive>> GetDrivesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProviderDrive> drives = new[]
        {
            new ProviderDrive
            {
                Name = "Function:",
                Root = ItemPath.Root("function"),
                DisplayLabel = "User functions",
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
                Path = ItemPath.Root("function"),
                Kind = ItemKind.Directory,
                Properties = PropertyBag.Empty.With("Name", "Function:"),
            });
        }

        var fn = _aliases.ResolveFunction(name);
        if (fn is null)
            return ValueTask.FromResult<IItem?>(null);

        return ValueTask.FromResult<IItem?>(ToItem(path, fn));
    }

    // ---- IContainerProvider ----

    public async IAsyncEnumerable<IItem> GetChildrenAsync(
        ItemPath path,
        EnumerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var fn in _aliases.ListFunctions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(options.Filter)
                && !fn.Name.Contains(options.Filter, options.FilterComparison))
                continue;

            var childPath = new ItemPath
            {
                Provider = "function",
                InternalPath = fn.Name,
            };
            yield return ToItem(childPath, fn);
        }
    }

    // ---- INavigationProvider ----

    public bool IsValidPath(ItemPath path) => path.Provider == "function";

    public ItemPath NormalizePath(ItemPath path)
    {
        var internalPath = path.InternalPath.Trim('/').Trim('\\');
        return new ItemPath { Provider = "function", InternalPath = internalPath };
    }

    // ---- IContentProvider ----

    public ValueTask<Stream> OpenReadAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = ExtractName(path);
        var fn = _aliases.ResolveFunction(name);
        var body = fn?.Body ?? "";
        return ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(body)));
    }

    // ---- IContentWriterProvider ----

    public ValueTask<Stream> OpenWriteAsync(ItemPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new FunctionWriteStream(_aliases, ExtractName(path)));
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
        _aliases.RemoveSessionFunction(name);
        return ValueTask.CompletedTask;
    }

    public ValueTask RenameAsync(ItemPath path, string newName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = ExtractName(path);
        var fn = _aliases.ResolveFunction(name);
        if (fn is null) return ValueTask.CompletedTask;
        _aliases.RemoveSessionFunction(name);
        _aliases.SetSessionFunction(fn with { Name = newName });
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

    private static Item ToItem(ItemPath path, UserFunction fn)
    {
        return new Item
        {
            Path = path,
            Kind = ItemKind.File,
            Properties = PropertyBag.Empty
                .With("Name", fn.Name)
                .With("Value", fn.Body)
                .With("Body", fn.Body)
                .With("Parameters", string.Join(", ", fn.Parameters))
                .With("Description", fn.Description)
                .With("Source", fn.Source.ToString()),
        };
    }

    /// <summary>Stream that commits its content as a function body on Dispose.</summary>
    private sealed class FunctionWriteStream : MemoryStream
    {
        private readonly IAliasRegistry _aliases;
        private readonly string _name;

        public FunctionWriteStream(IAliasRegistry aliases, string name)
        {
            _aliases = aliases;
            _name = name;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Position = 0;
                using var reader = new StreamReader(this, Encoding.UTF8, leaveOpen: true);
                var body = reader.ReadToEnd();
                _aliases.SetSessionFunction(new UserFunction
                {
                    Name = _name,
                    Body = body,
                    Parameters = Array.Empty<string>(),
                });
            }
            base.Dispose(disposing);
        }
    }
}
