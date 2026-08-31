using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenShell.Commands;
using OpenShell.Commands.Builtins;
using OpenShell.Items;
using OpenShell.Preview;
using OpenShell.Paths;
using OpenShell.Providers;
using OpenShell.TestUtils;
using System.Reactive.Linq;
using Xunit;

namespace OpenShell.Core.Tests.Commands;

public sealed class GlobalSearchCommandTests
{
    [Fact]
    public async Task ExecuteAsync_UsesReadyIndexAndHonorsPathScope()
    {
        using var temp = new TempDir();
        using var store = new FileIndexStore(temp.GetFullPath("index/files.db"));
        var root = temp.GetFullPath("one");
        var inScope = Path.Combine(root, "report.txt");
        var outOfScope = temp.GetFullPath("two/report.txt");
        store.Upsert(inScope, "report.txt", 10, 10);
        store.Upsert(outOfScope, "report.txt", 20, 20);

        using var services = new ServiceCollection()
            .AddSingleton(store)
            .BuildServiceProvider();
        var location = new ItemPath { Provider = "fs", InternalPath = root.Replace('\\', '/') };
        var host = new StubHost(location, services);
        var context = new CommandContext
        {
            Providers = new ProviderRegistry(),
            Commands = new CommandRegistry(),
            Host = host,
            CurrentLocation = location,
        };

        var results = new List<IItem>();
        await foreach (var item in new GlobalSearchCommand().ExecuteAsync(
            new GlobalSearchCommand.Args("report", location, IncludeContents: false, MaxResults: 10),
            context))
        {
            results.Add(item);
        }

        results.Should().ContainSingle();
        results[0].Path.InternalPath.Should().Be(inScope.Replace('\\', '/'));
    }

    private sealed class StubHost : OpenShell.IHost
    {
        public StubHost(ItemPath location, IServiceProvider services)
        {
            CurrentLocation = location;
            Services = services;
        }

        public HostKind Kind => HostKind.Cli;
        public ItemPath CurrentLocation { get; set; }
        public IObservable<IReadOnlyList<IItem>> Selection => Observable.Empty<IReadOnlyList<IItem>>();
        public IProgress<OperationProgress> Progress { get; } = new Progress<OperationProgress>();
        public IServiceProvider Services { get; }

        public Task WriteOutputLineAsync(string line, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task WriteItemsAsync(IAsyncEnumerable<IItem> items, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
