using OpenShell.Items;
using OpenShell.Paths;
using EnumerationOptions = OpenShell.Paths.EnumerationOptions;

namespace OpenShell.Providers;

/// <summary>
/// Root contract for every provider. Per ADR-0001, this is intentionally tiny — concrete capabilities
/// are expressed via the other interfaces in this namespace. Per ADR-0038, all capability interfaces are
/// tagged with <see cref="ProviderApiAttribute"/> to document their versioning lifecycle.
/// </summary>
[ProviderApi(SinceVersion = "1.0.0")]
public interface IProvider
{
    ProviderInfo Info { get; }

    /// <summary>Declared capabilities. Must match the interfaces this instance implements.</summary>
    IReadOnlySet<ProviderCapability> Capabilities { get; }

    /// <summary>Optional initialisation hook invoked once after registration.</summary>
    ValueTask InitialiseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>Per ADR-0001: read a single item at <c>path</c>.</summary>
[ProviderApi(SinceVersion = "1.0.0")]
public interface IItemProvider
{
    ValueTask<IItem?> GetItemAsync(ItemPath path, CancellationToken cancellationToken = default);
}

/// <summary>Per ADR-0001: enumerate children of a container. Per ADR-0002: streaming + cancellable.</summary>
[ProviderApi(SinceVersion = "1.0.0")]
public interface IContainerProvider
{
    IAsyncEnumerable<IItem> GetChildrenAsync(
        ItemPath path,
        EnumerationOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Per ADR-0001: path validation and normalisation.</summary>
[ProviderApi(SinceVersion = "1.0.0")]
public interface INavigationProvider
{
    bool IsValidPath(ItemPath path);
    ItemPath NormalizePath(ItemPath path);
}

/// <summary>Per ADR-0001: read binary content as a stream.</summary>
[ProviderApi(SinceVersion = "1.0.0")]
public interface IContentProvider
{
    ValueTask<Stream> OpenReadAsync(ItemPath path, CancellationToken cancellationToken = default);
}

/// <summary>Per ADR-0001: provider-specific properties (size, attributes, ACLs, ...).</summary>
[ProviderApi(SinceVersion = "1.0.0")]
public interface IPropertyProvider
{
    ValueTask<PropertyBag> GetPropertiesAsync(IItem item, CancellationToken cancellationToken = default);
}

/// <summary>Per ADR-0001: security descriptors (ACLs/owners).</summary>
[ProviderApi(SinceVersion = "1.0.0")]
public interface ISecurityProvider
{
    ValueTask<Acl?> GetAclAsync(IItem item, CancellationToken cancellationToken = default);
}

/// <summary>Per ADR-0001: list mountable drives (real or virtual).</summary>
[ProviderApi(SinceVersion = "1.0.0")]
public interface IDriveProvider
{
    ValueTask<IReadOnlyList<ProviderDrive>> GetDrivesAsync(CancellationToken cancellationToken = default);
}
