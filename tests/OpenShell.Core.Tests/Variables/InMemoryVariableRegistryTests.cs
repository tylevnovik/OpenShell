using FluentAssertions;
using OpenShell.Variables;
using Xunit;

namespace OpenShell.Core.Tests.Variables;

/// <summary>
/// InMemoryVariableRegistry 单元测试。Per ADR-0042, ADR-0033.
/// </summary>
public class InMemoryVariableRegistryTests
{
    [Fact]
    public void Resolve_UndefinedName_ReturnsNull()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Resolve("undefined").Should().BeNull();
    }

    [Fact]
    public void Set_SessionScope_ResolveReturnsValue()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("foo", "bar");
        registry.Resolve("foo").Should().Be("bar");
    }

    [Fact]
    public void Set_GlobalScope_ResolveReturnsValue()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("gvar", "gval", VariableScope.Global);
        registry.Resolve("gvar").Should().Be("gval");
    }

    [Fact]
    public void Set_ScriptScope_ResolveReturnsValue()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("svar", "sval", VariableScope.Script);
        registry.Resolve("svar").Should().Be("sval");
    }

    [Fact]
    public void Resolve_SessionOverridesScript()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("dup", "script-val", VariableScope.Script);
        registry.Set("dup", "session-val", VariableScope.Session);
        registry.Resolve("dup").Should().Be("session-val");
    }

    [Fact]
    public void Resolve_ScriptOverridesGlobal()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("dup", "global-val", VariableScope.Global);
        registry.Set("dup", "script-val", VariableScope.Script);
        registry.Resolve("dup").Should().Be("script-val");
    }

    [Fact]
    public void Set_AutomaticReadOnlyVariable_Throws()
    {
        var registry = new InMemoryVariableRegistry();
        var act = () => registry.Set("TRUE", false);
        act.Should().Throw<ReadOnlyVariableException>();
    }

    [Fact]
    public void Set_PwdReadOnlyVariable_Throws()
    {
        var registry = new InMemoryVariableRegistry();
        var act = () => registry.Set("PWD", "/somewhere");
        act.Should().Throw<ReadOnlyVariableException>();
    }

    [Fact]
    public void IsReadOnly_TrueAutomaticVariable_ReturnsTrue()
    {
        var registry = new InMemoryVariableRegistry();
        registry.IsReadOnly("TRUE").Should().BeTrue();
        registry.IsReadOnly("FALSE").Should().BeTrue();
        registry.IsReadOnly("NULL").Should().BeTrue();
        registry.IsReadOnly("HOME").Should().BeTrue();
        registry.IsReadOnly("HOSTNAME").Should().BeTrue();
        registry.IsReadOnly("PID").Should().BeTrue();
        registry.IsReadOnly("OS").Should().BeTrue();
        registry.IsReadOnly("LASTEXITCODE").Should().BeTrue();
        registry.IsReadOnly("PWD").Should().BeTrue();
    }

    [Fact]
    public void IsReadOnly_UserVariable_ReturnsFalse()
    {
        var registry = new InMemoryVariableRegistry();
        registry.IsReadOnly("myvar").Should().BeFalse();
        registry.IsReadOnly("env:PATH").Should().BeFalse();
    }

    [Fact]
    public void Resolve_TrueAutomatic_ReturnsBooleanTrue()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Resolve("TRUE").Should().Be(true);
    }

    [Fact]
    public void Resolve_FalseAutomatic_ReturnsBooleanFalse()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Resolve("FALSE").Should().Be(false);
    }

    [Fact]
    public void Resolve_NullAutomatic_ReturnsNull()
    {
        var registry = new InMemoryVariableRegistry();
        // NULL 被赋值为 null! ——Resolve 返回 null
        registry.Resolve("NULL").Should().BeNull();
    }

    [Fact]
    public void Resolve_HomeAutomatic_ReturnsNonEmptyString()
    {
        var registry = new InMemoryVariableRegistry();
        var home = registry.Resolve("HOME") as string;
        home.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Resolve_OsAutomatic_ReturnsKnownPlatform()
    {
        var registry = new InMemoryVariableRegistry();
        var os = registry.Resolve("OS") as string;
        os.Should().BeOneOf("Windows", "Linux", "macOS");
    }

    [Fact]
    public void Resolve_HostnameAutomatic_ReturnsMachineName()
    {
        var registry = new InMemoryVariableRegistry();
        var hostname = registry.Resolve("HOSTNAME") as string;
        hostname.Should().Be(Environment.MachineName);
    }

    [Fact]
    public void Resolve_PidAutomatic_ReturnsCurrentProcessId()
    {
        var registry = new InMemoryVariableRegistry();
        var pid = registry.Resolve("PID");
        pid.Should().Be(Environment.ProcessId);
    }

    [Fact]
    public void SetAutomatic_PopulatesAutomaticVariable()
    {
        var registry = new InMemoryVariableRegistry();
        registry.SetAutomatic("ERROR", "something failed");
        registry.Resolve("ERROR").Should().Be("something failed");
    }

    [Fact]
    public void SetAutomatic_CanOverrideReadOnlyValue()
    {
        var registry = new InMemoryVariableRegistry();
        registry.SetAutomatic("LASTEXITCODE", 42);
        registry.Resolve("LASTEXITCODE").Should().Be(42);
    }

    [Fact]
    public void Remove_ExistingUserVariable_ReturnsTrueAndRemoves()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("tempvar", 1);
        var result = registry.Remove("tempvar");
        result.Should().BeTrue();
        registry.Resolve("tempvar").Should().BeNull();
    }

    [Fact]
    public void Remove_NonExisting_ReturnsFalse()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Remove("nonexistent").Should().BeFalse();
    }

    [Fact]
    public void Remove_ReadOnlyAutomaticVariable_ReturnsFalse()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Remove("TRUE").Should().BeFalse();
        registry.Resolve("TRUE").Should().Be(true);
    }

    [Fact]
    public void List_AllScopes_ReturnsEverything()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("user1", "v1");
        registry.Set("user2", 2L);
        var all = registry.List();
        all.Should().Contain(kv => kv.Key == "user1" && (string)kv.Value! == "v1");
        all.Should().Contain(kv => kv.Key == "user2" && (long)kv.Value! == 2L);
    }

    [Fact]
    public void List_SessionScope_ReturnsOnlySession()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("sessionvar", "v", VariableScope.Session);
        registry.Set("scriptvar", "v", VariableScope.Script);
        var session = registry.List(VariableScope.Session);
        session.Should().Contain(kv => kv.Key == "sessionvar");
        session.Should().NotContain(kv => kv.Key == "scriptvar");
    }

    [Fact]
    public void Set_WithGlobalScopeModifier_UsesGlobalScope()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("global:modvar", "global-mod-val");
        registry.Resolve("global:modvar").Should().Be("global-mod-val");
    }

    [Fact]
    public void Set_WithScriptScopeModifier_UsesScriptScope()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("script:modvar", "script-mod-val");
        registry.Resolve("script:modvar").Should().Be("script-mod-val");
    }

    [Fact]
    public void Resolve_EnvBridge_ReturnsEnvironmentVariable()
    {
        var registry = new InMemoryVariableRegistry();
        // 使用一个肯定存在的环境变量
        Environment.SetEnvironmentVariable("OPENSHELL_TEST_VAR", "test-value-123");
        try
        {
            var value = registry.Resolve("env:OPENSHELL_TEST_VAR");
            value.Should().Be("test-value-123");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENSHELL_TEST_VAR", null);
        }
    }

    // ---- ADR-0047: ScopeStack behaviour ----

    [Fact]
    public void Set_EnvVar_ActuallyCallsEnvironmentSetEnvironmentVariable()
    {
        // Per ADR-0047 §10.5: 修复 ADR-0042 旧 Set bug (没有调 OS API).
        var registry = new InMemoryVariableRegistry();
        try
        {
            registry.Set("env:OPENSHELL_SET_ENV_TEST", "the-value");
            Environment.GetEnvironmentVariable("OPENSHELL_SET_ENV_TEST").Should().Be("the-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENSHELL_SET_ENV_TEST", null);
        }
    }

    [Fact]
    public void Set_EnvVar_WithNullValue_RemovesEnvironmentVariable()
    {
        var registry = new InMemoryVariableRegistry();
        try
        {
            Environment.SetEnvironmentVariable("OPENSHELL_NULL_ENV_TEST", "initial");
            registry.Set("env:OPENSHELL_NULL_ENV_TEST", null!);
            Environment.GetEnvironmentVariable("OPENSHELL_NULL_ENV_TEST").Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENSHELL_NULL_ENV_TEST", null);
        }
    }

    [Fact]
    public void Resolve_LocalScope_WalksUpToParentFrame()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("base", "from-current");
        using (registry.PushScope())
        {
            // New scope can see parent's "base".
            registry.Resolve("base").Should().Be("from-current");
        }
    }

    [Fact]
    public void Set_InChildScope_DoesNotLeakToParentAfterPop()
    {
        var registry = new InMemoryVariableRegistry();
        using (registry.PushScope())
        {
            registry.Set("temp", "child-only");
            registry.Resolve("temp").Should().Be("child-only");
        }
        // After pop, "temp" should not be visible.
        registry.Resolve("temp").Should().BeNull();
    }

    [Fact]
    public void Set_PrivateModifier_HiddenFromChildScope()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("private:secret", "hidden");
        // Visible in current frame.
        registry.Resolve("secret").Should().Be("hidden");
        using (registry.PushScope())
        {
            // Child scope cannot see parent's private.
            registry.Resolve("secret").Should().BeNull();
        }
    }

    [Fact]
    public void Set_WithPrivateModifier_SetsIsPrivateFlag()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("private:inner", "val");
        var entry = registry.Stack.LookupLocal("inner");
        entry.Should().NotBeNull();
        entry!.IsPrivate.Should().BeTrue();
        entry!.Value.Should().Be("val");
    }

    [Fact]
    public void Set_WithGlobalModifier_WritesToGlobalFrame()
    {
        var registry = new InMemoryVariableRegistry();
        using (registry.PushScope())
        {
            registry.Set("global:glob", "global-value");
        }
        // After pop, the value persists in Global frame.
        registry.Resolve("global:glob").Should().Be("global-value");
        registry.Resolve("glob").Should().Be("global-value");
    }

    [Fact]
    public void Set_WithScriptModifier_WritesToScriptFrame()
    {
        var registry = new InMemoryVariableRegistry();
        using (registry.PushScope())
        {
            registry.Set("script:scr", "script-value");
        }
        // After pop, the value persists in Script frame.
        registry.Resolve("script:scr").Should().Be("script-value");
        registry.Resolve("scr").Should().Be("script-value");
    }

    [Fact]
    public void Set_WithLocalModifier_OnlyWritesToCurrentFrame()
    {
        var registry = new InMemoryVariableRegistry();
        // Pre-populate Global so child can shadow.
        registry.Set("global:shadowed", "global-val");
        using (registry.PushScope())
        {
            registry.Set("local:shadowed", "local-val");
            registry.Resolve("shadowed").Should().Be("local-val");
        }
        // After pop, original Global value visible again.
        registry.Resolve("shadowed").Should().Be("global-val");
    }

    [Fact]
    public void Resolve_UsingModifier_Degenerates_ToLocalLookup()
    {
        // Per ADR-0047 §1.2 + ADR-0046 §4: 在本地上下文中 $using: 退化为闭包读取（Local 查找），
        // 与 ScriptBlock 闭包捕获语义兼容。远程上下文（Invoke-Command / Start-Job）由远程宿主处理。
        var registry = new InMemoryVariableRegistry();
        using (registry.PushScope())
        {
            registry.Set("local:outer", "captured-value");
            registry.Resolve("using:outer").Should().Be("captured-value");
        }
        // 未定义变量返回 null（不抛异常）。
        registry.Resolve("using:undefined").Should().BeNull();
    }

    [Fact]
    public void PushScope_IncreasesDepth()
    {
        var registry = new InMemoryVariableRegistry();
        var initialDepth = registry.Stack.Depth;
        using (registry.PushScope())
        {
            registry.Stack.Depth.Should().Be(initialDepth + 1);
        }
        registry.Stack.Depth.Should().Be(initialDepth);
    }

    [Fact]
    public void PushScope_DisposeAutomatically_PopsStack()
    {
        var registry = new InMemoryVariableRegistry();
        var depthBefore = registry.Stack.Depth;
        {
            using var scope = registry.PushScope();
            // Still in scope.
        }
        registry.Stack.Depth.Should().Be(depthBefore);
    }

    [Fact]
    public void Constructor_AcceptsCustomScopeStack()
    {
        var customStack = new ScopeStack();
        var registry = new InMemoryVariableRegistry(customStack);
        registry.Stack.Should().BeSameAs(customStack);
    }

    [Fact]
    public void List_AllScopes_ReturnsVisibleFromAllFrames()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("global:lister", "g");
        registry.Set("script:lister", "s");
        registry.Set("local:lister", "l");
        // Local should shadow global/script.
        var list = registry.List();
        list.Should().Contain(kv => kv.Key == "lister" && (string)kv.Value! == "l");
    }

    [Fact]
    public void List_SpecificScope_OnlyReturnsThatFrame()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("global:onlyG", "g");
        registry.Set("onlyLocal", "l");
        var globals = registry.List(VariableScope.Global);
        globals.Should().Contain(kv => kv.Key == "onlyG");
        globals.Should().NotContain(kv => kv.Key == "onlyLocal");
    }

    [Fact]
    public void SessionAlias_BehavesIdenticallyToLocal()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("v", "session", VariableScope.Session);
        registry.Resolve("v").Should().Be("session");
        // Resolve via Local modifier also finds it.
        registry.Resolve("local:v").Should().Be("session");
    }

    [Fact]
    public void Set_DuplicateName_InDifferentScopes_DoesNotThrow()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("global:x", "g");
        registry.Set("x", "l");
        using (registry.PushScope())
        {
            registry.Set("x", "child");
            registry.Resolve("x").Should().Be("child");
        }
        // Back to outer scope.
        registry.Resolve("x").Should().Be("l");
    }

    [Fact]
    public void Remove_InCurrentScope_DoesNotAffectParent()
    {
        var registry = new InMemoryVariableRegistry();
        registry.Set("global:r", "global-val");
        using (registry.PushScope())
        {
            // Cannot remove from parent via current scope Remove.
            registry.Remove("r").Should().BeFalse();
            // Still visible (via parent).
            registry.Resolve("r").Should().Be("global-val");
        }
    }

    [Fact]
    public void Set_EnvVar_PersistsAcrossScopePush()
    {
        var registry = new InMemoryVariableRegistry();
        try
        {
            using (registry.PushScope())
            {
                registry.Set("env:OPENSHELL_PERSIST_TEST", "v1");
            }
            Environment.GetEnvironmentVariable("OPENSHELL_PERSIST_TEST").Should().Be("v1");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENSHELL_PERSIST_TEST", null);
        }
    }
}
