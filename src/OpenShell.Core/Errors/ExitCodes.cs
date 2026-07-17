namespace OpenShell.Errors;

/// <summary>
/// Process exit codes. Per ADR-0026.
/// Aligned with POSIX sysexits where applicable.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int GeneralError = 1;
    public const int ParseError = 2;
    public const int InvalidArgument = 3;
    public const int CommandNotFound = 4;
    public const int ProviderError = 5;
    public const int PermissionDenied = 6;
    public const int Cancelled = 7;
    public const int Timeout = 8;
    public const int OperationFailed = 9;
    public const int ConfigurationError = 10;
    /// <summary>POSIX-compatible generic failure.</summary>
    public const int GenericFailure = 64;
    /// <summary>POSIX SIGINT (128 + 2).</summary>
    public const int Interrupted = 130;

    public static int For(ErrorCategory category) => category switch
    {
        ErrorCategory.ParseError => ParseError,
        ErrorCategory.InvalidArgument => InvalidArgument,
        ErrorCategory.ProviderNotFound => CommandNotFound,
        ErrorCategory.ProviderError => ProviderError,
        ErrorCategory.CapabilityNotSupported => ProviderError,
        ErrorCategory.PermissionDenied => PermissionDenied,
        ErrorCategory.AuthenticationFailed => PermissionDenied,
        ErrorCategory.OperationCancelled => Cancelled,
        ErrorCategory.OperationTimeout => Timeout,
        ErrorCategory.OperationFailed => OperationFailed,
        ErrorCategory.ConfigurationError => ConfigurationError,
        ErrorCategory.OutOfMemory => GenericFailure,
        _ => GeneralError,
    };
}
