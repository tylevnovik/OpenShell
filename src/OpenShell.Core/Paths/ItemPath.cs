using System.Globalization;

namespace OpenShell.Paths;

/// <summary>
/// Provider-namespaced immutable path. Per ADR-0006.
/// Format: <c>provider::internalPath</c>, e.g. <c>fs::C:/Users/foo</c>, <c>zip::archive.zip/sub</c>.
/// Bare paths (without <c>provider::</c>) are resolved against the host's current default provider.
/// </summary>
public readonly record struct ItemPath
{
    private readonly string? _provider;
    private readonly string? _internalPath;

    /// <summary>Provider name in lowercase (e.g. "fs", "zip", "reg", "s3").</summary>
    public string Provider
    {
        get => _provider ?? "fs";
        init => _provider = string.IsNullOrEmpty(value) ? "fs" : value.ToLowerInvariant();
    }

    /// <summary>Provider-internal path, always uses '/' as separator.</summary>
    public string InternalPath
    {
        get => _internalPath ?? "";
        init => _internalPath = NormalizeSeparators(value ?? "");
    }

    /// <summary>True if the internal path is rooted (absolute within the provider).</summary>
    public bool IsRooted =>
        (InternalPath.Length > 0 && InternalPath[0] == '/')
        || IsWindowsDrive(InternalPath);

    /// <summary>Display form used in CLI prompts and debugging: <c>fs::C:/Users/foo</c>.</summary>
    public string Display => $"{Provider}::{InternalPath}";

    /// <summary>Friendly form used in GUI labels: bare path for the default provider, full display otherwise.</summary>
    public string FriendlyName => InternalPath;

    public ItemPath(string provider, string internalPath, bool _ = false)
    {
        Provider = provider;
        InternalPath = internalPath;
    }

    public static ItemPath Root(string provider) => new() { Provider = provider, InternalPath = "/" };

    /// <summary>
    /// Parse a path string. Supports:
    /// <list type="bullet">
    ///   <item><c>provider::internal/path</c> — explicit provider.</item>
    ///   <item><c>fs::C:\Users\foo</c> — backslash is accepted, normalised to '/'.</item>
    ///   <item><c>.</c>, <c>..</c>, <c>sub/dir</c> — bare relative path (default provider assumed by caller).</item>
    /// </list>
    /// </summary>
    public static ItemPath Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Path cannot be empty.", nameof(input));

        ReadOnlySpan<char> span = input.AsSpan().Trim();
        var sepIdx = span.IndexOf("::".AsSpan());
        if (sepIdx >= 0)
        {
            var provider = span[..sepIdx].ToString();
            var internalPath = span[(sepIdx + 2)..].ToString();
            return new ItemPath { Provider = provider, InternalPath = internalPath };
        }

        // Bare path: assume the caller knows the default provider; we keep "fs" as placeholder
        // until the host resolves it via CurrentLocation.Provider.
        return new ItemPath { Provider = "fs", InternalPath = span.ToString() };
    }

    /// <summary>Combine with a relative path segment. Returns a new ItemPath in the same provider.</summary>
    public ItemPath Combine(string relative)
    {
        if (string.IsNullOrEmpty(relative))
            return this;

        var normalized = NormalizeSeparators(relative);
        if (normalized.Length > 0 && normalized[0] == '/')
            return this with { InternalPath = normalized };

        var sep = InternalPath.Length == 0 || InternalPath[^1] == '/' ? "" : "/";
        return this with { InternalPath = $"{InternalPath}{sep}{normalized}" };
    }

    /// <summary>Get the parent path. Returns the same path if already at root.</summary>
    public ItemPath GetParent()
    {
        var internalPath = InternalPath.TrimEnd('/');
        var lastSep = internalPath.LastIndexOf('/');
        if (lastSep < 0)
        {
            // e.g. "C:" drive root or single segment — keep as-is
            return this with { InternalPath = internalPath + "/" };
        }
        return this with { InternalPath = internalPath[..lastSep] };
    }

    /// <summary>Get the last segment of the path (file or directory name).</summary>
    public string GetName()
    {
        var internalPath = InternalPath.TrimEnd('/');
        var lastSep = internalPath.LastIndexOf('/');
        return lastSep < 0 ? internalPath : internalPath[(lastSep + 1)..];
    }

    private static string NormalizeSeparators(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        return input.Replace('\\', '/');
    }

    private static bool IsWindowsDrive(string path)
        => path.Length >= 2
           && char.IsLetter(path[0])
           && path[1] == ':';

    public override string ToString() => Display;
}
