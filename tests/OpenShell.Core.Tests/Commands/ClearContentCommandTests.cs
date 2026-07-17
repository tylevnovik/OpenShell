using FluentAssertions;
using OpenShell;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;
using OpenShell.Providers;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// <c>Clear-Content</c> unit tests. Per ADR-0048 §5.4.
/// 验证文件内容截断为 0 字节、保留文件元数据、文件不存在错误、-Force 覆盖只读。
/// </summary>
public class ClearContentCommandTests
{
    [Fact]
    public async Task Execute_TruncatesExistingFile_ToZeroBytes()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("clear.txt");
        File.WriteAllText(path, "some content to clear");

        var cmd = new ClearContentCommand();
        var args = new ClearContentCommand.Args(
            Path: ItemPath.Parse("fs::" + path.Replace('\\', '/')));
        var ctx = TestCtx();

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        // 内容截断为 0 字节，但文件仍存在。
        File.Exists(path).Should().BeTrue();
        new FileInfo(path).Length.Should().Be(0);
    }

    [Fact]
    public async Task Execute_KeepsFileMetadata()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("keep.txt");
        File.WriteAllText(path, "data");
        var createdBefore = File.GetCreationTime(path);

        var cmd = new ClearContentCommand();
        var args = new ClearContentCommand.Args(
            Path: ItemPath.Parse("fs::" + path.Replace('\\', '/')));
        var ctx = TestCtx();

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        // 文件仍存在（元数据保留）。
        File.Exists(path).Should().BeTrue();
        File.GetCreationTime(path).Should().Be(createdBefore);
    }

    [Fact]
    public async Task Execute_FileNotFound_WritesItemNotFound()
    {
        var cmd = new ClearContentCommand();
        var args = new ClearContentCommand.Args(
            Path: ItemPath.Parse("fs::C:/no/such/file.txt"));
        var ctx = TestCtx();

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.ItemNotFound);
    }

    [Fact]
    public async Task Execute_NonFsProviderWithoutCapability_WritesCapabilityNotSupported()
    {
        var cmd = new ClearContentCommand();
        var args = new ClearContentCommand.Args(
            Path: ItemPath.Parse("zip::archive.zip/file.txt"));
        var ctx = TestCtx();

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.CapabilityNotSupported);
    }

    [Fact]
    public async Task Execute_Force_OvercomesReadOnly()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("ro.txt");
        File.WriteAllText(path, "data");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var cmd = new ClearContentCommand();
        var args = new ClearContentCommand.Args(
            Path: ItemPath.Parse("fs::" + path.Replace('\\', '/')),
            Force: true);
        var ctx = TestCtx();

        try
        {
            await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

            new FileInfo(path).Length.Should().Be(0);
            // 只读属性被去掉。
            var attrs = File.GetAttributes(path);
            (attrs & FileAttributes.ReadOnly).Should().Be(FileAttributes.None);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public async Task Execute_WritesConfirmationToHost()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("msg.txt");
        File.WriteAllText(path, "x");
        var host = new CapturingHost();

        var cmd = new ClearContentCommand();
        var args = new ClearContentCommand.Args(
            Path: ItemPath.Parse("fs::" + path.Replace('\\', '/')));
        var ctx = TestCtx(host);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        host.CapturedOutput.Should().ContainMatch("*Cleared*");
    }

    [Fact]
    public async Task Execute_OnEmptyFile_RemainsEmpty()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("empty.txt");
        File.WriteAllText(path, "");

        var cmd = new ClearContentCommand();
        var args = new ClearContentCommand.Args(
            Path: ItemPath.Parse("fs::" + path.Replace('\\', '/')));
        var ctx = TestCtx();

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        new FileInfo(path).Length.Should().Be(0);
        File.Exists(path).Should().BeTrue();
    }

    private static CommandContext TestCtx(OpenShell.IHost? host = null)
    {
        return new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = host ?? new NopHost(),
            CurrentLocation = ItemPath.Parse("fs::/"),
            Errors = new InMemoryErrorStream(),
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

    private sealed class CapturingHost : OpenShell.IHost
    {
        private readonly List<string> _output = new();
        public HostKind Kind => HostKind.Cli;
        public ItemPath CurrentLocation { get; set; } = ItemPath.Parse("fs::/");
        public IObservable<IReadOnlyList<IItem>> Selection => new EmptyObs<IReadOnlyList<IItem>>();
        public IProgress<OperationProgress> Progress => new Progress<OperationProgress>(_ => { });
        public IServiceProvider Services => new EmptyServiceProvider();
        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
        {
            _output.Add(line);
            return Task.CompletedTask;
        }
        public Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IReadOnlyList<string> CapturedOutput => _output;
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
