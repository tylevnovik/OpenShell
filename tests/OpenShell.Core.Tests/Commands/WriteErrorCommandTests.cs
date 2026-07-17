using System.Runtime.CompilerServices;
using FluentAssertions;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Providers;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// <c>Write-Error</c> unit tests. Per ADR-0048 §2.3 / ADR-0026.
/// Verifies that an <see cref="ErrorRecord"/> is written to <see cref="IErrorStream"/>
/// with the expected message, category, and target path, and that nothing is yielded
/// to the success stream.
/// </summary>
public class WriteErrorCommandTests
{
    [Fact]
    public async Task Execute_WritesErrorRecord()
    {
        var errors = new InMemoryErrorStream();
        var cmd = new WriteErrorCommand();
        var args = new WriteErrorCommand.Args(Message: "boom");
        var ctx = TestCtx(errors);

        var results = new List<IItem>();
        await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
        errors.LastError.Should().NotBeNull();
        errors.LastError!.Message.Should().Be("boom");
    }

    [Fact]
    public async Task Execute_NullMessage_FallsBackToPlaceholder()
    {
        var errors = new InMemoryErrorStream();
        var cmd = new WriteErrorCommand();
        var args = new WriteErrorCommand.Args(Message: null);
        var ctx = TestCtx(errors);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        errors.LastError.Should().NotBeNull();
        errors.LastError!.Message.Should().Contain("no message");
    }

    [Fact]
    public async Task Execute_Category_ParsedIntoRecord()
    {
        var errors = new InMemoryErrorStream();
        var cmd = new WriteErrorCommand();
        var args = new WriteErrorCommand.Args(Message: "x", Category: "ItemNotFound");
        var ctx = TestCtx(errors);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        errors.LastError!.Category.Should().Be(ErrorCategory.ItemNotFound);
    }

    [Fact]
    public async Task Execute_UnknownCategory_FallsBackToUnknown()
    {
        var errors = new InMemoryErrorStream();
        var cmd = new WriteErrorCommand();
        var args = new WriteErrorCommand.Args(Message: "x", Category: "BogusCategory");
        var ctx = TestCtx(errors);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        errors.LastError!.Category.Should().Be(ErrorCategory.Unknown);
    }

    [Fact]
    public async Task Execute_TargetPath_ParsedIntoRecord()
    {
        var errors = new InMemoryErrorStream();
        var cmd = new WriteErrorCommand();
        var args = new WriteErrorCommand.Args(Message: "x", TargetPath: "fs::C:/missing");
        var ctx = TestCtx(errors);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        errors.LastError!.TargetPath.Should().NotBeNull();
        errors.LastError!.TargetPath!.Value.Display.Should().Be("fs::C:/missing");
    }

    [Fact]
    public async Task Execute_Suggestion_PreservedInRecord()
    {
        var errors = new InMemoryErrorStream();
        var cmd = new WriteErrorCommand();
        var args = new WriteErrorCommand.Args(Message: "x", Suggestion: "Check the path");
        var ctx = TestCtx(errors);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        errors.LastError!.Suggestion.Should().Be("Check the path");
    }

    [Fact]
    public async Task Execute_Operation_IsWriteError()
    {
        var errors = new InMemoryErrorStream();
        var cmd = new WriteErrorCommand();
        var args = new WriteErrorCommand.Args(Message: "x");
        var ctx = TestCtx(errors);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        errors.LastError!.Operation.Should().Be("write-error");
        errors.LastError!.Phase.Should().Be(ErrorPhase.Operation);
    }

    [Fact]
    public async Task Execute_YieldsNothing_DoesNotEnterSuccessStream()
    {
        var errors = new InMemoryErrorStream();
        var cmd = new WriteErrorCommand();
        var args = new WriteErrorCommand.Args(Message: "ignored");
        var ctx = TestCtx(errors);

        var count = 0;
        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default))
            count++;

        count.Should().Be(0);
    }

    private static CommandContext TestCtx(InMemoryErrorStream errors)
    {
        return new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = new NopHost(),
            CurrentLocation = ItemPath.Parse("fs::/"),
            Errors = errors,
        };
    }

    private sealed class NopHost : OpenShell.IHost
    {
        public HostKind Kind => HostKind.Cli;
        public ItemPath CurrentLocation { get; set; } = ItemPath.Parse("fs::/");
        public IObservable<IReadOnlyList<IItem>> Selection => new EmptyObs<IReadOnlyList<IItem>>();
        public IProgress<OperationProgress> Progress => new Progress<OperationProgress>(_ => { });
        public IServiceProvider Services => new EmptyServiceProvider();
        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyObs<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) { observer.OnCompleted(); return new Disp(); }
    }

    private sealed class Disp : IDisposable { public void Dispose() { } }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
