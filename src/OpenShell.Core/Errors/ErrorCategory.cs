namespace OpenShell.Errors;

/// <summary>
/// Error classification. Per ADR-0026.
/// New categories require architecture review — keep this enum stable.
/// </summary>
public enum ErrorCategory
{
    Unknown = 0,
    ParseError,
    InvalidArgument,
    ProviderError,
    ProviderNotFound,
    CapabilityNotSupported,
    ItemNotFound,
    ItemAlreadyExists,
    PermissionDenied,
    OperationCancelled,
    OperationTimeout,
    OperationFailed,
    CircuitBroken,
    NetworkError,
    AuthenticationFailed,
    ConfigurationError,
    OutOfMemory,
    IOError,
    NotImplemented,
}
