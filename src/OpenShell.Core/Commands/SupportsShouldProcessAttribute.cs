namespace OpenShell.Commands;

/// <summary>
/// Marks a command as supporting <c>-WhatIf</c> / <c>-Confirm</c> common parameters
/// and the <see cref="CommandContext.ShouldProcess(string, string, ConfirmImpact)"/>
/// safety gate. Per ADR-0049 §1. Mirrors PowerShell's <c>[CmdletBinding(SupportsShouldProcess)]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SupportsShouldProcessAttribute : Attribute
{
    /// <summary>
    /// Static destructive-impact classification of the command. Compared against the
    /// current <c>ConfirmPreference</c> threshold at runtime to decide whether to
    /// auto-prompt. Per ADR-0049 §5. Defaults to <see cref="ConfirmImpact.Medium"/>.
    /// </summary>
    public ConfirmImpact ConfirmImpact { get; init; } = ConfirmImpact.Medium;
}

/// <summary>
/// Static destructive-impact classification. Ordered <c>None &lt; Low &lt; Medium &lt; High</c>.
/// Per ADR-0049 §5. Compared against <see cref="ConfirmPreference"/> to decide auto-prompting.
/// </summary>
public enum ConfirmImpact
{
    /// <summary>Never auto-confirm (read-only / side-effect-free).</summary>
    None = 0,

    /// <summary>Light impact (overwrite file, change config).</summary>
    Low = 1,

    /// <summary>Medium impact (delete single file, clear content).</summary>
    Medium = 2,

    /// <summary>High impact (force recursive delete, kill process, format drive).</summary>
    High = 3,
}

/// <summary>
/// 标记命令的远程能力。Per ADR-0049 §1 (原"延迟实现", 现已落实)。
/// 与 PowerShell 的 <c>RemotingCapability</c> 枚举对齐:
/// <list type="bullet">
///   <item><see cref="None"/>: 不支持远程 (默认)。</item>
///   <item><see cref="PowerShell"/>: 通过 PowerShell 远程处理 (Invoke-Command -ComputerName)。</item>
///   <item><see cref="SupportedByCommand"/>: 命令自身实现远程 (如 Get-Process -ComputerName)。</item>
///   <item><see cref="Unsupported"/>: 显式标记不支持远程, 调用方传递 -ComputerName 时报错。</item>
/// </list>
/// </summary>
public enum RemotingCapability
{
    /// <summary>无远程能力 (默认)。</summary>
    None = 0,

    /// <summary>通过 PowerShell 远程会话 (Runspace/WinRM/SSH) 透明执行。</summary>
    PowerShell = 1,

    /// <summary>命令自身实现远程 (例如内置 -ComputerName 参数走 RPC/SMB/SSH)。</summary>
    SupportedByCommand = 2,

    /// <summary>显式不支持远程; 传递远程参数将报 <c>NotSupportedException</c>。</summary>
    Unsupported = 3,
}
