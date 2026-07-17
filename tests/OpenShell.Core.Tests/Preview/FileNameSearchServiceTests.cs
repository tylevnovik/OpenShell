using FluentAssertions;
using OpenShell.Core.Tests.TestSupport;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Preview;
using OpenShell.Providers;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Preview;

/// <summary>
/// ADR-0030 §4: FileNameSearchService 单测。
/// 验证: 简单子串匹配, MaxResults 限制, 模糊子序列匹配, Recurse=false 仅当前目录。
/// </summary>
public class FileNameSearchServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FileNameSearchService _svc;
    private readonly ItemPath _root;

    public FileNameSearchServiceTests()
    {
        var providers = new ProviderRegistry();
        providers.Register(new StubFileProvider());
        _svc = new FileNameSearchService(providers);
        _root = new ItemPath { Provider = "fs", InternalPath = _tempDir.FullPath.Replace('\\', '/') };
    }

    [Fact]
    public async Task SearchAsync_SubstringMatch_ReturnsMatchingFiles()
    {
        // Arrange: test.txt + mytest.cs + other.log
        _tempDir.CreateFile("test.txt", "");
        _tempDir.CreateFile("mytest.cs", "");
        _tempDir.CreateFile("other.log", "");

        // Act: query="test", 关闭模糊匹配 (纯子串)。
        var results = new List<IItem>();
        await foreach (var item in _svc.SearchAsync(
            _root, "test", new SearchOptions(FuzzyMatch: false), default))
        {
            results.Add(item);
        }

        // Assert: 匹配 test.txt + mytest.cs, 不匹配 other.log。
        results.Should().HaveCount(2);
        results.Select(r => r.Name).Should().Contain(new[] { "test.txt", "mytest.cs" });
        results.Should().AllBeAssignableTo<SearchResultItem>();
    }

    [Fact]
    public async Task SearchAsync_MaxResults_LimitsOutput()
    {
        // Arrange: 创建 5 个匹配文件。
        for (int i = 0; i < 5; i++)
            _tempDir.CreateFile($"match{i}.txt", "");

        // Act: MaxResults = 2。
        var results = new List<IItem>();
        await foreach (var item in _svc.SearchAsync(
            _root, "match", new SearchOptions(FuzzyMatch: false, MaxResults: 2), default))
        {
            results.Add(item);
        }

        // Assert: 只返回 2 个。
        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAsync_FuzzyMatch_MatchesSubsequence()
    {
        // Arrange: test.txt — 子序列 "tst" 匹配 (t-e-s-t 中 t,s,t 按顺序)。
        _tempDir.CreateFile("test.txt", "");
        _tempDir.CreateFile("other.log", "");

        // Act: query="tst", 模糊匹配。
        var results = new List<IItem>();
        await foreach (var item in _svc.SearchAsync(
            _root, "tst", new SearchOptions(FuzzyMatch: true), default))
        {
            results.Add(item);
        }

        // Assert: "tst" 作为子序列匹配 "test.txt", 不匹配 "other.log"。
        results.Should().HaveCount(1);
        results[0].Name.Should().Be("test.txt");
    }

    [Fact]
    public async Task SearchAsync_NoRecurse_OnlyCurrentDirectory()
    {
        // Arrange: 当前目录 match.txt + 子目录 match2.txt。
        _tempDir.CreateFile("match.txt", "");
        _tempDir.CreateDirectory("sub");
        _tempDir.CreateFile("sub/match2.txt", "");

        // Act: Recurse=false, 关闭模糊匹配。
        var results = new List<IItem>();
        await foreach (var item in _svc.SearchAsync(
            _root, "match", new SearchOptions(Recurse: false, FuzzyMatch: false), default))
        {
            results.Add(item);
        }

        // Assert: 只匹配当前目录的 match.txt, 不递归子目录。
        results.Should().HaveCount(1);
        results[0].Name.Should().Be("match.txt");
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsNothing()
    {
        _tempDir.CreateFile("a.txt", "");

        var results = new List<IItem>();
        await foreach (var item in _svc.SearchAsync(_root, "", new SearchOptions(), default))
            results.Add(item);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_FuzzyResult_HasScoreProperty()
    {
        _tempDir.CreateFile("test.txt", "");

        var results = new List<IItem>();
        await foreach (var item in _svc.SearchAsync(_root, "test", new SearchOptions(FuzzyMatch: true), default))
            results.Add(item);

        results.Should().HaveCount(1);
        var searchResult = results[0].Should().BeOfType<SearchResultItem>().Subject;
        searchResult.Score.Should().BeGreaterThan(0);
    }

    public void Dispose() => _tempDir.Dispose();
}
