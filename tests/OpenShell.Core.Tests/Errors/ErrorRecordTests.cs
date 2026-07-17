using FluentAssertions;
using OpenShell.Errors;
using OpenShell.Paths;
using Xunit;

namespace OpenShell.Core.Tests.Errors;

/// <summary>
/// ErrorRecord 单元测试。Per ADR-0026, ADR-0033.
/// 验证 FromException 异常映射、SuggestFor 建议、ToString 多行格式。
/// </summary>
public class ErrorRecordTests
{
    [Fact]
    public void FromException_FileNotFoundException_MapsToItemNotFound()
    {
        var ex = new FileNotFoundException("missing.txt");
        var record = ErrorRecord.FromException(ex);
        record.Category.Should().Be(ErrorCategory.ItemNotFound);
    }

    [Fact]
    public void FromException_DirectoryNotFoundException_MapsToItemNotFound()
    {
        var ex = new DirectoryNotFoundException("missing-dir");
        var record = ErrorRecord.FromException(ex);
        record.Category.Should().Be(ErrorCategory.ItemNotFound);
    }

    [Fact]
    public void FromException_UnauthorizedAccessException_MapsToPermissionDenied()
    {
        var ex = new UnauthorizedAccessException("no access");
        var record = ErrorRecord.FromException(ex);
        record.Category.Should().Be(ErrorCategory.PermissionDenied);
    }

    [Fact]
    public void FromException_OperationCanceledException_MapsToOperationCancelled()
    {
        var ex = new OperationCanceledException("cancelled");
        var record = ErrorRecord.FromException(ex);
        record.Category.Should().Be(ErrorCategory.OperationCancelled);
    }

    [Fact]
    public void FromException_TimeoutException_MapsToOperationTimeout()
    {
        var ex = new TimeoutException("timed out");
        var record = ErrorRecord.FromException(ex);
        record.Category.Should().Be(ErrorCategory.OperationTimeout);
    }

    [Fact]
    public void FromException_IOException_MapsToIOError()
    {
        var ex = new IOException("io fail");
        var record = ErrorRecord.FromException(ex);
        record.Category.Should().Be(ErrorCategory.IOError);
    }

    [Fact]
    public void FromException_OutOfMemoryException_MapsToOutOfMemory()
    {
        var ex = new OutOfMemoryException("oom");
        var record = ErrorRecord.FromException(ex);
        record.Category.Should().Be(ErrorCategory.OutOfMemory);
    }

    [Fact]
    public void FromException_OpenShellException_UsesCategoryProperty()
    {
        var ex = new ItemNotFoundException("not here");
        var record = ErrorRecord.FromException(ex);
        record.Category.Should().Be(ErrorCategory.ItemNotFound);
    }

    [Fact]
    public void FromException_UnknownException_MapsToUnknown()
    {
        var ex = new ApplicationException("unknown");
        var record = ErrorRecord.FromException(ex);
        record.Category.Should().Be(ErrorCategory.Unknown);
    }

    [Fact]
    public void FromException_ArgumentNullException_MapsToInvalidArgument()
    {
        var ex = new ArgumentNullException("param");
        var record = ErrorRecord.FromException(ex);
        record.Category.Should().Be(ErrorCategory.InvalidArgument);
    }

    [Fact]
    public void FromException_SetsMessageFromException()
    {
        var ex = new FileNotFoundException("missing.txt");
        var record = ErrorRecord.FromException(ex);
        record.Message.Should().Be("missing.txt");
    }

    [Fact]
    public void FromException_SetsExceptionReference()
    {
        var ex = new IOException("io fail");
        var record = ErrorRecord.FromException(ex);
        record.Exception.Should().BeSameAs(ex);
    }

    [Fact]
    public void FromException_SetsDetailToExceptionToString()
    {
        var ex = new IOException("io fail");
        var record = ErrorRecord.FromException(ex);
        record.Detail.Should().Be(ex.ToString());
    }

    [Fact]
    public void FromException_PassesOperationAndPhase()
    {
        var ex = new IOException("io fail");
        var record = ErrorRecord.FromException(
            ex,
            operation: "copy-item",
            phase: ErrorPhase.Operation);
        record.Operation.Should().Be("copy-item");
        record.Phase.Should().Be(ErrorPhase.Operation);
    }

    [Fact]
    public void FromException_PassesTargetPath()
    {
        var ex = new FileNotFoundException("missing.txt");
        var path = ItemPath.Parse("fs::C:/tmp/missing.txt");
        var record = ErrorRecord.FromException(ex, targetPath: path);
        record.TargetPath.Should().Be(path);
    }

    [Fact]
    public void FromException_PermissionDenied_GeneratesSuggestion()
    {
        var ex = new UnauthorizedAccessException("denied");
        var record = ErrorRecord.FromException(ex);
        record.Suggestion.Should().NotBeNullOrEmpty();
        record.Suggestion!.Should().Contain("elevated");
    }

    [Fact]
    public void FromException_ItemNotFound_GeneratesSuggestion()
    {
        var ex = new FileNotFoundException("missing.txt");
        var record = ErrorRecord.FromException(ex);
        record.Suggestion.Should().NotBeNullOrEmpty();
        record.Suggestion!.Should().Contain("get-childitem");
    }

    [Fact]
    public void FromException_ExplicitSuggestionOverridesDefault()
    {
        var ex = new UnauthorizedAccessException("denied");
        var record = ErrorRecord.FromException(ex, suggestion: "custom hint");
        record.Suggestion.Should().Be("custom hint");
    }

    [Fact]
    public void FromException_UnknownCategory_HasNullSuggestion()
    {
        var ex = new ApplicationException("unknown");
        var record = ErrorRecord.FromException(ex);
        record.Suggestion.Should().BeNull();
    }

    [Fact]
    public void FromException_AssignsNewErrorId()
    {
        var ex = new IOException("io fail");
        var record = ErrorRecord.FromException(ex);
        record.ErrorId.Should().NotBeEmpty();
    }

    [Fact]
    public void ToString_IncludesOperationAndMessage()
    {
        var record = new ErrorRecord
        {
            Message = "boom",
            Operation = "copy-item",
            Category = ErrorCategory.IOError,
        };
        var s = record.ToString();
        s.Should().Contain("copy-item");
        s.Should().Contain("boom");
    }

    [Fact]
    public void ToString_WithTargetPath_IncludesPathLine()
    {
        var path = ItemPath.Parse("fs::C:/tmp/missing.txt");
        var record = new ErrorRecord
        {
            Message = "boom",
            Operation = "copy-item",
            TargetPath = path,
        };
        var s = record.ToString();
        s.Should().Contain("path:");
        s.Should().Contain(path.Display);
    }

    [Fact]
    public void ToString_WithSuggestion_IncludesSuggestionLine()
    {
        var record = new ErrorRecord
        {
            Message = "boom",
            Operation = "copy-item",
            Suggestion = "try again",
        };
        var s = record.ToString();
        s.Should().Contain("suggestion:");
        s.Should().Contain("try again");
    }

    [Fact]
    public void ToString_WithPhase_IncludesPhaseLine()
    {
        var record = new ErrorRecord
        {
            Message = "boom",
            Operation = "copy-item",
            Phase = ErrorPhase.Operation,
        };
        var s = record.ToString();
        s.Should().Contain("phase:");
        s.Should().Contain("operation");
    }

    [Fact]
    public void ToString_IncludesErrorIdLine()
    {
        var record = new ErrorRecord
        {
            Message = "boom",
            Operation = "copy-item",
        };
        var s = record.ToString();
        s.Should().Contain("error-id:");
        s.Should().Contain(record.ErrorId.ToString());
    }

    [Fact]
    public void ToString_IsMultiLine_WhenPathAndSuggestionPresent()
    {
        var record = new ErrorRecord
        {
            Message = "boom",
            Operation = "copy-item",
            TargetPath = ItemPath.Parse("fs::C:/tmp/x"),
            Suggestion = "retry",
            Phase = ErrorPhase.Operation,
        };
        var s = record.ToString();
        var lines = s.Split('\n');
        lines.Length.Should().BeGreaterThan(1);
    }
}
