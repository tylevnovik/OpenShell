using System.Runtime.CompilerServices;
using FluentAssertions;
using OpenShell;
using OpenShell.Commands;
using OpenShell.Items;
using OpenShell.Operations;
using OpenShell.Paths;
using OpenShell.Pipeline;
using OpenShell.Providers;
using Xunit;

namespace OpenShell.Core.Tests.Pipeline;

/// <summary>
/// PipelineExecutor 单元测试。Per ADR-0010, ADR-0033.
/// 用真实 stub 命令类 (带 [Verb] 标注) + 真实 CommandRegistry, 验证 TryExecuteAsync 单段/多段/Source|Transform|Sink 反射调用。
/// </summary>
public class PipelineExecutorTests
{
    [Fact]
    public async Task TryExecuteAsync_SingleCommand_ReturnsFalse()
    {
        // 不含 | 的命令行不进入 pipeline 路径, 返回 false 让 host 用普通路径执行。
        var (executor, _) = BuildExecutor();
        var ctx = BuildContext();

        var executed = await executor.TryExecuteAsync(
            "stub-source",
            () => ctx,
            (c, items) => Task.CompletedTask);

        executed.Should().BeFalse();
    }

    [Fact]
    public async Task TryExecuteAsync_EmptyLine_ReturnsFalse()
    {
        var (executor, _) = BuildExecutor();
        var ctx = BuildContext();

        var executed = await executor.TryExecuteAsync(
            "",
            () => ctx,
            (c, items) => Task.CompletedTask);

        executed.Should().BeFalse();
    }

    [Fact]
    public async Task TryExecuteAsync_TwoSegments_SourceAndDefaultSink_ProducesItems()
    {
        var (executor, _) = BuildExecutor();
        var ctx = BuildContext();
        StubSource.Reset(new[] { "alpha", "beta" });

        var collected = new List<IItem>();
        var executed = await executor.TryExecuteAsync(
            "stub-source | stub-source",
            () => ctx,
            (c, items) => CollectAsync(items, collected));

        executed.Should().BeTrue();
        // Source | Source: 第一段产出, 末段是 Source 类型, 走 default sink, 收到 2 个 item。
        collected.Should().HaveCount(2);
    }

    [Fact]
    public async Task TryExecuteAsync_SourceTransformSink_AllInvoked()
    {
        var (executor, _) = BuildExecutor();
        var ctx = BuildContext();
        StubSource.Reset(new[] { "alpha", "beta", "gamma" });
        StubTransform.Reset();
        StubSink.Reset();

        var executed = await executor.TryExecuteAsync(
            "stub-source | stub-transform | stub-sink",
            () => ctx,
            (c, items) => Task.CompletedTask);

        executed.Should().BeTrue();
        StubSource.InvokeCount.Should().BeGreaterThan(0);
        StubTransform.TransformCount.Should().Be(3);
        StubSink.ConsumeCount.Should().Be(3);
    }

    [Fact]
    public async Task TryExecuteAsync_SourceTransformSink_OrdersItemsCorrectly()
    {
        var (executor, _) = BuildExecutor();
        var ctx = BuildContext();
        StubSource.Reset(new[] { "alpha", "beta", "gamma" });
        StubTransform.Reset();
        StubSink.Reset();

        await executor.TryExecuteAsync(
            "stub-source | stub-transform | stub-sink",
            () => ctx,
            (c, items) => Task.CompletedTask);

        StubSink.ReceivedItems.Should().HaveCount(3);
        StubSink.ReceivedItems[0].Name.Should().Be("alpha");
        StubSink.ReceivedItems[2].Name.Should().Be("gamma");
    }

    [Fact]
    public async Task TryExecuteAsync_SourceTransformDefaultSink_AppliesTransform()
    {
        var (executor, _) = BuildExecutor();
        var ctx = BuildContext();
        StubSource.Reset(new[] { "alpha" });
        StubTransform.Reset();

        var collected = new List<IItem>();
        var executed = await executor.TryExecuteAsync(
            "stub-source | stub-transform",
            () => ctx,
            (c, items) => CollectAsync(items, collected));

        executed.Should().BeTrue();
        collected.Should().HaveCount(1);
        StubTransform.TransformCount.Should().Be(1);
    }

    [Fact]
    public async Task TryExecuteAsync_UnknownCommand_ThrowsInvalidOperationException()
    {
        var (executor, _) = BuildExecutor();
        var ctx = BuildContext();

        var act = async () => await executor.TryExecuteAsync(
            "stub-source | nonexistent-command",
            () => ctx,
            (c, items) => Task.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*command not found in pipeline*");
    }

    [Fact]
    public async Task TryExecuteAsync_WithArgs_PassesPositionalParameter()
    {
        var (executor, _) = BuildExecutor();
        var ctx = BuildContext();
        StubSource.Reset(new[] { "x" });
        StubTransform.Reset();

        var collected = new List<IItem>();
        // StubTransform 的 Expression 是 Position=0: 通过位置参数传递。
        await executor.TryExecuteAsync(
            "stub-source | stub-transform my-expr",
            () => ctx,
            (c, items) => CollectAsync(items, collected));

        StubTransform.LastArgs?.Expression.Should().Be("my-expr");
    }

    [Fact]
    public async Task TryExecuteAsync_SourceAndDefaultSink_StreamsAllItems()
    {
        var (executor, _) = BuildExecutor();
        var ctx = BuildContext();
        StubSource.Reset(new[] { "1", "2", "3", "4" });

        var collected = new List<IItem>();
        await executor.TryExecuteAsync(
            "stub-source | stub-source",
            () => ctx,
            (c, items) => CollectAsync(items, collected));

        collected.Should().HaveCount(4);
        collected.Select(i => i.Name).Should().BeEquivalentTo(new[] { "1", "2", "3", "4" });
    }

    private static (PipelineExecutor executor, CommandRegistry commands) BuildExecutor()
    {
        var commands = new CommandRegistry();
        commands.Register(CommandDescriptor.FromType(typeof(StubSource)));
        commands.Register(CommandDescriptor.FromType(typeof(StubTransform)));
        commands.Register(CommandDescriptor.FromType(typeof(StubSink)));
        return (new PipelineExecutor(commands), commands);
    }

    private static CommandContext BuildContext()
    {
        return new CommandContext
        {
            Providers = new TestProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = new NopHost(),
            CurrentLocation = ItemPath.Parse("fs::/"),
        };
    }

    private static async Task CollectAsync(IAsyncEnumerable<IItem> items, List<IItem> sink)
    {
        await foreach (var item in items)
            sink.Add(item);
    }

    // ==== Stub commands: 必须有 [Verb] + 嵌套 Args record + 无参构造 ====

    [Verb("Stub", Noun = "Source", Aliases = new[] { "src" })]
    public sealed class StubSource : ICommand<StubSource.Args>, IPipelineSource
    {
        private static IReadOnlyList<string> _items = Array.Empty<string>();

        public static void Reset(IReadOnlyList<string> items)
        {
            _items = items;
            InvokeCount = 0;
        }

        public static int InvokeCount;

        public record Args();

        public async IAsyncEnumerable<IItem> ExecuteAsync(
            Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
        {
            InvokeCount++;
            foreach (var name in _items)
            {
                ct.ThrowIfCancellationRequested();
                yield return Item.File(ItemPath.Parse($"fs::/{name}"));
            }
            await Task.CompletedTask;
        }
    }

    [Verb("Stub", Noun = "Transform", Aliases = new[] { "tform" })]
    public sealed class StubTransform : IPipelineTransform<StubTransform.Args>
    {
        public static int TransformCount;
        public static Args? LastArgs;

        public static void Reset()
        {
            TransformCount = 0;
            LastArgs = null;
        }

        public record Args(
            [property: Parameter(Position = 0)] string Expression = "");

        public async IAsyncEnumerable<IItem> Transform(
            IAsyncEnumerable<IItem> input,
            Args args,
            CommandContext ctx,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastArgs = args;
            await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
            {
                TransformCount++;
                yield return item;
            }
        }

        public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("StubTransform is pipeline-only");
    }

    [Verb("Stub", Noun = "Sink", Aliases = new[] { "sink" })]
    public sealed class StubSink : IPipelineSink<StubSink.Args>
    {
        public static int ConsumeCount;
        public static List<IItem> ReceivedItems = new();

        public static void Reset()
        {
            ConsumeCount = 0;
            ReceivedItems.Clear();
        }

        public record Args();

        public async ValueTask Consume(
            IAsyncEnumerable<IItem> input,
            Args args,
            CommandContext ctx,
            CancellationToken cancellationToken = default)
        {
            await foreach (var item in input.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                ConsumeCount++;
                ReceivedItems.Add(item);
            }
        }

        public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("StubSink is pipeline-only");
    }

    // ==== Minimal helpers (no file system / DI dependency) ====

    private sealed class TestProviderRegistry : IProviderRegistry
    {
        public IReadOnlyCollection<ProviderInfo> Registered => Array.Empty<ProviderInfo>();
        public void Register(IProvider provider) { }
        public bool Unregister(string providerName) => false;
        public IProvider Get(string providerName) => throw new InvalidOperationException();
        public bool TryGet(string providerName, out IProvider? provider) { provider = null; return false; }
        public T? Resolve<T>(string providerName) where T : class => null;
        public IProvider ResolveProvider(ItemPath path) => throw new InvalidOperationException();
        public T? ResolveCapability<T>(ItemPath path) where T : class => null;
    }

    private sealed class NopHost : IHost
    {
        public HostKind Kind => HostKind.Cli;
        public ItemPath CurrentLocation { get; set; } = ItemPath.Parse("fs::/");
        public IObservable<IReadOnlyList<IItem>> Selection => new EmptyObs<IReadOnlyList<IItem>>();
        public IProgress<OperationProgress> Progress => new Progress<OperationProgress>(_ => { });
        public IServiceProvider Services => throw new NotSupportedException();
        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyObs<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            observer.OnCompleted();
            return new Disp();
        }
    }

    private sealed class Disp : IDisposable
    {
        public void Dispose() { }
    }
}
