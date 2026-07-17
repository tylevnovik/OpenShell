---
topic: about_path_syntax
synopsis: Explains the provider-namespaced path model used by OpenShell.
---

# about_path_syntax

## SHORT DESCRIPTION

OpenShell paths use a `provider::internalPath` model so the same syntax can
address files, archives, registry keys, and remote objects. Bare paths fall
back to the current default provider.

## LONG DESCRIPTION

An `ItemPath` is a `readonly record struct` with two components: a provider
name (lowercase) and a provider-internal path (using `/` as separator).

### Format

    provider::internalPath

Examples:

| Path                                  | Meaning                                       |
|---------------------------------------|-----------------------------------------------|
| `fs::C:\Users\foo`                    | FileSystem, absolute Windows path.            |
| `fs::users/blmpt`                     | FileSystem, relative to current location.     |
| `zip::archive.zip/subdir/file.txt`    | Archive provider, virtual mount.              |
| `reg::HKLM/Software/Microsoft`        | Registry provider, hive + tree path.          |
| `s3::bucket/key`                      | Remote S3 provider.                           |
| `C:\Users\foo`                        | Bare path; resolves against current provider. |
| `.` / `..`                            | Relative segments against `CurrentLocation`. |

### Bare Paths

When no `provider::` prefix is given, the path is interpreted relative to the
host's current location. The CLI's `cd` command changes only the internal
path; to switch providers, supply the full `provider::path` form:

    set-location zip::archive.zip/subdir

### Path Operations

`ItemPath` exposes:

- `Parse(string)` — accepts `provider::path` or bare path.
- `Combine(string relative)` — joins a relative segment, normalising separators.
- `GetParent()` — returns the parent path, or the same path at root.
- `GetName()` — returns the last segment (file or directory name).
- `Display` — `provider::internalPath` form for prompts.
- `FriendlyName` — bare path for the default provider; full form otherwise.

### Cross-Provider References

Pipeline commands can reference any provider's paths explicitly:

    get-childitem fs::C:\ | copy-item -dest zip::archive.zip/

Cross-provider copy requires explicit `From` and `To`; implicit provider
switching on relative paths is not supported.

### Separator Normalisation

All internal paths use `/`. The FileSystem provider converts back to `\` on
Windows only at display time. Mixed separators in input (`C:\Users/foo`) are
normalised to `/` on parse.

### Rooted vs Relative

A path is rooted when it starts with `/` or matches a Windows drive pattern
(`C:`). Non-rooted paths are resolved against `CommandContext.CurrentLocation`.

## SEE ALSO

- `about_providers`
- `set-location`
- `get-childitem`
- `get-item`
