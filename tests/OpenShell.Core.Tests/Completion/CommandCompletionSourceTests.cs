using FluentAssertions;
using NSubstitute;
using OpenShell.Commands;
using OpenShell.Completion;
using OpenShell.Completion.Sources;
using Xunit;

namespace OpenShell.Core.Tests.Completion;

/// <summary>
/// CommandCompletionSource tests. Per ADR-0009.
/// Verifies prefix matching for command full names and aliases at the command-name position.
/// </summary>
public class CommandCompletionSourceTests
{
    private static CommandDescriptor MakeDescriptor(
        string fullName,
        string[]? aliases = null,
        string? description = null)
        => new()
        {
            Verb = fullName.Contains('-') ? fullName.Split('-', 2)[0] : fullName,
            Noun = fullName.Contains('-') ? fullName.Split('-', 2)[1] : "",
            FullName = fullName,
            CommandType = typeof(object),
            ArgsType = typeof(object),
            Description = description,
            Aliases = aliases ?? [],
        };

    private static ICommandRegistry MakeRegistry(params CommandDescriptor[] descriptors)
    {
        var registry = Substitute.For<ICommandRegistry>();
        var collection = descriptors.ToList();
        registry.Registered.Returns(collection);
        return registry;
    }

    [Fact]
    public void GetCompletions_EmptyToken_ReturnsAllCommands()
    {
        var commands = MakeRegistry(
            MakeDescriptor("get-childitem", description: "List children"),
            MakeDescriptor("set-item"));
        var source = new CommandCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("", 0));

        results.Should().HaveCount(2);
        results.Should().Contain(c => c.CompletionText == "get-childitem");
        results.Should().Contain(c => c.CompletionText == "set-item");
    }

    [Fact]
    public void GetCompletions_PrefixMatch_ReturnsMatchingCommands()
    {
        var commands = MakeRegistry(
            MakeDescriptor("get-childitem"),
            MakeDescriptor("get-item"),
            MakeDescriptor("set-item"));
        var source = new CommandCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("get", 3));

        results.Should().HaveCount(2);
        results.Should().OnlyContain(c =>
            c.CompletionText.StartsWith("get", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetCompletions_AliasMatch_ReturnsAliasItem()
    {
        var commands = MakeRegistry(
            MakeDescriptor("get-childitem", aliases: new[] { "gci", "dir" }));
        var source = new CommandCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("gc", 2));

        results.Should().Contain(c => c.CompletionText == "gci" && c.Kind == CompletionKind.Alias);
    }

    [Fact]
    public void GetCompletions_AliasMatch_DifferentAlias()
    {
        var commands = MakeRegistry(
            MakeDescriptor("get-childitem", aliases: new[] { "gci", "dir" }));
        var source = new CommandCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("di", 2));

        results.Should().Contain(c => c.CompletionText == "dir");
    }

    [Fact]
    public void GetCompletions_CaseInsensitive_MatchesCommand()
    {
        var commands = MakeRegistry(MakeDescriptor("get-item"));
        var source = new CommandCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("GET", 3));

        results.Should().HaveCount(1);
        results[0].CompletionText.Should().Be("get-item");
    }

    [Fact]
    public void GetCompletions_NotAtStart_ReturnsEmpty()
    {
        var commands = MakeRegistry(MakeDescriptor("get-item"));
        var source = new CommandCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("get-item -", 10));

        results.Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_NoMatch_ReturnsEmpty()
    {
        var commands = MakeRegistry(MakeDescriptor("get-item"));
        var source = new CommandCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("xyz", 3));

        results.Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_CommandItem_HasCommandKind()
    {
        var commands = MakeRegistry(MakeDescriptor("get-item"));
        var source = new CommandCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("get", 3));

        results[0].Kind.Should().Be(CompletionKind.Command);
    }

    [Fact]
    public void GetCompletions_AliasItem_HasDescription()
    {
        var commands = MakeRegistry(
            MakeDescriptor("get-childitem", aliases: new[] { "gci" }));
        var source = new CommandCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("gci", 3));

        var aliasItem = results.Single(r => r.CompletionText == "gci");
        aliasItem.Description.Should().NotBeNull();
        aliasItem.Description.Should().Contain("get-childitem");
    }

    [Fact]
    public void GetCompletions_PreservesDescription()
    {
        var commands = MakeRegistry(
            MakeDescriptor("get-item", description: "Gets an item"));
        var source = new CommandCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("get", 3));

        results[0].Description.Should().Be("Gets an item");
    }
}
