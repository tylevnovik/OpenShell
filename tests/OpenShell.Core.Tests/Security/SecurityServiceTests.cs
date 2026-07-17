using FluentAssertions;
using OpenShell.Paths;
using OpenShell.Security;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Security;

/// <summary>
/// ADR-0036 §1-§14: SecurityService 单测。
/// 验证:
/// - IsAllowed: Restricted + High+ → false; User + High → true
/// - ConfirmAsync: 各 strictness 下的确认行为 (lax/default/strict/paranoid)
/// - Admin 角色跳过 High 确认 (但 Critical/Destructive 仍需确认)
/// - 用 mock confirmationPrompt 委托
/// - AssessRisk 委托到 RiskAnalyzer
/// - LogAuditAsync 写入审计文件
/// </summary>
public class SecurityServiceTests
{
    private static ProtectedPathRegistry MakeRegistry() => new(initialPaths: Array.Empty<string>());

    [Fact]
    public void IsAllowed_RestrictedRoleAndHigh_ReturnsFalse()
    {
        var svc = new SecurityService(
            new JsonAuditService(filePath: Path.GetTempFileName()),
            MakeRegistry(),
            role: SecurityRole.Restricted,
            strictness: SecurityStrictness.Default);

        svc.IsAllowed(OperationRisk.High).Should().BeFalse();
        svc.IsAllowed(OperationRisk.Critical).Should().BeFalse();
        svc.IsAllowed(OperationRisk.Destructive).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_RestrictedRoleAndMedium_ReturnsTrue()
    {
        var svc = new SecurityService(
            new JsonAuditService(filePath: Path.GetTempFileName()),
            MakeRegistry(),
            role: SecurityRole.Restricted);

        svc.IsAllowed(OperationRisk.Medium).Should().BeTrue();
        svc.IsAllowed(OperationRisk.Low).Should().BeTrue();
        svc.IsAllowed(OperationRisk.Safe).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_UserRoleAndHigh_ReturnsTrue()
    {
        var svc = new SecurityService(
            new JsonAuditService(filePath: Path.GetTempFileName()),
            MakeRegistry(),
            role: SecurityRole.User);

        svc.IsAllowed(OperationRisk.High).Should().BeTrue();
        svc.IsAllowed(OperationRisk.Critical).Should().BeTrue();
        svc.IsAllowed(OperationRisk.Destructive).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_AdminRoleAndAny_ReturnsTrue()
    {
        var svc = new SecurityService(
            new JsonAuditService(filePath: Path.GetTempFileName()),
            MakeRegistry(),
            role: SecurityRole.Admin);

        // IsAllowed 不区分 role (除 Restricted 外都允许); Admin 仍允许全部。
        svc.IsAllowed(OperationRisk.High).Should().BeTrue();
        svc.IsAllowed(OperationRisk.Destructive).Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmAsync_Safe_ReturnsTrue_WithoutPrompt()
    {
        var promptCalled = false;
        var svc = new SecurityService(
            new JsonAuditService(filePath: Path.GetTempFileName()),
            MakeRegistry(),
            role: SecurityRole.User,
            strictness: SecurityStrictness.Paranoid,
            confirmationPrompt: (_, _, _) => { promptCalled = true; return Task.FromResult(true); });

        var result = await svc.ConfirmAsync(OperationRisk.Safe, "get-item", CancellationToken.None);

        result.Should().BeTrue();
        promptCalled.Should().BeFalse("Safe risk never requires confirmation");
    }

    [Fact]
    public async Task ConfirmAsync_Low_ReturnsTrue_WithoutPrompt()
    {
        var promptCalled = false;
        var svc = new SecurityService(
            new JsonAuditService(filePath: Path.GetTempFileName()),
            MakeRegistry(),
            role: SecurityRole.User,
            strictness: SecurityStrictness.Paranoid,
            confirmationPrompt: (_, _, _) => { promptCalled = true; return Task.FromResult(true); });

        var result = await svc.ConfirmAsync(OperationRisk.Low, "copy-item", CancellationToken.None);

        result.Should().BeTrue();
        promptCalled.Should().BeFalse("Low risk never requires confirmation");
    }

    [Fact]
    public async Task ConfirmAsync_Lax_OnlyDestructiveRequiresConfirmation()
    {
        var svc = MakeService(SecurityRole.User, SecurityStrictness.Lax, promptResult: true);

        // Lax: 仅 Destructive 需确认。
        (await svc.ConfirmAsync(OperationRisk.Medium, "set-content", CancellationToken.None)).Should().BeTrue();
        (await svc.ConfirmAsync(OperationRisk.High, "remove-item", CancellationToken.None)).Should().BeTrue();
        (await svc.ConfirmAsync(OperationRisk.Critical, "remove-item -r", CancellationToken.None)).Should().BeTrue();
        // Destructive 触发 prompt, prompt 返回 true。
        (await svc.ConfirmAsync(OperationRisk.Destructive, "remove-item -f", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmAsync_Lax_DestructiveWhenPromptReturnsFalse_ReturnsFalse()
    {
        var svc = MakeService(SecurityRole.User, SecurityStrictness.Lax, promptResult: false);

        var result = await svc.ConfirmAsync(OperationRisk.Destructive, "remove-item -f", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmAsync_Default_CriticalAndDestructiveRequireConfirmation()
    {
        var svc = MakeService(SecurityRole.User, SecurityStrictness.Default, promptResult: true);

        // Default: Critical 及以上需确认。
        (await svc.ConfirmAsync(OperationRisk.High, "remove-item", CancellationToken.None)).Should().BeTrue();
        // Critical + Destructive 触发 prompt, prompt 返回 true。
        (await svc.ConfirmAsync(OperationRisk.Critical, "remove-item -r", CancellationToken.None)).Should().BeTrue();
        (await svc.ConfirmAsync(OperationRisk.Destructive, "remove-item -f", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmAsync_Default_CriticalWhenPromptReturnsFalse_ReturnsFalse()
    {
        var svc = MakeService(SecurityRole.User, SecurityStrictness.Default, promptResult: false);

        var result = await svc.ConfirmAsync(OperationRisk.Critical, "remove-item -r", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmAsync_Strict_HighAndAboveRequireConfirmation()
    {
        var svc = MakeService(SecurityRole.User, SecurityStrictness.Strict, promptResult: true);

        // Strict: High 及以上需确认。
        (await svc.ConfirmAsync(OperationRisk.Medium, "set-content", CancellationToken.None)).Should().BeTrue();
        // High / Critical / Destructive 触发 prompt, prompt 返回 true。
        (await svc.ConfirmAsync(OperationRisk.High, "remove-item", CancellationToken.None)).Should().BeTrue();
        (await svc.ConfirmAsync(OperationRisk.Critical, "remove-item -r", CancellationToken.None)).Should().BeTrue();
        (await svc.ConfirmAsync(OperationRisk.Destructive, "remove-item -f", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmAsync_Strict_HighWhenPromptReturnsFalse_ReturnsFalse()
    {
        var svc = MakeService(SecurityRole.User, SecurityStrictness.Strict, promptResult: false);

        var result = await svc.ConfirmAsync(OperationRisk.High, "remove-item", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmAsync_Paranoid_MediumAndAboveRequireConfirmation()
    {
        var svc = MakeService(SecurityRole.User, SecurityStrictness.Paranoid, promptResult: true);

        // Paranoid: Medium 及以上需确认。
        // (Safe / Low 不需)
        (await svc.ConfirmAsync(OperationRisk.Safe, "get-item", CancellationToken.None)).Should().BeTrue();
        (await svc.ConfirmAsync(OperationRisk.Low, "copy-item", CancellationToken.None)).Should().BeTrue();
        // Medium+ 触发 prompt, prompt 返回 true。
        (await svc.ConfirmAsync(OperationRisk.Medium, "set-content", CancellationToken.None)).Should().BeTrue();
        (await svc.ConfirmAsync(OperationRisk.High, "remove-item", CancellationToken.None)).Should().BeTrue();
        (await svc.ConfirmAsync(OperationRisk.Critical, "remove-item -r", CancellationToken.None)).Should().BeTrue();
        (await svc.ConfirmAsync(OperationRisk.Destructive, "remove-item -f", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmAsync_Paranoid_MediumWhenPromptReturnsFalse_ReturnsFalse()
    {
        var svc = MakeService(SecurityRole.User, SecurityStrictness.Paranoid, promptResult: false);

        var result = await svc.ConfirmAsync(OperationRisk.Medium, "set-content", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmAsync_AdminRole_SkipsHighConfirmation()
    {
        bool promptCalled = false;
        var svc = new SecurityService(
            new JsonAuditService(filePath: Path.GetTempFileName()),
            MakeRegistry(),
            role: SecurityRole.Admin,
            strictness: SecurityStrictness.Strict, // 即便 strict 模式
            confirmationPrompt: (_, _, _) => { promptCalled = true; return Task.FromResult(true); });

        // Admin 跳过 High (即使 strict 也不弹 prompt)
        var result = await svc.ConfirmAsync(OperationRisk.High, "remove-item", CancellationToken.None);

        result.Should().BeTrue();
        promptCalled.Should().BeFalse("Admin skips High confirmation");
    }

    [Fact]
    public async Task ConfirmAsync_AdminRole_StillConfirmsCritical()
    {
        bool promptCalled = false;
        var svc = new SecurityService(
            new JsonAuditService(filePath: Path.GetTempFileName()),
            MakeRegistry(),
            role: SecurityRole.Admin,
            strictness: SecurityStrictness.Default,
            confirmationPrompt: (_, _, _) => { promptCalled = true; return Task.FromResult(true); });

        var result = await svc.ConfirmAsync(OperationRisk.Critical, "remove-item -r", CancellationToken.None);

        result.Should().BeTrue();
        promptCalled.Should().BeTrue("Admin still requires Critical confirmation");
    }

    [Fact]
    public async Task ConfirmAsync_NoPromptInjected_DefaultRefusesAboveThreshold()
    {
        // 没有注入 prompt 委托 + 风险超过阈值 → 保守拒绝。
        var svc = new SecurityService(
            new JsonAuditService(filePath: Path.GetTempFileName()),
            MakeRegistry(),
            role: SecurityRole.User,
            strictness: SecurityStrictness.Default,
            confirmationPrompt: null);

        var result = await svc.ConfirmAsync(OperationRisk.Critical, "remove-item -r", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmAsync_PassesRiskAndCommandToPrompt()
    {
        OperationRisk? capturedRisk = null;
        string? capturedCommand = null;
        var svc = new SecurityService(
            new JsonAuditService(filePath: Path.GetTempFileName()),
            MakeRegistry(),
            role: SecurityRole.User,
            strictness: SecurityStrictness.Default,
            confirmationPrompt: (risk, cmd, _) => { capturedRisk = risk; capturedCommand = cmd; return Task.FromResult(true); });

        await svc.ConfirmAsync(OperationRisk.Critical, "remove-item -r fs::C:/", CancellationToken.None);

        capturedRisk.Should().Be(OperationRisk.Critical);
        capturedCommand.Should().Be("remove-item -r fs::C:/");
    }

    [Fact]
    public async Task AssessRisk_DelegatesToRiskAnalyzer()
    {
        var svc = new SecurityService(
            new JsonAuditService(filePath: Path.GetTempFileName()),
            MakeRegistry());

        // 普通文件删除 → High
        var risk = svc.AssessRisk("remove-item", ItemPath.Parse("fs::C:/Users/test/file.txt"),
            force: false, recurse: false, useTrash: true);
        risk.Should().Be(OperationRisk.High);

        // 根目录 → Critical
        var rootRisk = svc.AssessRisk("remove-item", ItemPath.Parse("fs::/"),
            force: false, recurse: true, useTrash: true);
        rootRisk.Should().Be(OperationRisk.Critical);
    }

    [Fact]
    public async Task LogAuditAsync_WritesToAuditFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "audit.jsonl");
        var audit = new JsonAuditService(filePath: path);
        var svc = new SecurityService(audit, MakeRegistry());

        await svc.LogAuditAsync("remove-item", "fs::C:/Users/test/file.txt",
            OperationRisk.High, approved: true, approvedBy: "prompt", CancellationToken.None);

        var entries = await audit.QueryAsync();
        entries.Should().HaveCount(1);
        entries[0].Command.Should().Be("remove-item");
        entries[0].Args.Should().Be("fs::C:/Users/test/file.txt");
        entries[0].Risk.Should().Be(OperationRisk.High);
        entries[0].Approved.Should().BeTrue();
        entries[0].ApprovedBy.Should().Be("prompt");
        entries[0].User.Should().Be(Environment.UserName);
    }

    [Fact]
    public void Constructor_NullAudit_Throws()
    {
        var act = () => new SecurityService(null!, MakeRegistry());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        var act = () => new SecurityService(new JsonAuditService(filePath: Path.GetTempFileName()), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private static SecurityService MakeService(SecurityRole role, SecurityStrictness strictness, bool promptResult)
    {
        return new SecurityService(
            new JsonAuditService(filePath: Path.GetTempFileName()),
            MakeRegistry(),
            role: role,
            strictness: strictness,
            confirmationPrompt: (_, _, _) => Task.FromResult(promptResult));
    }
}
