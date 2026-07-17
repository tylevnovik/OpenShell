namespace OpenShell.Errors;

/// <summary>
/// Phase in which an error occurred. Per ADR-0026.
/// </summary>
public enum ErrorPhase
{
    Unknown = 0,
    Parse,
    ArgumentBinding,
    ProviderResolution,
    ProviderInitialization,
    Operation,
    Cleanup,
}
