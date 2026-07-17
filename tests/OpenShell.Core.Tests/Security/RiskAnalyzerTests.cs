using FluentAssertions;
using OpenShell.Paths;
using OpenShell.Security;
using Xunit;

namespace OpenShell.Core.Tests.Security;

/// <summary>
/// ADR-0036 §3: RiskAnalyzer 单测。
/// 验证 remove-item / copy-item / set-content / get-item 各场景的风险等级判定。
/// </summary>
public class RiskAnalyzerTests
{
    private readonly RiskAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_GetItem_ReturnsLow()
    {
        var path = ItemPath.Parse("fs::C:/Users/test/file.txt");

        var risk = _analyzer.Analyze("get-item", path, force: false, recurse: false, useTrash: false);

        // 未知命令默认 Low (Safe 在 ADR 中保留给明确只读命令, 默认值 Low 与 ADR §3 一致)。
        risk.Should().Be(OperationRisk.Low);
    }

    [Fact]
    public void Analyze_RemoveItem_NormalFile_ReturnsHigh()
    {
        var path = ItemPath.Parse("fs::C:/Users/test/file.txt");

        var risk = _analyzer.Analyze("remove-item", path, force: false, recurse: false, useTrash: true);

        risk.Should().Be(OperationRisk.High);
    }

    [Fact]
    public void Analyze_RemoveItem_RootDirectory_ReturnsCritical()
    {
        // Unix 根目录
        var unixRoot = ItemPath.Parse("fs::/");
        var riskUnix = _analyzer.Analyze("remove-item", unixRoot, force: false, recurse: true, useTrash: true);
        riskUnix.Should().Be(OperationRisk.Critical);

        // Windows 盘符根
        if (OperatingSystem.IsWindows())
        {
            var winRoot = ItemPath.Parse("fs::C:/");
            var riskWin = _analyzer.Analyze("remove-item", winRoot, force: false, recurse: true, useTrash: true);
            riskWin.Should().Be(OperationRisk.Critical);
        }
    }

    [Fact]
    public void Analyze_RemoveItem_ForceWithoutTrash_ReturnsDestructive()
    {
        var path = ItemPath.Parse("fs::C:/Users/test/file.txt");

        // --force 且不走 Trash → 物理删除, Destructive
        var risk = _analyzer.Analyze("remove-item", path, force: true, recurse: false, useTrash: false);

        risk.Should().Be(OperationRisk.Destructive);
    }

    [Fact]
    public void Analyze_RemoveItem_ForceWithTrash_ReturnsHigh()
    {
        // --force 但仍走 Trash → 仅物理删除标志, 但 Trash 仍在 → High (非 Destructive)
        // (按 AnalyzeRemove 实现: force && !useTrash 才升级 Destructive)
        var path = ItemPath.Parse("fs::C:/Users/test/file.txt");

        var risk = _analyzer.Analyze("remove-item", path, force: true, recurse: false, useTrash: true);

        // 普通文件路径, 非 root / 非系统目录, GetItemCount=-1 (无法判断), force+useTrash → High。
        risk.Should().Be(OperationRisk.High);
    }

    [Fact]
    public void Analyze_RemoveItem_SystemDirectory_ReturnsCritical()
    {
        if (OperatingSystem.IsWindows())
        {
            var winSys = ItemPath.Parse("fs::C:/Windows");
            var risk = _analyzer.Analyze("remove-item", winSys, force: false, recurse: true, useTrash: true);
            risk.Should().Be(OperationRisk.Critical);

            // 子目录也要识别。
            var winSub = ItemPath.Parse("fs::C:/Windows/System32/drivers");
            var riskSub = _analyzer.Analyze("remove-item", winSub, force: false, recurse: true, useTrash: true);
            riskSub.Should().Be(OperationRisk.Critical);

            // Program Files
            var progFiles = ItemPath.Parse("fs::C:/Program Files");
            var riskProg = _analyzer.Analyze("remove-item", progFiles, force: false, recurse: true, useTrash: true);
            riskProg.Should().Be(OperationRisk.Critical);
        }
        else
        {
            var etc = ItemPath.Parse("fs::/etc");
            var risk = _analyzer.Analyze("remove-item", etc, force: false, recurse: true, useTrash: true);
            risk.Should().Be(OperationRisk.Critical);

            var usrBin = ItemPath.Parse("fs::/usr/bin");
            var riskSub = _analyzer.Analyze("remove-item", usrBin, force: false, recurse: true, useTrash: true);
            riskSub.Should().Be(OperationRisk.Critical);
        }
    }

    [Fact]
    public void Analyze_CopyItem_NonFsProvider_ReturnsMedium()
    {
        // 非 fs 视为跨 Provider
        var path = ItemPath.Parse("s3::bucket/file.txt");

        var risk = _analyzer.Analyze("copy-item", path, force: false, recurse: false, useTrash: false);

        risk.Should().Be(OperationRisk.Medium);
    }

    [Fact]
    public void Analyze_CopyItem_FsNormalFile_ReturnsLow()
    {
        var path = ItemPath.Parse("fs::C:/Users/test/file.txt");

        var risk = _analyzer.Analyze("copy-item", path, force: false, recurse: false, useTrash: false);

        risk.Should().Be(OperationRisk.Low);
    }

    [Fact]
    public void Analyze_SetContent_SystemFile_ReturnsHigh()
    {
        if (OperatingSystem.IsWindows())
        {
            var sysFile = ItemPath.Parse("fs::C:/Windows/System32/drivers/etc/hosts");

            var risk = _analyzer.Analyze("set-content", sysFile, force: false, recurse: false, useTrash: false);

            risk.Should().Be(OperationRisk.High);
        }
        else
        {
            var sysFile = ItemPath.Parse("fs::/etc/passwd");

            var risk = _analyzer.Analyze("set-content", sysFile, force: false, recurse: false, useTrash: false);

            risk.Should().Be(OperationRisk.High);
        }
    }

    [Fact]
    public void Analyze_SetContent_NormalFile_ReturnsMedium()
    {
        var path = ItemPath.Parse("fs::C:/Users/test/file.txt");

        var risk = _analyzer.Analyze("set-content", path, force: false, recurse: false, useTrash: false);

        risk.Should().Be(OperationRisk.Medium);
    }

    [Fact]
    public void Analyze_NullPath_ReturnsLow()
    {
        var risk = _analyzer.Analyze("remove-item", path: null, force: false, recurse: false, useTrash: false);

        risk.Should().Be(OperationRisk.Low);
    }

    [Fact]
    public void Analyze_AliasRm_WorksSameAsRemoveItem()
    {
        var path = ItemPath.Parse("fs::C:/Users/test/file.txt");

        var risk = _analyzer.Analyze("rm", path, force: false, recurse: false, useTrash: true);

        risk.Should().Be(OperationRisk.High);
    }

    [Fact]
    public void IsRoot_RootPath_ReturnsTrue()
    {
        _analyzer.IsRoot(ItemPath.Parse("fs::/")).Should().BeTrue();

        if (OperatingSystem.IsWindows())
        {
            _analyzer.IsRoot(ItemPath.Parse("fs::C:/")).Should().BeTrue();
            _analyzer.IsRoot(ItemPath.Parse("fs::D:/")).Should().BeTrue();
        }
    }

    [Fact]
    public void IsRoot_NonRootPath_ReturnsFalse()
    {
        _analyzer.IsRoot(ItemPath.Parse("fs::/home/user")).Should().BeFalse();
        _analyzer.IsRoot(ItemPath.Parse("fs::C:/Users")).Should().BeFalse();
    }

    [Fact]
    public void IsSystemDirectory_MatchesCurrentOS()
    {
        if (OperatingSystem.IsWindows())
        {
            _analyzer.IsSystemDirectory(ItemPath.Parse("fs::C:/Windows")).Should().BeTrue();
            _analyzer.IsSystemDirectory(ItemPath.Parse("fs::C:/Windows/System32")).Should().BeTrue();
            _analyzer.IsSystemDirectory(ItemPath.Parse("fs::C:/Program Files")).Should().BeTrue();
            _analyzer.IsSystemDirectory(ItemPath.Parse("fs::C:/Users")).Should().BeFalse();
        }
        else
        {
            _analyzer.IsSystemDirectory(ItemPath.Parse("fs::/etc")).Should().BeTrue();
            _analyzer.IsSystemDirectory(ItemPath.Parse("fs::/usr/bin")).Should().BeTrue();
            _analyzer.IsSystemDirectory(ItemPath.Parse("fs::/home/user")).Should().BeFalse();
        }
    }

    [Fact]
    public void IsSystemDirectory_NonFsProvider_ReturnsFalse()
    {
        // 非 fs Provider 由 Provider 自行保护, RiskAnalyzer 不识别为系统目录。
        _analyzer.IsSystemDirectory(ItemPath.Parse("reg::HKLM/SOFTWARE")).Should().BeFalse();
    }

    [Fact]
    public void Analyze_LargeBatch_WithOverride_ReturnsCritical()
    {
        // 子类重写 GetItemCount 模拟大批量删除。
        var analyzer = new TestableRiskAnalyzer(itemCount: 1500);
        var path = ItemPath.Parse("fs::C:/Users/test/subdir");

        var risk = analyzer.Analyze("remove-item", path, force: false, recurse: true, useTrash: true);

        risk.Should().Be(OperationRisk.Critical);
    }

    [Fact]
    public void Analyze_LargeBatch_BelowThreshold_ReturnsHigh()
    {
        var analyzer = new TestableRiskAnalyzer(itemCount: 500);
        var path = ItemPath.Parse("fs::C:/Users/test/subdir");

        var risk = analyzer.Analyze("remove-item", path, force: false, recurse: true, useTrash: true);

        risk.Should().Be(OperationRisk.High);
    }

    /// <summary>测试子类: 重写 GetItemCount 注入预期条目数。</summary>
    private sealed class TestableRiskAnalyzer : RiskAnalyzer
    {
        private readonly int _itemCount;

        public TestableRiskAnalyzer(int itemCount) => _itemCount = itemCount;

        protected override int GetItemCount(ItemPath path) => _itemCount;
    }
}
