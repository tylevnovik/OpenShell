using FluentAssertions;
using OpenShell.Preview;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Preview;

/// <summary>
/// 长期文件索引正确性测试：重复文件名、改名、删除和路径范围必须保持一致。
/// </summary>
public sealed class FileIndexStoreTests
{
    [Fact]
    public void SearchByName_KeepsDuplicateNamesBoundToTheirOwnPaths()
    {
        using var temp = new TempDir();
        using var store = CreateStore(temp);
        var first = temp.GetFullPath("one/report.txt");
        var second = temp.GetFullPath("two/report.txt");

        store.Upsert(first, "report.txt", 10, 10);
        store.Upsert(second, "report.txt", 20, 20);

        var results = store.SearchByName("report*", limit: 10);

        results.Select(x => x.Path).Should().BeEquivalentTo(
            new[] { Normalize(first), Normalize(second) });
    }

    [Fact]
    public void SearchByName_AppliesPathScope()
    {
        using var temp = new TempDir();
        using var store = CreateStore(temp);
        var first = temp.GetFullPath("one/report.txt");
        var second = temp.GetFullPath("two/report.txt");

        store.Upsert(first, "report.txt", 10, 10);
        store.Upsert(second, "report.txt", 20, 20);

        var results = store.SearchByName("report*", limit: 10, pathPrefix: temp.GetFullPath("one"));

        results.Should().ContainSingle().Which.Path.Should().Be(Normalize(first));
    }

    [Fact]
    public void Upsert_RenameAndDeleteKeepFtsInSync()
    {
        using var temp = new TempDir();
        using var store = CreateStore(temp);
        var path = temp.GetFullPath("renamed.txt");

        store.Upsert(path, "old.txt", 10, 10);
        store.Upsert(path, "new.txt", 11, 11);

        store.SearchByName("old*").Should().BeEmpty();
        store.SearchByName("new*").Should().ContainSingle();

        store.Delete(path);

        store.HasEntries.Should().BeFalse();
        store.SearchByName("new*").Should().BeEmpty();
    }

    [Fact]
    public async Task Lifecycle_RebuildsStoreFromLoadedIndexer()
    {
        using var temp = new TempDir();
        var indexPath = temp.GetFullPath("filename-index.db");
        var databasePath = temp.GetFullPath("index/files.db");
        var indexedFile = temp.GetFullPath("indexed.txt");

        using (var indexer = new UsnJournalIndexer(indexPath))
        {
            indexer.Files[Normalize(indexedFile)] = new UsnJournalIndexer.IndexedFile(
                indexedFile, "indexed.txt", 5, 5);
            await indexer.SaveAsync();
        }

        using var loadedIndexer = new UsnJournalIndexer(indexPath);
        using var store = new FileIndexStore(databasePath);
        var lifecycle = new FileIndexLifecycleService(loadedIndexer, store, startBackgroundRefresh: false);

        await lifecycle.StartAsync(CancellationToken.None);

        lifecycle.IsReady.Should().BeTrue();
        store.SearchByName("indexed*").Should().ContainSingle();
    }

    private static FileIndexStore CreateStore(TempDir temp)
        => new(temp.GetFullPath("index/files.db"));

    private static string Normalize(string path) => path.Replace('\\', '/');
}
