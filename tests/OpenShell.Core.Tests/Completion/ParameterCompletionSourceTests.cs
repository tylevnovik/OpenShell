using FluentAssertions;
using NSubstitute;
using OpenShell.Commands;
using OpenShell.Completion;
using OpenShell.Completion.Sources;
using Xunit;

namespace OpenShell.Core.Tests.Completion;

/// <summary>
/// ParameterCompletionSource tests. Per ADR-0009.
/// Verifies parameter completion for the active command when the token starts with a hyphen.
/// </summary>
public class ParameterCompletionSourceTests
{
    private static CommandDescriptor MakeDescriptorWithParams(
        string fullName,
        params (string Name, string[] Aliases, bool Mandatory)[] parameters)
        => new()
        {
            Verb = fullName.Contains('-') ? fullName.Split('-', 2)[0] : fullName,
            Noun = fullName.Contains('-') ? fullName.Split('-', 2)[1] : "",
            FullName = fullName,
            CommandType = typeof(object),
            ArgsType = typeof(object),
            Parameters = parameters
                .Select(p => new ParameterDescriptor
                {
                    Name = p.Name,
                    ParameterAttribute = new ParameterAttribute
                    {
                        Aliases = p.Aliases,
                        Mandatory = p.Mandatory,
                    },
                    Type = typeof(string),
                })
                .ToList(),
        };

    private static ICommandRegistry MakeRegistry(CommandDescriptor descriptor)
    {
        var registry = Substitute.For<ICommandRegistry>();
        registry.Resolve(descriptor.FullName).Returns(descriptor);
        return registry;
    }

    [Fact]
    public void GetCompletions_DashToken_ReturnsMatchingParameters()
    {
        var descriptor = MakeDescriptorWithParams(
            "get-item",
            ("Path", new[] { "-p" }, false),
            ("Recurse", Array.Empty<string>(), false));
        var commands = MakeRegistry(descriptor);
        var source = new ParameterCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("get-item -p", 11));

        results.Should().Contain(c => c.CompletionText == "-Path");
        results.Should().Contain(c => c.CompletionText == "-p");
    }

    [Fact]
    public void GetCompletions_PrefixMatch_ReturnsOnlyMatchingParameters()
    {
        var descriptor = MakeDescriptorWithParams(
            "get-item",
            ("Path", Array.Empty<string>(), false),
            ("Recurse", Array.Empty<string>(), false),
            ("Force", Array.Empty<string>(), false));
        var commands = MakeRegistry(descriptor);
        var source = new ParameterCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("get-item -r", 11));

        results.Should().HaveCount(1);
        results[0].CompletionText.Should().Be("-Recurse");
    }

    [Fact]
    public void GetCompletions_UnknownCommand_ReturnsEmpty()
    {
        var descriptor = MakeDescriptorWithParams("get-item", ("Path", Array.Empty<string>(), false));
        var registry = Substitute.For<ICommandRegistry>();
        registry.Resolve("get-item").Returns(descriptor);
        var source = new ParameterCompletionSource(registry);

        var results = source.GetCompletions(new CompletionContext("unknown-cmd -p", 13));

        results.Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_TokenWithoutDash_ReturnsEmpty()
    {
        var descriptor = MakeDescriptorWithParams("get-item", ("Path", Array.Empty<string>(), false));
        var commands = MakeRegistry(descriptor);
        var source = new ParameterCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("get-item path", 12));

        results.Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_AtStart_ReturnsEmpty()
    {
        var descriptor = MakeDescriptorWithParams("get-item", ("Path", Array.Empty<string>(), false));
        var commands = MakeRegistry(descriptor);
        var source = new ParameterCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("-", 1));

        results.Should().BeEmpty();
    }

    [Fact]
    public void GetCompletions_MandatoryParameter_HasDescription()
    {
        var descriptor = MakeDescriptorWithParams(
            "get-item",
            ("Path", Array.Empty<string>(), true));
        var commands = MakeRegistry(descriptor);
        var source = new ParameterCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("get-item -p", 11));

        var pathItem = results.Single(r => r.CompletionText == "-Path");
        pathItem.Description.Should().Be("Required");
        pathItem.Kind.Should().Be(CompletionKind.Parameter);
    }

    [Fact]
    public void GetCompletions_OptionalParameter_HasNullDescription()
    {
        var descriptor = MakeDescriptorWithParams(
            "get-item",
            ("Recurse", Array.Empty<string>(), false));
        var commands = MakeRegistry(descriptor);
        var source = new ParameterCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("get-item -r", 11));

        results[0].Description.Should().BeNull();
    }

    [Fact]
    public void GetCompletions_ParameterAlias_MatchesPrefix()
    {
        var descriptor = MakeDescriptorWithParams(
            "get-item",
            ("Path", new[] { "-p" }, false));
        var commands = MakeRegistry(descriptor);
        var source = new ParameterCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("get-item -p", 11));

        results.Should().Contain(c => c.CompletionText == "-p" && c.Kind == CompletionKind.Parameter);
    }

    [Fact]
    public void GetCompletions_CaseInsensitive_MatchesParameter()
    {
        var descriptor = MakeDescriptorWithParams(
            "get-item",
            ("Path", Array.Empty<string>(), false));
        var commands = MakeRegistry(descriptor);
        var source = new ParameterCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("get-item -PA", 12));

        results.Should().HaveCount(1);
        results[0].CompletionText.Should().Be("-Path");
    }

    [Fact]
    public void GetCompletions_EmptyDash_ReturnsAllParameters()
    {
        var descriptor = MakeDescriptorWithParams(
            "get-item",
            ("Path", Array.Empty<string>(), false),
            ("Recurse", Array.Empty<string>(), false));
        var commands = MakeRegistry(descriptor);
        var source = new ParameterCompletionSource(commands);

        var results = source.GetCompletions(new CompletionContext("get-item -", 10));

        results.Should().HaveCount(2);
    }
}
