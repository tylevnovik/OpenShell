using FluentAssertions;
using OpenShell.KeyBindings;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.KeyBindings;

/// <summary>
/// Tests for KeyBindingFileLoader. Per ADR-0027 section 4.
/// Uses isolated temp files; missing or invalid files degrade gracefully.
/// </summary>
public class KeyBindingFileLoaderTests
{
    private const string TwoBindingsToml = """
[[binding]]
gesture = "Ctrl+Shift+F"
command = "format-table"
when = "focus:pane"
description = "Format as table"

[[binding]]
gesture = "F9"
command = "open-external"
description = "Open in external app"
""";

    [Fact]
    public void Load_ValidFile_ReturnsBindings()
    {
        using var temp = new TempDir();
        var path = temp.CreateFile("kb.toml", TwoBindingsToml);
        var loader = new KeyBindingFileLoader(path);

        var loaded = loader.Load();

        loaded.Should().HaveCount(2);
        loaded[0].GestureText.Should().Be("Ctrl+Shift+F");
        loaded[0].Command.Should().Be("format-table");
        loaded[0].When.Should().Be("focus:pane");
        loaded[0].Description.Should().Be("Format as table");
        loaded[0].Unbind.Should().BeFalse();
        loaded[1].GestureText.Should().Be("F9");
        loaded[1].Command.Should().Be("open-external");
        loaded[1].Unbind.Should().BeFalse();
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        using var temp = new TempDir();
        var loader = new KeyBindingFileLoader(temp.GetFullPath("does-not-exist.toml"));

        var loaded = loader.Load();

        loaded.Should().BeEmpty();
    }

    [Fact]
    public void Load_UnbindEntry_SetsUnbindTrue()
    {
        using var temp = new TempDir();
        var toml = """
[[binding]]
gesture = "Ctrl+H"
unbind = true
""";
        var path = temp.CreateFile("kb.toml", toml);
        var loader = new KeyBindingFileLoader(path);

        var loaded = loader.Load();

        loaded.Should().HaveCount(1);
        loaded[0].GestureText.Should().Be("Ctrl+H");
        loaded[0].Unbind.Should().BeTrue();
        loaded[0].Command.Should().BeNull();
    }

    [Fact]
    public void Load_InvalidToml_ReturnsEmptyNoThrow()
    {
        using var temp = new TempDir();
        var path = temp.CreateFile("kb.toml", "this is = = not valid toml [[[");
        var loader = new KeyBindingFileLoader(path);

        var act = () => loader.Load();

        act.Should().NotThrow();
        loader.Load().Should().BeEmpty();
    }

    [Fact]
    public void Load_EntryWithArgs_ParsesArgsTable()
    {
        using var temp = new TempDir();
        var toml = """
[[binding]]
gesture = "Ctrl+E"
command = "export"
description = "Export items"

[binding.args]
format = "csv"
destination = "/tmp/out"
""";
        var path = temp.CreateFile("kb.toml", toml);
        var loader = new KeyBindingFileLoader(path);

        var loaded = loader.Load();

        loaded.Should().HaveCount(1);
        loaded[0].Args.Should().NotBeNull();
        loaded[0].Args!["format"].Should().Be("csv");
        loaded[0].Args!["destination"].Should().Be("/tmp/out");
    }

    [Fact]
    public void Load_EntryMissingGesture_Skipped()
    {
        using var temp = new TempDir();
        var toml = """
[[binding]]
command = "no-gesture"
description = "missing gesture"
""";
        var path = temp.CreateFile("kb.toml", toml);
        var loader = new KeyBindingFileLoader(path);

        var loaded = loader.Load();

        loaded.Should().BeEmpty();
    }

    [Fact]
    public void Save_RoundTripsThroughLoad()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("kb.toml");
        var loader = new KeyBindingFileLoader(path);
        var original = new List<UserKeyBinding>
        {
            new(
                GestureText: "Ctrl+Shift+F",
                Command: "format-table",
                Args: new Dictionary<string, string> { ["format"] = "table" },
                When: "focus:pane",
                Description: "Format as table",
                Unbind: false),
            new(
                GestureText: "Ctrl+H",
                Command: null,
                Args: null,
                When: null,
                Description: null,
                Unbind: true),
        };

        loader.Save(original);
        var loaded = loader.Load();

        loaded.Should().HaveCount(2);
        loaded[0].GestureText.Should().Be("Ctrl+Shift+F");
        loaded[0].Command.Should().Be("format-table");
        loaded[0].When.Should().Be("focus:pane");
        loaded[0].Description.Should().Be("Format as table");
        loaded[0].Unbind.Should().BeFalse();
        loaded[0].Args.Should().NotBeNull();
        loaded[0].Args!["format"].Should().Be("table");
        loaded[1].GestureText.Should().Be("Ctrl+H");
        loaded[1].Unbind.Should().BeTrue();
    }

    [Fact]
    public void Save_CreatesParentDirectory()
    {
        using var temp = new TempDir();
        var nestedPath = temp.GetFullPath("nested/deep/kb.toml");
        var loader = new KeyBindingFileLoader(nestedPath);

        loader.Save(new List<UserKeyBinding>());

        File.Exists(nestedPath).Should().BeTrue();
    }

    [Fact]
    public void Save_EmptyList_RoundTripsToEmpty()
    {
        using var temp = new TempDir();
        var path = temp.GetFullPath("kb.toml");
        var loader = new KeyBindingFileLoader(path);

        loader.Save(new List<UserKeyBinding>());
        var loaded = loader.Load();

        loaded.Should().BeEmpty();
    }
}
