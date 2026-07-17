using FluentAssertions;
using NSubstitute;
using OpenShell.Completion;
using OpenShell.Completion.Sources;
using OpenShell.Core.Tests.TestSupport;
using OpenShell.Paths;
using OpenShell.Providers;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Completion;

/// <summary>
/// PathCompletionSource tests. Per ADR-0009.
/// Uses StubFileProvider against a real TempDir so path enumeration behavior is verified
/// without depending on the OpenShell.Providers.FileSystem assembly.
/// </summary>
public class PathCompletionSourceTests
{
    private static (PathCompletionSource source, TempDir tempDir) CreateSource()
    {
        var tempDir = new TempDir();
        var provider = new StubFileProvider();
        var registry = new ProviderRegistry();
        registry.Register(provider);
        var location = ItemPath.Parse(tempDir.FullPath);
        var source = new PathCompletionSource(registry, () => location);
        return (source, tempDir);
    }

    [Fact]
    public void GetCompletions_PrefixMatch_ReturnsMatchingFiles()
    {
        var (source, tempDir) = CreateSource();
        using (tempDir)
        {
            tempDir.CreateFile("alpha.txt");
            tempDir.CreateFile("beta.txt");

            var results = source.GetCompletions(new CompletionContext("get-item al", 11));

            results.Should().HaveCount(1);
            results[0].CompletionText.Should().Be("alpha.txt");
            results[0].Kind.Should().Be(CompletionKind.Path);
        }
    }

    [Fact]
    public void GetCompletions_Directory_AppendsTrailingSlash()
    {
        var (source, tempDir) = CreateSource();
        using (tempDir)
        {
            tempDir.CreateDirectory("subdir");

            var results = source.GetCompletions(new CompletionContext("get-item sub", 12));

            results.Should().HaveCount(1);
            results[0].CompletionText.Should().Be("subdir/");
            results[0].Description.Should().Be("Directory");
        }
    }

    [Fact]
    public void GetCompletions_MultipleMatches_ReturnsAll()
    {
        var (source, tempDir) = CreateSource();
        using (tempDir)
        {
            tempDir.CreateFile("app.cs");
            tempDir.CreateFile("app.js");
            tempDir.CreateFile("other.txt");

            var results = source.GetCompletions(new CompletionContext("get-item app", 12));

            results.Should().HaveCount(2);
            results.Should().OnlyContain(c => c.CompletionText.StartsWith("app", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void GetCompletions_AtStart_ReturnsEmpty()
    {
        var (source, tempDir) = CreateSource();
        using (tempDir)
        {
            tempDir.CreateFile("alpha.txt");

            var results = source.GetCompletions(new CompletionContext("al", 2));

            results.Should().BeEmpty();
        }
    }

    [Fact]
    public void GetCompletions_DashToken_ReturnsEmpty()
    {
        var (source, tempDir) = CreateSource();
        using (tempDir)
        {
            tempDir.CreateFile("alpha.txt");

            var results = source.GetCompletions(new CompletionContext("get-item -", 10));

            results.Should().BeEmpty();
        }
    }

    [Fact]
    public void GetCompletions_DollarToken_ReturnsEmpty()
    {
        var (source, tempDir) = CreateSource();
        using (tempDir)
        {
            tempDir.CreateFile("alpha.txt");

            var results = source.GetCompletions(new CompletionContext("get-item $HO", 12));

            results.Should().BeEmpty();
        }
    }

    [Fact]
    public void GetCompletions_NoMatch_ReturnsEmpty()
    {
        var (source, tempDir) = CreateSource();
        using (tempDir)
        {
            tempDir.CreateFile("alpha.txt");

            var results = source.GetCompletions(new CompletionContext("get-item xyz", 12));

            results.Should().BeEmpty();
        }
    }

    [Fact]
    public void GetCompletions_EmptyToken_ReturnsAllEntries()
    {
        var (source, tempDir) = CreateSource();
        using (tempDir)
        {
            tempDir.CreateFile("alpha.txt");
            tempDir.CreateDirectory("beta");

            var results = source.GetCompletions(new CompletionContext("get-item ", 9));

            results.Should().HaveCount(2);
        }
    }

    [Fact]
    public void GetCompletions_CaseInsensitive_MatchesFiles()
    {
        var (source, tempDir) = CreateSource();
        using (tempDir)
        {
            tempDir.CreateFile("Alpha.txt");

            var results = source.GetCompletions(new CompletionContext("get-item al", 11));

            results.Should().HaveCount(1);
            results[0].CompletionText.Should().Be("Alpha.txt");
        }
    }

    [Fact]
    public void GetCompletions_PathWithSeparator_EnumeratesSubdirectory()
    {
        var (source, tempDir) = CreateSource();
        using (tempDir)
        {
            tempDir.CreateFile("sub/gamma.txt");
            tempDir.CreateFile("sub/gamma.cs");
            tempDir.CreateFile("other.txt");

            var results = source.GetCompletions(new CompletionContext("get-item sub/g", 14));

            results.Should().HaveCount(2);
            results.Should().OnlyContain(c => c.CompletionText.StartsWith("gamma", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void GetCompletions_FileItem_HasNullDescription()
    {
        var (source, tempDir) = CreateSource();
        using (tempDir)
        {
            tempDir.CreateFile("alpha.txt");

            var results = source.GetCompletions(new CompletionContext("get-item al", 11));

            results[0].Description.Should().BeNull();
        }
    }
}
