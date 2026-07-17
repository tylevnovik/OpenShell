using System.Runtime.CompilerServices;
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
using OpenShell.Variables;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

/// <summary>
/// <c>Tee-Object</c> unit tests. Per ADR-0048 §1.8.
/// 验证透传 + 文件写入 + 变量写入、-Append 选项、空输入行为。
/// </summary>
public class TeeObjectCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ThrowsBecausePipelineOnly()
    {
        var cmd = new TeeObjectCommand();
        var args = new TeeObjectCommand.Args();
        var ctx = TestCtx();

        var act = async () =>
        {
            var results = new List<IItem>();
            await foreach (var item in cmd.ExecuteAsync(args, ctx, default))
                results.Add(item);
        };

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Transform_PassThrough_YieldsAllInput()
    {
        var cmd = new TeeObjectCommand();
        var args = new TeeObjectCommand.Args();
        var ctx = TestCtx();
        var input = Items(
            Make("a.txt", value: "first"),
            Make("b.txt", value: "second"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(2);
        results[0].Name.Should().Be("a.txt");
        results[1].Name.Should().Be("b.txt");
    }

    [Fact]
    public async Task Transform_FilePath_WritesContentToFile()
    {
        using var temp = new TempDir();
        var outPath = temp.GetFullPath("tee.txt");

        var cmd = new TeeObjectCommand();
        var args = new TeeObjectCommand.Args(FilePath: ItemPath.Parse("fs::" + outPath.Replace('\\', '/')));
        var ctx = TestCtx();
        var input = Items(
            Make("a.txt", value: "first"),
            Make("b.txt", value: "second"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        // 透传到下游。
        results.Should().HaveCount(2);
        // 同时写入文件。
        File.Exists(outPath).Should().BeTrue();
        var written = File.ReadAllLines(outPath);
        written.Should().HaveCount(2);
        written[0].Should().Be("first");
        written[1].Should().Be("second");
    }

    [Fact]
    public async Task Transform_Append_AppendsToExistingFile()
    {
        using var temp = new TempDir();
        var outPath = temp.GetFullPath("tee.txt");
        File.WriteAllLines(outPath, new[] { "preexisting" });

        var cmd = new TeeObjectCommand();
        var args = new TeeObjectCommand.Args(
            FilePath: ItemPath.Parse("fs::" + outPath.Replace('\\', '/')),
            Append: true);
        var ctx = TestCtx();
        var input = Items(Make("a.txt", value: "new"));

        await foreach (var _ in cmd.Transform(input, args, ctx, default)) { }

        var written = File.ReadAllLines(outPath);
        // 原行 + 新行 = 2 行。
        written.Should().HaveCount(2);
        written[0].Should().Be("preexisting");
        written[1].Should().Be("new");
    }

    [Fact]
    public async Task Transform_Variable_StoresItemsInVariable()
    {
        var variables = new InMemoryVariableRegistry();
        var cmd = new TeeObjectCommand();
        var args = new TeeObjectCommand.Args(Variable: "captured");
        var ctx = TestCtx(variables: variables);
        var input = Items(
            Make("a.txt", value: "first"),
            Make("b.txt", value: "second"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        // 变量中应包含 2 个 IItem。
        var stored = variables.Resolve("captured", VariableScope.Global);
        stored.Should().BeAssignableTo<System.Collections.IEnumerable>();
        var storedItems = ((System.Collections.IEnumerable)stored!).Cast<IItem>().ToList();
        storedItems.Should().HaveCount(2);
    }

    [Fact]
    public async Task Transform_FileAndVariable_WritesBoth()
    {
        using var temp = new TempDir();
        var outPath = temp.GetFullPath("tee.txt");
        var variables = new InMemoryVariableRegistry();

        var cmd = new TeeObjectCommand();
        var args = new TeeObjectCommand.Args(
            FilePath: ItemPath.Parse("fs::" + outPath.Replace('\\', '/')),
            Variable: "captured");
        var ctx = TestCtx(variables: variables);
        var input = Items(Make("a.txt", value: "v"));

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(input, args, ctx, default))
            results.Add(item);

        results.Should().HaveCount(1);
        File.Exists(outPath).Should().BeTrue();
        var stored = variables.Resolve("captured", VariableScope.Global);
        stored.Should().NotBeNull();
    }

    [Fact]
    public async Task Transform_EmptyInput_YieldsNothing()
    {
        using var temp = new TempDir();
        var outPath = temp.GetFullPath("empty.txt");

        var cmd = new TeeObjectCommand();
        var args = new TeeObjectCommand.Args(FilePath: ItemPath.Parse("fs::" + outPath.Replace('\\', '/')));
        var ctx = TestCtx();

        var results = new List<IItem>();
        await foreach (var item in cmd.Transform(Items(), args, ctx, default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_UsesItemValueWhenAvailable()
    {
        using var temp = new TempDir();
        var outPath = temp.GetFullPath("val.txt");

        var cmd = new TeeObjectCommand();
        var args = new TeeObjectCommand.Args(FilePath: ItemPath.Parse("fs::" + outPath.Replace('\\', '/')));
        var ctx = TestCtx();
        // 无 Value 属性时，使用 item.Name 作为写入字符串。
        var input = Items(new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = "nameonly.txt" },
            Kind = ItemKind.File,
        });

        await foreach (var _ in cmd.Transform(input, args, ctx, default)) { }

        var written = File.ReadAllText(outPath).TrimEnd('\r', '\n');
        written.Should().Be("nameonly.txt");
    }

    private static IItem Make(string name, object? value = null)
        => new Item
        {
            Path = new ItemPath { Provider = "fs", InternalPath = name },
            Kind = ItemKind.File,
            Properties = value is null
                ? PropertyBag.Empty
                : PropertyBag.Empty.With("Value", value),
        };

    private static async IAsyncEnumerable<IItem> Items(params IItem[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private static CommandContext TestCtx(InMemoryVariableRegistry? variables = null)
    {
        return new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = new NopHost(),
            CurrentLocation = ItemPath.Parse("fs::/"),
            Errors = new InMemoryErrorStream(),
            Variables = variables ?? new InMemoryVariableRegistry(),
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
