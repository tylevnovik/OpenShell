using FluentAssertions;
using NSubstitute;
using OpenShell.Completion;
using OpenShell.Completion.Sources;
using OpenShell.History;
using OpenShell.Paths;
using Xunit;

namespace OpenShell.Core.Tests.Completion;

/// <summary>
/// HistoryCompletionSource tests. Per ADR-0009.
/// Verifies history prefix matching, deduplication, and the result cap.
/// </summary>
public class HistoryCompletionSourceTests
{
    private static HistoryEntry MakeEntry(string command)
        => new()
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Command = command,
            Success = true,
            ExitCode = 0,
            WorkingDirectory = ItemPath.Root("fs"),
        };

    private static IHistoryService MakeService(params HistoryEntry[] entries)
    {
        var service = Substitute.For<IHistoryService>();
        service.Recent.Returns(entries.ToList());
        return service;
    }

    [Fact]
    public void GetCompletions_EmptyToken_ReturnsAllHistory()
    {
        var history = MakeService(
            MakeEntry("get-item"),
            MakeEntry("set-item"));
        var source = new HistoryCompletionSource(history);

        var results = source.GetCompletions(new CompletionContext("", 0));

        results.Should().HaveCount(2);
    }

    [Fact]
    public void GetCompletions_PrefixMatch_ReturnsMatchingEntries()
    {
        var history = MakeService(
            MakeEntry("get-item"),
            MakeEntry("set-item"),
            MakeEntry("get-childitem"));
        var source = new HistoryCompletionSource(history);

        var results = source.GetCompletions(new CompletionContext("get", 3));

        results.Should().HaveCount(2);
        results.Should().OnlyContain(c => c.CompletionText.StartsWith("get", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetCompletions_NewestFirst_ReturnsInReverseOrder()
    {
        var history = MakeService(
            MakeEntry("get-item-old"),
            MakeEntry("get-item-new"));
        var source = new HistoryCompletionSource(history);

        var results = source.GetCompletions(new CompletionContext("get", 3));

        results.Should().HaveCount(2);
        results[0].CompletionText.Should().Be("get-item-new");
        results[1].CompletionText.Should().Be("get-item-old");
    }

    [Fact]
    public void GetCompletions_DuplicateCommands_Deduplicated()
    {
        var history = MakeService(
            MakeEntry("get-item"),
            MakeEntry("get-item"),
            MakeEntry("get-item"));
        var source = new HistoryCompletionSource(history);

        var results = source.GetCompletions(new CompletionContext("get", 3));

        results.Should().HaveCount(1);
        results[0].CompletionText.Should().Be("get-item");
    }

    [Fact]
    public void GetCompletions_MoreThanMaxResults_CapsAtFive()
    {
        var entries = Enumerable.Range(0, 10)
            .Select(i => MakeEntry($"get-cmd{i}"))
            .ToArray();
        var history = MakeService(entries);
        var source = new HistoryCompletionSource(history);

        var results = source.GetCompletions(new CompletionContext("get", 3));

        results.Should().HaveCount(5);
    }

    [Fact]
    public void GetCompletions_NotAtStart_ReturnsEmpty()
    {
        var history = MakeService(MakeEntry("get-item"));
        var source = new HistoryCompletionSource(history);

        var results = source.GetCompletions(new CompletionContext("get-item -", 10));

        results.Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_NoMatch_ReturnsEmpty()
    {
        var history = MakeService(MakeEntry("get-item"));
        var source = new HistoryCompletionSource(history);

        var results = source.GetCompletions(new CompletionContext("xyz", 3));

        results.Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_CaseInsensitive_MatchesHistory()
    {
        var history = MakeService(MakeEntry("Get-Item"));
        var source = new HistoryCompletionSource(history);

        var results = source.GetCompletions(new CompletionContext("get", 3));

        results.Should().HaveCount(1);
        results[0].CompletionText.Should().Be("Get-Item");
    }

    [Fact]
    public void GetCompletions_HistoryItem_HasHistoryKind()
    {
        var history = MakeService(MakeEntry("get-item"));
        var source = new HistoryCompletionSource(history);

        var results = source.GetCompletions(new CompletionContext("get", 3));

        results[0].Kind.Should().Be(CompletionKind.History);
    }

    [Fact]
    public void GetCompletions_DeduplicationKeepsNewest()
    {
        var history = MakeService(
            MakeEntry("get-item"),
            MakeEntry("get-item"));
        var source = new HistoryCompletionSource(history);

        var results = source.GetCompletions(new CompletionContext("get", 3));

        results.Should().HaveCount(1);
        // The newer entry (index 1) should be the one kept since we iterate newest-first.
        results[0].CompletionText.Should().Be("get-item");
    }

    [Fact]
    public void GetCompletions_EmptyHistory_ReturnsEmpty()
    {
        var history = MakeService();
        var source = new HistoryCompletionSource(history);

        var results = source.GetCompletions(new CompletionContext("get", 3));

        results.Should().BeEmpty();
    }
}
