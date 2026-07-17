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
/// <c>Add-Content</c> unit tests. Per ADR-0048 §5.3.
/// 验证追加写入、-NoNewline 选项、文件不存在则创建、-Force 覆盖只读。
/// </summary>
public class AddContentCommandTests
{
    [Fact]
    public async Task Execute_CreatesFile_IfNotExists()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("new.txt");

        var cmd = new AddContentCommand();
        var args = new AddContentCommand.Args(
            Path: ItemPath.Parse("fs::" + path.Replace('\\', '/')),
            Value: "hello");
        var ctx = TestCtx();

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Be("hello" + Environment.NewLine);
    }

    [Fact]
    public async Task Execute_AppendsToExistingFile()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("append.txt");
        File.WriteAllText(path, "first" + Environment.NewLine);

        var cmd = new AddContentCommand();
        var args = new AddContentCommand.Args(
            Path: ItemPath.Parse("fs::" + path.Replace('\\', '/')),
            Value: "second");
        var ctx = TestCtx();

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        var lines = File.ReadAllLines(path);
        lines.Should().HaveCount(2);
        lines[0].Should().Be("first");
        lines[1].Should().Be("second");
    }

    [Fact]
    public async Task Execute_NoNewline_DoesNotAppendNewline()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("nonl.txt");

        var cmd = new AddContentCommand();
        var args = new AddContentCommand.Args(
            Path: ItemPath.Parse("fs::" + path.Replace('\\', '/')),
            Value: "raw",
            NoNewline: true);
        var ctx = TestCtx();

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        File.ReadAllText(path).Should().Be("raw");
    }

    [Fact]
    public async Task Execute_Force_OverwritesReadOnlyAttribute()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("readonly.txt");
        File.WriteAllText(path, "preexisting");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var cmd = new AddContentCommand();
        var args = new AddContentCommand.Args(
            Path: ItemPath.Parse("fs::" + path.Replace('\\', '/')),
            Value: "appended",
            Force: true);
        var ctx = TestCtx();

        try
        {
            await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

            // 验证写入成功且只读属性被去掉。
            var attrs = File.GetAttributes(path);
            (attrs & FileAttributes.ReadOnly).Should().Be(FileAttributes.None);
            File.ReadAllText(path).Should().Contain("appended");
        }
        finally
        {
            // 测试清理：去掉只读属性。
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public async Task Execute_NonFsProvider_WritesCapabilityNotSupported()
    {
        var cmd = new AddContentCommand();
        var args = new AddContentCommand.Args(
            Path: ItemPath.Parse("zip::archive.zip/file.txt"),
            Value: "v");
        var ctx = TestCtx();

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        ctx.Errors!.LastError.Should().NotBeNull();
        ctx.Errors!.LastError!.Category.Should().Be(ErrorCategory.CapabilityNotSupported);
    }

    [Fact]
    public async Task Execute_WritesMessageToHost()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("msg.txt");
        var host = new CapturingHost();

        var cmd = new AddContentCommand();
        var args = new AddContentCommand.Args(
            Path: ItemPath.Parse("fs::" + path.Replace('\\', '/')),
            Value: "x");
        var ctx = TestCtx(host);

        await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        host.CapturedOutput.Should().ContainMatch("*chars*");
    }

    [Fact]
    public async Task Execute_AppendsMultipleTimes()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("multi.txt");
        var cmd = new AddContentCommand();
        var args = new AddContentCommand.Args(
            Path: ItemPath.Parse("fs::" + path.Replace('\\', '/')),
            Value: "line");
        var ctx = TestCtx();

        // 连续追加 3 次。
        for (int i = 0; i < 3; i++)
            await foreach (var _ in cmd.ExecuteAsync(args, ctx, default)) { }

        var lines = File.ReadAllLines(path);
        lines.Should().HaveCount(3);
        lines.Should().AllBe("line");
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

    /// <summary>捕获 WriteOutputLineAsync 输出的 Host，便于断言。</summary>
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
