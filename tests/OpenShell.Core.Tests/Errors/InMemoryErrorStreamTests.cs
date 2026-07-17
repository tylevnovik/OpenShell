using FluentAssertions;
using Microsoft.Extensions.Logging;
using OpenShell.Errors;
using OpenShell.Logging;
using Xunit;

namespace OpenShell.Core.Tests.Errors;

/// <summary>
/// InMemoryErrorStream 单元测试。Per ADR-0026, ADR-0031 §7, ADR-0033.
/// 验证 Write 追加 + 容量上限 FIFO + Clear + LastError。
/// </summary>
public class InMemoryErrorStreamTests
{
    private static ErrorRecord MakeRecord(string message = "err")
        => new() { Message = message, Operation = "test", Category = ErrorCategory.Unknown };

    [Fact]
    public void Write_AppendsToRecentErrors()
    {
        var stream = new InMemoryErrorStream();
        var e1 = MakeRecord("first");
        var e2 = MakeRecord("second");

        stream.Write(e1);
        stream.Write(e2);

        stream.RecentErrors.Should().HaveCount(2);
        stream.RecentErrors[0].Should().BeSameAs(e1);
        stream.RecentErrors[1].Should().BeSameAs(e2);
    }

    [Fact]
    public void Write_UpdatesLastError()
    {
        var stream = new InMemoryErrorStream();
        stream.LastError.Should().BeNull();

        var e1 = MakeRecord("first");
        stream.Write(e1);
        stream.LastError.Should().BeSameAs(e1);

        var e2 = MakeRecord("second");
        stream.Write(e2);
        stream.LastError.Should().BeSameAs(e2);
    }

    [Fact]
    public void Write_OverCapacity_DropsOldestFifo()
    {
        var stream = new InMemoryErrorStream { Capacity = 3 };

        var e1 = MakeRecord("1");
        var e2 = MakeRecord("2");
        var e3 = MakeRecord("3");
        var e4 = MakeRecord("4");

        stream.Write(e1);
        stream.Write(e2);
        stream.Write(e3);
        stream.Write(e4);

        stream.RecentErrors.Should().HaveCount(3);
        stream.RecentErrors[0].Should().BeSameAs(e2);
        stream.RecentErrors[1].Should().BeSameAs(e3);
        stream.RecentErrors[2].Should().BeSameAs(e4);
    }

    [Fact]
    public void Write_ExactlyAtCapacity_KeepsAll()
    {
        var stream = new InMemoryErrorStream { Capacity = 3 };
        stream.Write(MakeRecord("1"));
        stream.Write(MakeRecord("2"));
        stream.Write(MakeRecord("3"));
        stream.RecentErrors.Should().HaveCount(3);
    }

    [Fact]
    public void Clear_EmptiesRecentErrors()
    {
        var stream = new InMemoryErrorStream();
        stream.Write(MakeRecord("a"));
        stream.Write(MakeRecord("b"));
        stream.RecentErrors.Should().HaveCount(2);

        stream.Clear();

        stream.RecentErrors.Should().BeEmpty();
    }

    [Fact]
    public void Clear_ResetsLastError()
    {
        var stream = new InMemoryErrorStream();
        stream.Write(MakeRecord("a"));
        stream.LastError.Should().NotBeNull();

        stream.Clear();

        stream.LastError.Should().BeNull();
    }

    [Fact]
    public void Clear_OnEmptyStream_DoesNotThrow()
    {
        var stream = new InMemoryErrorStream();
        var act = () => stream.Clear();
        act.Should().NotThrow();
    }

    [Fact]
    public void ErrorWritten_EventFiresOnWrite()
    {
        var stream = new InMemoryErrorStream();
        ErrorRecord? received = null;
        stream.ErrorWritten += (s, e) => received = e;

        var rec = MakeRecord("event-test");
        stream.Write(rec);

        received.Should().BeSameAs(rec);
    }

    [Fact]
    public void RecentErrors_ReturnsCopy_NotLiveView()
    {
        var stream = new InMemoryErrorStream();
        stream.Write(MakeRecord("a"));
        var snapshot = stream.RecentErrors;
        stream.Write(MakeRecord("b"));

        snapshot.Should().HaveCount(1);
        stream.RecentErrors.Should().HaveCount(2);
    }

    [Fact]
    public void Default_Capacity_Is100()
    {
        var stream = new InMemoryErrorStream();
        stream.Capacity.Should().Be(100);
    }

    [Fact]
    public void Write_WithLogStore_AppendsLogLevelError()
    {
        // ILogStore 联动: Per ADR-0031 §7, 写错误时同步追加 LogLevel=Error 的日志。
        var logStore = new InMemoryLogStore();
        var stream = new InMemoryErrorStream(logStore);

        stream.Write(new ErrorRecord
        {
            Message = "boom",
            Operation = "copy-item",
            Category = ErrorCategory.IOError,
        });

        var recent = logStore.Recent(10);
        recent.Should().HaveCount(1);
        recent[0].Level.Should().Be(LogLevel.Error);
        recent[0].Message.Should().Contain("copy-item");
        recent[0].Message.Should().Contain("boom");
    }
}
