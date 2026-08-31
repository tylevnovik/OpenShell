namespace OpenShell.Errors;

/// <summary>
/// Base class for all OpenShell domain exceptions. Per ADR-0026.
/// Core layers throw these; command layers convert to <see cref="ErrorRecord"/>.
/// </summary>
public abstract class OpenShellException : Exception
{
    protected OpenShellException(string message) : base(message) { }
    protected OpenShellException(string message, Exception innerException) : base(message, innerException) { }

    public abstract ErrorCategory Category { get; }
}

public sealed class ItemNotFoundException : OpenShellException
{
    public ItemNotFoundException(string message) : base(message) { }
    public ItemNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    public override ErrorCategory Category => ErrorCategory.ItemNotFound;
}

public sealed class ItemAlreadyExistsException : OpenShellException
{
    public ItemAlreadyExistsException(string message) : base(message) { }
    public ItemAlreadyExistsException(string message, Exception innerException) : base(message, innerException) { }
    public override ErrorCategory Category => ErrorCategory.ItemAlreadyExists;
}

public sealed class PermissionDeniedException : OpenShellException
{
    public PermissionDeniedException(string message) : base(message) { }
    public PermissionDeniedException(string message, Exception innerException) : base(message, innerException) { }
    public override ErrorCategory Category => ErrorCategory.PermissionDenied;
}

public sealed class ProviderNotFoundException : OpenShellException
{
    public ProviderNotFoundException(string message) : base(message) { }
    public ProviderNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    public override ErrorCategory Category => ErrorCategory.ProviderNotFound;
}

public sealed class CapabilityNotSupported : OpenShellException
{
    public CapabilityNotSupported(string message) : base(message) { }
    public CapabilityNotSupported(string message, Exception innerException) : base(message, innerException) { }
    public override ErrorCategory Category => ErrorCategory.CapabilityNotSupported;
}

public sealed class OperationTimeoutException : OpenShellException
{
    public OperationTimeoutException(string message) : base(message) { }
    public OperationTimeoutException(string message, Exception innerException) : base(message, innerException) { }
    public override ErrorCategory Category => ErrorCategory.OperationTimeout;
}

public sealed class CircuitBrokenException : OpenShellException
{
    public CircuitBrokenException(string message) : base(message) { }
    public CircuitBrokenException(string message, Exception innerException) : base(message, innerException) { }
    public override ErrorCategory Category => ErrorCategory.CircuitBroken;
}

public sealed class ConfigurationErrorException : OpenShellException
{
    public ConfigurationErrorException(string message) : base(message) { }
    public ConfigurationErrorException(string message, Exception innerException) : base(message, innerException) { }
    public override ErrorCategory Category => ErrorCategory.ConfigurationError;
}

/// <summary>命令参数无法绑定到 Args record 时抛出的领域异常。</summary>
public sealed class CommandArgumentException : OpenShellException
{
    public CommandArgumentException(string message) : base(message) { }
    public CommandArgumentException(string message, Exception innerException) : base(message, innerException) { }
    public override ErrorCategory Category => ErrorCategory.InvalidArgument;
}
