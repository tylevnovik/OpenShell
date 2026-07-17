using FluentAssertions;
using OpenShell.Variables;
using Xunit;

namespace OpenShell.Core.Tests.Variables;

/// <summary>
/// ScopeStack 单元测试。Per ADR-0047 §1.
/// </summary>
public class ScopeStackTests
{
    [Fact]
    public void Constructor_PreconfiguresGlobalScriptLocalFrames()
    {
        var stack = new ScopeStack();
        stack.Depth.Should().Be(3);
        stack.Global.Kind.Should().Be(VariableScope.Global);
        stack.Script.Kind.Should().Be(VariableScope.Script);
        stack.Current.Kind.Should().Be(VariableScope.Local);
    }

    [Fact]
    public void Current_ReturnsLastFrame()
    {
        var stack = new ScopeStack();
        stack.Current.Should().BeSameAs(stack[0]);
    }

    [Fact]
    public void Indexer_WalksFromTopToBottom()
    {
        var stack = new ScopeStack();
        stack[0].Kind.Should().Be(VariableScope.Local);
        stack[1].Kind.Should().Be(VariableScope.Script);
        stack[2].Kind.Should().Be(VariableScope.Global);
    }

    [Fact]
    public void SetCurrent_ThenLookup_FindsEntry()
    {
        var stack = new ScopeStack();
        stack.SetCurrent("foo", new VariableEntry("foo", 42));
        var entry = stack.Lookup("foo");
        entry.Should().NotBeNull();
        entry!.Value.Should().Be(42);
        entry!.Name.Should().Be("foo");
    }

    [Fact]
    public void Lookup_WalksUpToParentFrames()
    {
        var stack = new ScopeStack();
        stack.SetCurrent("base", new VariableEntry("base", "from-current"));
        using (stack.PushScope())
        {
            // New current frame doesn't have "base".
            stack.LookupLocal("base").Should().BeNull();
            // But walking up the stack finds it.
            var entry = stack.Lookup("base");
            entry.Should().NotBeNull();
            entry!.Value.Should().Be("from-current");
        }
    }

    [Fact]
    public void Lookup_AfterPop_NoLongerFindsPoppedEntry()
    {
        var stack = new ScopeStack();
        using (stack.PushScope())
        {
            stack.SetCurrent("temp", new VariableEntry("temp", 1));
            stack.Lookup("temp").Should().NotBeNull();
        }
        // After pop the temp entry is gone.
        stack.Lookup("temp").Should().BeNull();
    }

    [Fact]
    public void Lookup_SkipsPrivateInParentFrames()
    {
        var stack = new ScopeStack();
        // Private in the parent frame.
        stack.SetCurrent("secret", new VariableEntry("secret", "hidden", isPrivate: true));
        using (stack.PushScope())
        {
            // Child cannot see parent's private.
            stack.Lookup("secret").Should().BeNull();
        }
        // Same frame can see own private.
        stack.Lookup("secret").Should().NotBeNull();
    }

    [Fact]
    public void Lookup_WithSkipPrivateFalse_FindsPrivateInParent()
    {
        var stack = new ScopeStack();
        stack.SetCurrent("secret", new VariableEntry("secret", "hidden", isPrivate: true));
        using (stack.PushScope())
        {
            // Explicit skipPrivate=false allows lookup.
            var entry = stack.Lookup("secret", skipPrivate: false);
            entry.Should().NotBeNull();
            entry!.Value.Should().Be("hidden");
        }
    }

    [Fact]
    public void LookupGlobal_OnlyChecksGlobalFrame()
    {
        var stack = new ScopeStack();
        stack.Global.Set("g", new VariableEntry("g", "global-val"));
        stack.SetCurrent("g", new VariableEntry("g", "current-val"));
        var entry = stack.LookupGlobal("g");
        entry.Should().NotBeNull();
        entry!.Value.Should().Be("global-val");
    }

    [Fact]
    public void LookupScript_OnlyChecksScriptFrame()
    {
        var stack = new ScopeStack();
        stack.Script.Set("s", new VariableEntry("s", "script-val"));
        var entry = stack.LookupScript("s");
        entry.Should().NotBeNull();
        entry!.Value.Should().Be("script-val");
    }

    [Fact]
    public void LookupLocal_OnlyChecksCurrentFrame()
    {
        var stack = new ScopeStack();
        stack.Global.Set("fromGlobal", new VariableEntry("fromGlobal", 1));
        // Local frame doesn't have it.
        stack.LookupLocal("fromGlobal").Should().BeNull();
        // But walking up does.
        stack.Lookup("fromGlobal").Should().NotBeNull();
    }

    [Fact]
    public void PushScope_DefaultsToLocalKind()
    {
        var stack = new ScopeStack();
        using (stack.PushScope())
        {
            stack.Current.Kind.Should().Be(VariableScope.Local);
            stack.Depth.Should().Be(4);
        }
        stack.Depth.Should().Be(3);
    }

    [Fact]
    public void PushScope_WithExplicitKind()
    {
        var stack = new ScopeStack();
        using (stack.PushScope(VariableScope.Script))
        {
            stack.Current.Kind.Should().Be(VariableScope.Script);
        }
    }

    [Fact]
    public void RemoveFromCurrent_RemovesFromTopFrame()
    {
        var stack = new ScopeStack();
        stack.SetCurrent("temp", new VariableEntry("temp", 1));
        stack.RemoveFromCurrent("temp").Should().BeTrue();
        stack.Lookup("temp").Should().BeNull();
    }

    [Fact]
    public void RemoveFromCurrent_ReturnsFalseWhenAbsent()
    {
        var stack = new ScopeStack();
        stack.RemoveFromCurrent("nonexistent").Should().BeFalse();
    }

    [Fact]
    public void EnumerateVisible_ChildOverridesParent()
    {
        var stack = new ScopeStack();
        stack.Global.Set("dup", new VariableEntry("dup", "global"));
        stack.SetCurrent("dup", new VariableEntry("dup", "current"));
        stack.SetCurrent("onlyCurrent", new VariableEntry("onlyCurrent", 1));

        var visible = stack.EnumerateVisible().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        visible["dup"].Value.Should().Be("current");
        visible.Should().ContainKey("onlyCurrent");
    }

    [Fact]
    public void ScopeFrame_Set_ReplacesValue()
    {
        var frame = new ScopeFrame(VariableScope.Local);
        frame.Set("x", new VariableEntry("x", 1));
        frame.Set("x", new VariableEntry("x", 2));
        frame.TryGet("x", out var entry).Should().BeTrue();
        entry!.Value.Should().Be(2);
    }

    [Fact]
    public void ScopeFrame_Remove_ReturnsWhetherExisted()
    {
        var frame = new ScopeFrame(VariableScope.Local);
        frame.Set("x", new VariableEntry("x", 1));
        frame.Remove("x").Should().BeTrue();
        frame.Remove("x").Should().BeFalse();
    }

    [Fact]
    public void ScopeFrame_CaseInsensitiveKeys()
    {
        var frame = new ScopeFrame(VariableScope.Local);
        frame.Set("Foo", new VariableEntry("Foo", 1));
        frame.TryGet("FOO", out var entry).Should().BeTrue();
        entry!.Value.Should().Be(1);
    }
}
