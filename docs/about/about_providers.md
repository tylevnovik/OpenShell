---
topic: about_providers
synopsis: Explains the OpenShell provider model and how capabilities compose.
---

# about_providers

## SHORT DESCRIPTION

OpenShell accesses heterogeneous data sources (file system, archives, registry,
remote) through a unified provider abstraction. Each provider declares the
capabilities it supports via fine-grained interfaces, and commands query those
capabilities before dispatch.

## LONG DESCRIPTION

OpenShell providers replace the single-large-interface model used by traditional
shells. Instead of one base class with many `throw NotSupportedException`
stubs, each provider implements only the capability interfaces it truly supports.
Capabilities are surfaced both statically (via `ProviderInfo.Capabilities`) and
dynamically (via `is IContainerProvider` checks).

### Capability Interfaces

| Interface              | Purpose                                    |
|------------------------|--------------------------------------------|
| `IItemProvider`         | Read item metadata (size, timestamps).     |
| `IContainerProvider`    | Enumerate children of a container.        |
| `INavigationProvider`   | Parse, normalize, combine paths.          |
| `IContentProvider`      | Read/write item content as a stream.       |
| `IPropertyProvider`     | Read/write item properties.                |
| `ISecurityProvider`     | Read/write ACLs.                           |
| `IDriveProvider`        | Mount/unmount drives.                      |

### Built-in Providers

- `fs` — FileSystem provider (read/write/content/property/security/drive).
- `zip` — Archive provider (read/content/property, no native write).
- `reg` — Registry provider (read/write/property/security, no content).
- `s3` — Remote S3 provider (read/optional write/property, no security).

### Resolving Capabilities

Commands obtain a typed capability via `CommandContext.Providers.ResolveCapability<T>(path)`:

    var container = ctx.Providers.ResolveCapability<IContainerProvider>(path)
        ?? throw new InvalidOperationException(
            $"Provider '{path.Provider}' does not support enumeration.");

The dispatcher rejects unsupported calls before invocation, so providers
never see requests they cannot fulfil.

## SEE ALSO

- `about_path_syntax`
- `about_pipeline`
- `get-childitem`
- `get-item`
- `set-location`
