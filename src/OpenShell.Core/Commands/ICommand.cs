using Microsoft.Extensions.DependencyInjection;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;
using OpenShell.Providers;

namespace OpenShell.Commands;

/// <summary>
/// Per-Command invocation context. Per ADR-0004, carries registry, host pointer, and current location.
/// Per ADR-0007/ADR-0026 also exposes operation engine and error stream.
/// Per ADR-0049 §8 also exposes <see cref="ShouldProcess(string, string, ConfirmImpact)"/> /
/// <see cref="ShouldContinue(string, string)"/> helpers that delegate to a registered
/// <see cref="IShouldProcessService"/> (and gracefully default to "proceed" when no service is registered).
/// </summary>
public sealed class CommandContext
{
    public required IProviderRegistry Providers { get; init; }
    public required ICommandRegistry Commands { get; init; }
    public required IHost Host { get; init; }
    public required ItemPath CurrentLocation { get; init; }
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Structured error sink. Per ADR-0026.</summary>
    public IErrorStream? Errors { get; init; }

    /// <summary>Operation engine for cp/mv/rm/rename/mkdir/touch. Per ADR-0007.</summary>
    public IOperationEngine? Operations { get; init; }

    /// <summary>Alias registry for user-defined aliases and functions. Per ADR-0024.</summary>
    public IAliasRegistry? Aliases { get; init; }

    /// <summary>Help service for command help resolution. Per ADR-0025.</summary>
    public IHelpService? Help { get; init; }

    /// <summary>Virtual drive registry for New-PSDrive / Remove-PSDrive. Per ADR-0023.</summary>
    public IDriveRegistry? Drives { get; init; }

    /// <summary>Variable registry for $var / $env: / get-variable / set-variable. Per ADR-0042.</summary>
    public OpenShell.Variables.IVariableRegistry? Variables { get; init; }

    /// <summary>Convenience: resolve the container provider at the current location.</summary>
    public IContainerProvider ResolveContainer(ItemPath path)
        => Providers.ResolveCapability<IContainerProvider>(path)
            ?? throw new InvalidOperationException(
                $"Provider '{path.Provider}' does not support enumeration.");

    /// <summary>
    /// Per ADR-0049 §3 / §8: gate a destructive action. Commands decorated with
    /// <c>[SupportsShouldProcess]</c> should call this immediately before performing
    /// the destructive side effect and <c>yield break</c> / <c>return</c> when it returns
    /// <c>false</c> (the user is in WhatIf mode or rejected the prompt).
    /// </summary>
    /// <param name="target">Human-readable description of the operation target.</param>
    /// <param name="action">Human-readable description of the operation action.</param>
    /// <param name="impact">
    /// Effective destructive impact; defaults to <see cref="ConfirmImpact.Medium"/>. Callers
    /// may raise this (e.g. <c>Remove-Item -Recurse -Force</c> → <see cref="ConfirmImpact.High"/>).
    /// </param>
    /// <returns><c>true</c> to proceed with the action; <c>false</c> to skip it.</returns>
    public bool ShouldProcess(string target, string action, ConfirmImpact impact = ConfirmImpact.Medium)
    {
        // No service registered = no ShouldProcess infrastructure available: default to "proceed"
        // so unit-test scenarios and host configurations without the service keep working.
        var service = Host.Services.GetService(typeof(IShouldProcessService)) as IShouldProcessService;
        if (service is null) return true;
        return service.ShouldProcess(target, action, impact);
    }

    /// <summary>
    /// Per ADR-0049 §4 / §8: force-prompt the user regardless of impact / WhatIf / ConfirmPreference.
    /// Use for second-order confirmations (e.g. <c>Remove-Item -Recurse -Force</c> after
    /// <see cref="ShouldProcess(string, string, ConfirmImpact)"/> already returned <c>true</c>).
    /// </summary>
    /// <param name="target">Human-readable description of the operation target.</param>
    /// <param name="action">Human-readable description of the operation action.</param>
    /// <returns><c>true</c> to proceed; <c>false</c> to skip.</returns>
    public bool ShouldContinue(string target, string action)
    {
        var service = Host.Services.GetService(typeof(IShouldProcessService)) as IShouldProcessService;
        if (service is null) return true;
        return service.ShouldContinue(target, action);
    }
}

/// <summary>Marker interface for all commands. Per ADR-0004, command classes are sealed and stateless.</summary>
public interface ICommand;

/// <summary>Command that produces a stream of items (e.g. Get-ChildItem, Where, Select).</summary>
public interface ICommand<TArgs> : ICommand where TArgs : notnull
{
    IAsyncEnumerable<IItem> ExecuteAsync(TArgs args, CommandContext ctx, CancellationToken cancellationToken = default);
}

/// <summary>Marker for pipeline-only commands (where/select/sort/format/out-*).</summary>
public interface IPipelineCommand : ICommand;
