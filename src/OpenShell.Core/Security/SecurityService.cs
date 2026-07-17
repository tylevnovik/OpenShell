using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenShell;
using OpenShell.Paths;

namespace OpenShell.Security;

/// <summary>
/// 安全协调服务。Per ADR-0036 §1-§14.
/// 负责风险评估、确认决策、审计记录的协调。
/// </summary>
/// <remarks>
/// ADR-0036 §14: paranoid 模式下 Critical / Destructive 操作需额外 PIN 确认
/// (PIN 哈希存储于 <c>~/.openshell/security.pin</c>, 0600 权限)。
/// </remarks>
public interface ISecurityService
{
    /// <summary>评估命令风险等级。</summary>
    OperationRisk AssessRisk(string command, ItemPath? path, bool force, bool recurse, bool useTrash);

    /// <summary>按 strictness + role 决定是否需要确认, 弹出确认 UI 并返回用户选择。</summary>
    Task<bool> ConfirmAsync(OperationRisk risk, string command, CancellationToken ct);

    /// <summary>按 role + risk 判断是否被禁止 (Restricted + High+ → false)。</summary>
    bool IsAllowed(OperationRisk risk);

    /// <summary>记录审计日志。</summary>
    Task LogAuditAsync(string command, string args, OperationRisk risk, bool approved, string approvedBy, CancellationToken ct);
}

/// <summary>
/// 默认 <see cref="ISecurityService"/> 实现。Per ADR-0036 §1-§14.
/// </summary>
public sealed class SecurityService : ISecurityService
{
    private const string PinFileName = "security.pin";

    private readonly IAuditService _audit;
    private readonly ProtectedPathRegistry _protectedPaths;
    private readonly SecurityRole _role;
    private readonly SecurityStrictness _strictness;
    private readonly Func<OperationRisk, string, CancellationToken, Task<bool>>? _confirmationPrompt;
    private readonly ISecurePasswordPrompter? _passwordPrompter;
    private readonly ILogger<SecurityService>? _logger;

    public SecurityService(
        IAuditService audit,
        ProtectedPathRegistry protectedPaths,
        SecurityRole role = SecurityRole.User,
        SecurityStrictness strictness = SecurityStrictness.Default,
        Func<OperationRisk, string, CancellationToken, Task<bool>>? confirmationPrompt = null,
        ISecurePasswordPrompter? passwordPrompter = null,
        ILogger<SecurityService>? logger = null)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _protectedPaths = protectedPaths ?? throw new ArgumentNullException(nameof(protectedPaths));
        _role = role;
        _strictness = strictness;
        _confirmationPrompt = confirmationPrompt;
        _passwordPrompter = passwordPrompter;
        _logger = logger;
    }

    /// <inheritdoc />
    public OperationRisk AssessRisk(string command, ItemPath? path, bool force, bool recurse, bool useTrash)
    {
        var analyzer = new RiskAnalyzer();
        return analyzer.Analyze(command, path, force, recurse, useTrash);
    }

    /// <inheritdoc />
    public bool IsAllowed(OperationRisk risk)
    {
        // Restricted 角色: High 及以上禁止。
        if (_role == SecurityRole.Restricted && risk >= OperationRisk.High)
            return false;
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(OperationRisk risk, string command, CancellationToken ct)
    {
        // Safe / Low: 任何 strictness 下都不需确认。
        if (risk <= OperationRisk.Low)
            return true;

        // 不需要确认的风险等级下限 (按 strictness 决定)。
        var confirmThreshold = _strictness switch
        {
            SecurityStrictness.Lax => OperationRisk.Destructive,      // 仅 Destructive 需确认
            SecurityStrictness.Default => OperationRisk.Critical,    // Critical 及以上需确认
            SecurityStrictness.Strict => OperationRisk.High,         // High 及以上需确认
            SecurityStrictness.Paranoid => OperationRisk.Medium,     // Medium 及以上需确认
            _ => OperationRisk.Critical,
        };

        // risk 低于阈值: 不需确认。
        if (risk < confirmThreshold)
            return true;

        // Admin 角色跳过 High 及以下的确认 (Critical / Destructive 仍需确认)。
        if (_role == SecurityRole.Admin && risk <= OperationRisk.High)
            return true;

        // 没有注入确认 UI: 默认拒绝 (保守策略, 避免静默批准)。
        if (_confirmationPrompt is null)
            return false;

        // 弹出确认 UI (CLI / GUI 由调用方注入)。
        var confirmed = await _confirmationPrompt(risk, command, ct).ConfigureAwait(false);
        if (!confirmed)
            return false;

        // ADR-0036 §14: paranoid 模式下 Critical / Destructive 操作需额外 PIN 确认。
        if (_strictness == SecurityStrictness.Paranoid
            && (risk == OperationRisk.Critical || risk == OperationRisk.Destructive))
        {
            return await ConfirmWithPinAsync(command, ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// paranoid 模式 PIN 二次确认。Per ADR-0036 §14.
    /// 读取 <c>~/.openshell/security.pin</c> 中的 SHA256 哈希 (hex), 与用户输入的 SHA256 常时比较。
    /// 无 PIN 文件 / 无提示器 → 降级为普通确认 (带警告); PIN 不匹配 → 拒绝。
    /// </summary>
    private async Task<bool> ConfirmWithPinAsync(string command, CancellationToken ct)
    {
        // 无密码提示器 → 跳过 PIN 检查 (降级到普通确认已通过)。
        if (_passwordPrompter is null)
            return true;

        var pinPath = Path.Combine(OpenShellPaths.Root, PinFileName);
        byte[]? storedHashBytes = null;
        try
        {
            if (File.Exists(pinPath))
            {
                var hex = (await File.ReadAllTextAsync(pinPath, ct).ConfigureAwait(false)).Trim();
                storedHashBytes = Convert.FromHexString(hex);
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            _logger?.LogWarning(ex, "Failed to read/parse security PIN file '{Path}'; skipping PIN check.", pinPath);
            return true;
        }

        if (storedHashBytes is null || storedHashBytes.Length == 0)
        {
            _logger?.LogWarning(
                "Paranoid mode active but no PIN is set at '{Path}'. Destructive operation approved with normal confirmation only. " +
                "Create a PIN file (SHA256 hex of the PIN, mode 0600 on Unix) to enable PIN-gated confirmation.", pinPath);
            return true;
        }

        var input = await _passwordPrompter.PromptPasswordAsync(
            $"Enter PIN to confirm destructive operation '{command}':", ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(input))
            return false; // 用户取消。

        var inputBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        if (!CryptographicOperations.FixedTimeEquals(inputBytes, storedHashBytes))
        {
            _logger?.LogWarning("Paranoid PIN mismatch for destructive operation '{Command}'.", command);
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public Task LogAuditAsync(string command, string args, OperationRisk risk, bool approved, string approvedBy, CancellationToken ct)
    {
        var entry = new AuditEntry(
            Timestamp: DateTimeOffset.UtcNow,
            User: Environment.UserName,
            Command: command,
            Args: args,
            Risk: risk,
            Approved: approved,
            ApprovedBy: approvedBy);
        return _audit.LogAsync(entry, ct);
    }
}
