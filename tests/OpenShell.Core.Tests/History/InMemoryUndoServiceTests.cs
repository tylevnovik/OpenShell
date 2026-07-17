using FluentAssertions;
using NSubstitute;
using OpenShell.Errors;
using OpenShell.History;
using OpenShell.Operations;
using OpenShell.Paths;
using Xunit;

namespace OpenShell.Core.Tests.History;

/// <summary>
/// InMemoryUndoService 单元测试。Per ADR-0020, ADR-0033.
/// 验证双栈管理 + Push/Undo/Redo 基本流程, 用 NSubstitute mock 依赖。
/// </summary>
public class InMemoryUndoServiceTests
{
    private static OperationJournalEntry MakeEntry(
        string operation = "copy",
        string? undoOp = "delete")
    {
        var src = ItemPath.Parse("fs::C:/src");
        var dst = ItemPath.Parse("fs::C:/dst");
        return new OperationJournalEntry
        {
            Operation = operation,
            Sources = new[] { src },
            Destinations = new[] { dst },
            Undo = undoOp is null ? null : new UndoInfo
            {
                UndoOperation = undoOp,
                UndoParameters = new Dictionary<string, string>
                {
                    ["path"] = dst.Display,
                },
            },
        };
    }

    private static (InMemoryUndoService svc, IOperationEngine engine, IOperationJournal journal, ITrashService trash)
        CreateService()
    {
        var engine = Substitute.For<IOperationEngine>();
        var journal = Substitute.For<IOperationJournal>();
        var trash = Substitute.For<ITrashService>();

        // ReadRecentAsync 默认返回空。
        journal.ReadRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<OperationJournalEntry>>(Array.Empty<OperationJournalEntry>()));

        // DeleteAsync / MoveAsync / RenameAsync 默认返回成功。
        engine.DeleteAsync(Arg.Any<ItemPath>(), Arg.Any<DeleteOptions>(), Arg.Any<IProgress<OperationProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(OperationResult.Successful(1, 0)));
        engine.MoveAsync(Arg.Any<ItemPath>(), Arg.Any<ItemPath>(), Arg.Any<MoveOptions>(), Arg.Any<IProgress<OperationProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(OperationResult.Successful(1, 0)));
        engine.RenameAsync(Arg.Any<ItemPath>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(OperationResult.Successful(1, 0)));
        engine.CopyAsync(Arg.Any<ItemPath>(), Arg.Any<ItemPath>(), Arg.Any<CopyOptions>(), Arg.Any<IProgress<OperationProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(OperationResult.Successful(1, 0)));
        engine.TouchAsync(Arg.Any<ItemPath>(), Arg.Any<TouchOptions>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(OperationResult.Successful(1, 0)));
        engine.CreateDirectoryAsync(Arg.Any<ItemPath>(), Arg.Any<CreateDirectoryOptions>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(OperationResult.Successful(1, 0)));

        trash.RestoreAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var svc = new InMemoryUndoService(engine, journal, trash);
        return (svc, engine, journal, trash);
    }

    [Fact]
    public void CanUndo_False_WhenEmpty()
    {
        var (svc, _, _, _) = CreateService();
        svc.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void CanRedo_False_WhenEmpty()
    {
        var (svc, _, _, _) = CreateService();
        svc.CanRedo.Should().BeFalse();
    }

    [Fact]
    public void Push_MakesCanUndoTrue()
    {
        var (svc, _, _, _) = CreateService();
        svc.Push(MakeEntry());
        svc.CanUndo.Should().BeTrue();
        svc.UndoStack.Should().HaveCount(1);
    }

    [Fact]
    public async Task Push_ClearsRedoStack()
    {
        var (svc, _, _, _) = CreateService();
        // Pre-condition: push and undo to populate redo stack.
        svc.Push(MakeEntry());
        await svc.UndoAsync();
        svc.RedoStack.Should().HaveCount(1);

        // Act: a new push clears the redo stack (ADR-0020 §8).
        svc.Push(MakeEntry(operation: "mkdir"));

        svc.RedoStack.Should().BeEmpty();
        svc.UndoStack.Should().HaveCount(1);
    }

    [Fact]
    public async Task UndoAsync_OnEmptyStack_ReturnsNull()
    {
        var (svc, _, _, _) = CreateService();
        var result = await svc.UndoAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task UndoAsync_RemovesFromUndoStack_AndPushesToRedoStack()
    {
        var (svc, _, _, _) = CreateService();
        var entry = MakeEntry();
        svc.Push(entry);

        var undone = await svc.UndoAsync();

        undone.Should().NotBeNull();
        undone!.EntryId.Should().Be(entry.EntryId);
        svc.UndoStack.Should().BeEmpty();
        svc.RedoStack.Should().HaveCount(1);
        svc.CanUndo.Should().BeFalse();
        svc.CanRedo.Should().BeTrue();
    }

    [Fact]
    public async Task UndoAsync_InvokesReverseOperationDelete()
    {
        var (svc, engine, _, _) = CreateService();
        var entry = MakeEntry(operation: "copy", undoOp: "delete");
        svc.Push(entry);

        await svc.UndoAsync();

        // 反向操作: 删除 destination。
        await engine.Received(1).DeleteAsync(
            Arg.Any<ItemPath>(),
            Arg.Is<DeleteOptions>(o => o.UseTrash == false && o.Recurse == true),
            Arg.Any<IProgress<OperationProgress>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UndoAsync_InvokesJournalMarkUndone()
    {
        var (svc, _, journal, _) = CreateService();
        var entry = MakeEntry();
        svc.Push(entry);

        await svc.UndoAsync();

        await journal.Received(1).MarkUndoneAsync(entry.EntryId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RedoAsync_OnEmptyStack_ReturnsNull()
    {
        var (svc, _, _, _) = CreateService();
        var result = await svc.RedoAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task RedoAsync_AfterUndo_MovesEntryBackToUndoStack()
    {
        var (svc, _, _, _) = CreateService();
        var entry = MakeEntry(operation: "copy", undoOp: "delete");
        svc.Push(entry);
        await svc.UndoAsync();

        var redone = await svc.RedoAsync();

        redone.Should().NotBeNull();
        redone!.EntryId.Should().Be(entry.EntryId);
        svc.RedoStack.Should().BeEmpty();
        svc.UndoStack.Should().HaveCount(1);
        svc.CanUndo.Should().BeTrue();
        svc.CanRedo.Should().BeFalse();
    }

    [Fact]
    public async Task RedoAsync_InvokesJournalMarkRedone()
    {
        var (svc, _, journal, _) = CreateService();
        var entry = MakeEntry();
        svc.Push(entry);
        await svc.UndoAsync();

        await svc.RedoAsync();

        await journal.Received(1).MarkRedoneAsync(entry.EntryId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Clear_EmptiesBothStacks()
    {
        var (svc, _, _, _) = CreateService();
        svc.Push(MakeEntry());
        svc.Push(MakeEntry());

        svc.Clear();

        svc.UndoStack.Should().BeEmpty();
        svc.RedoStack.Should().BeEmpty();
        svc.CanUndo.Should().BeFalse();
        svc.CanRedo.Should().BeFalse();
    }

    [Fact]
    public async Task UndoAsync_SkipsEntriesWithNullUndo()
    {
        var (svc, _, _, _) = CreateService();
        var entry = MakeEntry(undoOp: null);
        svc.Push(entry);

        var undone = await svc.UndoAsync();

        // Undo=null 不可逆, 但仍需从栈中移除。
        undone.Should().BeNull();
        svc.UndoStack.Should().BeEmpty();
        svc.RedoStack.Should().BeEmpty();
    }

    [Fact]
    public async Task UndoAsync_Failure_StopsAndWritesError()
    {
        // engine.DeleteAsync 失败时, Undo 应停止并返回已 undone 的最后一条。
        var engine = Substitute.For<IOperationEngine>();
        var journal = Substitute.For<IOperationJournal>();
        var trash = Substitute.For<ITrashService>();
        var errorStream = new InMemoryErrorStream();

        journal.ReadRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<OperationJournalEntry>>(Array.Empty<OperationJournalEntry>()));
        engine.DeleteAsync(Arg.Any<ItemPath>(), Arg.Any<DeleteOptions>(), Arg.Any<IProgress<OperationProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(OperationResult.Failed("delete", "boom", new InvalidOperationException("boom"))));

        var svc = new InMemoryUndoService(engine, journal, trash, errorStream);
        var entry = MakeEntry();
        svc.Push(entry);

        var undone = await svc.UndoAsync();

        // Undo 失败时返回 null, 写一条 ErrorRecord 到错误流。
        undone.Should().BeNull();
        errorStream.LastError.Should().NotBeNull();
        errorStream.LastError!.Operation.Should().Be("undo");
    }
}
