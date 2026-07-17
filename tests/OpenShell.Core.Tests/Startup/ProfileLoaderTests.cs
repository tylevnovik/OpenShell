using FluentAssertions;
using OpenShell.Errors;
using OpenShell.Startup;
using OpenShell.Variables;
using Xunit;

namespace OpenShell.Core.Tests.Startup;

/// <summary>
/// ProfileLoader / ProfilePaths 单元测试。Per ADR-0041 §7.
/// 验证 $PROFILE 变量结构、字段语义与 --profile / --noprofile 行为。
/// </summary>
public class ProfileLoaderTests
{
    private static InMemoryErrorStream CreateErrors() => new();

    // ---- GetProfilePaths: 默认 (无 --profile) ----

    [Fact]
    public void GetProfilePaths_Default_ReturnsUserGlobalPath()
    {
        var loader = new ProfileLoader(CreateErrors());
        var paths = loader.GetProfilePaths();

        paths.CurrentProfile.Should().NotBeNullOrEmpty();
        paths.CurrentProfile.Should().EndWith("profile.openshell");
        paths.CurrentProfile.Should().Contain(".openshell");
    }

    [Fact]
    public void GetProfilePaths_Default_AllFieldsReturnSamePath()
    {
        // Per ADR-0041 §7 简化说明：首期三个子字段均返回同一文件路径。
        var loader = new ProfileLoader(CreateErrors());
        var paths = loader.GetProfilePaths();

        paths.AllUsersAllHosts.Should().Be(paths.CurrentProfile);
        paths.CurrentUserAllHosts.Should().Be(paths.CurrentProfile);
        paths.CurrentUserCurrentHost.Should().Be(paths.CurrentProfile);
    }

    // ---- GetProfilePaths: --profile <path> ----

    [Fact]
    public void GetProfilePaths_CustomPath_CurrentProfileReturnsCustom()
    {
        var loader = new ProfileLoader(CreateErrors())
        {
            CustomProfilePath = "/custom/my-profile.openshell",
        };
        var paths = loader.GetProfilePaths();

        paths.CurrentProfile.Should().Be("/custom/my-profile.openshell");
    }

    [Fact]
    public void GetProfilePaths_CustomPath_SubFieldsReturnDefault()
    {
        // --profile 指定时，子字段仍返回默认用户全局路径（保留字段语义）。
        var loader = new ProfileLoader(CreateErrors())
        {
            CustomProfilePath = "/custom/my-profile.openshell",
        };
        var defaultLoader = new ProfileLoader(CreateErrors());
        var defaultPaths = defaultLoader.GetProfilePaths();

        var paths = loader.GetProfilePaths();

        paths.AllUsersAllHosts.Should().Be(defaultPaths.AllUsersAllHosts);
        paths.CurrentUserAllHosts.Should().Be(defaultPaths.CurrentUserAllHosts);
        paths.CurrentUserCurrentHost.Should().Be(defaultPaths.CurrentUserCurrentHost);
    }

    [Fact]
    public void GetProfilePaths_WhitespaceCustomPath_TreatedAsUnset()
    {
        var loader = new ProfileLoader(CreateErrors())
        {
            CustomProfilePath = "   ",
        };
        var paths = loader.GetProfilePaths();

        paths.CurrentProfile.Should().NotBe("   ");
        paths.CurrentProfile.Should().EndWith("profile.openshell");
    }

    // ---- ExecuteAsync: $PROFILE 变量设置 ----

    [Fact]
    public async Task ExecuteAsync_SetsProfileVariable_InRegistry()
    {
        var errors = CreateErrors();
        var vars = new InMemoryVariableRegistry();
        var loader = new ProfileLoader(errors, vars);

        await loader.ExecuteAsync(_ => Task.CompletedTask, default);

        var profile = vars.Resolve("PROFILE");
        profile.Should().NotBeNull();
        profile.Should().BeOfType<ProfilePaths>();
    }

    [Fact]
    public async Task ExecuteAsync_ProfileVariable_HasCorrectFields()
    {
        var errors = CreateErrors();
        var vars = new InMemoryVariableRegistry();
        var loader = new ProfileLoader(errors, vars);

        await loader.ExecuteAsync(_ => Task.CompletedTask, default);

        var profile = (ProfilePaths)vars.Resolve("PROFILE")!;
        profile.CurrentProfile.Should().NotBeNullOrEmpty();
        profile.AllUsersAllHosts.Should().NotBeNullOrEmpty();
        profile.CurrentUserAllHosts.Should().NotBeNullOrEmpty();
        profile.CurrentUserCurrentHost.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_SkipProfile_StillSetsProfileVariable()
    {
        // --noprofile 也应设置 $PROFILE，便于用户查询 profile 文件位置。
        var errors = CreateErrors();
        var vars = new InMemoryVariableRegistry();
        var loader = new ProfileLoader(errors, vars)
        {
            SkipProfile = true,
        };

        await loader.ExecuteAsync(_ => Task.CompletedTask, default);

        var profile = vars.Resolve("PROFILE");
        profile.Should().NotBeNull();
        profile.Should().BeOfType<ProfilePaths>();
    }

    [Fact]
    public async Task ExecuteAsync_CustomProfile_SetsProfileVariableWithCustomPath()
    {
        var errors = CreateErrors();
        var vars = new InMemoryVariableRegistry();
        var loader = new ProfileLoader(errors, vars)
        {
            CustomProfilePath = "/custom/profile.openshell",
        };

        await loader.ExecuteAsync(_ => Task.CompletedTask, default);

        var profile = (ProfilePaths)vars.Resolve("PROFILE")!;
        profile.CurrentProfile.Should().Be("/custom/profile.openshell");
    }

    [Fact]
    public async Task ExecuteAsync_NoVariablesRegistry_DoesNotThrow()
    {
        // 不注入 IVariableRegistry 时，ProfileLoader 不应抛异常。
        var errors = CreateErrors();
        var loader = new ProfileLoader(errors);

        var act = async () => await loader.ExecuteAsync(_ => Task.CompletedTask, default);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExecuteAsync_ProfileVariableIsReadOnly()
    {
        var errors = CreateErrors();
        var vars = new InMemoryVariableRegistry();
        var loader = new ProfileLoader(errors, vars);

        await loader.ExecuteAsync(_ => Task.CompletedTask, default);

        vars.IsReadOnly("PROFILE").Should().BeTrue();
    }

    // ---- ProfilePaths: ToString / MemberAccessor 集成 ----

    [Fact]
    public void ProfilePaths_ToString_ReturnsCurrentProfile()
    {
        var paths = new ProfilePaths(
            currentProfile: "/home/user/.openshell/profile.openshell",
            allUsersAllHosts: "/home/user/.openshell/profile.openshell",
            currentUserAllHosts: "/home/user/.openshell/profile.openshell",
            currentUserCurrentHost: "/home/user/.openshell/profile.openshell");

        paths.ToString().Should().Be("/home/user/.openshell/profile.openshell");
    }

    [Fact]
    public void ProfilePaths_MemberAccessor_ReturnsAllUsersAllHosts()
    {
        var paths = new ProfilePaths(
            currentProfile: "/a/profile.openshell",
            allUsersAllHosts: "/all/profile.openshell",
            currentUserAllHosts: "/user-all/profile.openshell",
            currentUserCurrentHost: "/user-host/profile.openshell");

        MemberAccessor.GetProperty(paths, "AllUsersAllHosts").Should().Be("/all/profile.openshell");
        MemberAccessor.GetProperty(paths, "CurrentUserAllHosts").Should().Be("/user-all/profile.openshell");
        MemberAccessor.GetProperty(paths, "CurrentUserCurrentHost").Should().Be("/user-host/profile.openshell");
        MemberAccessor.GetProperty(paths, "CurrentProfile").Should().Be("/a/profile.openshell");
    }

    [Fact]
    public void ProfilePaths_MemberAccessor_CaseInsensitive()
    {
        var paths = new ProfilePaths(
            currentProfile: "/a/profile.openshell",
            allUsersAllHosts: "/all/profile.openshell",
            currentUserAllHosts: "/user-all/profile.openshell",
            currentUserCurrentHost: "/user-host/profile.openshell");

        MemberAccessor.GetProperty(paths, "allusersallhosts").Should().Be("/all/profile.openshell");
        MemberAccessor.GetProperty(paths, "CURRENTUSERCURRENTHOST").Should().Be("/user-host/profile.openshell");
    }

    [Fact]
    public void ProfilePaths_Constructor_NullCurrentProfile_Throws()
    {
        var act = () => new ProfilePaths(
            currentProfile: null!,
            allUsersAllHosts: "/a",
            currentUserAllHosts: "/b",
            currentUserCurrentHost: "/c");
        act.Should().Throw<ArgumentNullException>().WithParameterName("currentProfile");
    }

    [Fact]
    public void ProfilePaths_Constructor_NullAllUsersAllHosts_Throws()
    {
        var act = () => new ProfilePaths(
            currentProfile: "/a",
            allUsersAllHosts: null!,
            currentUserAllHosts: "/b",
            currentUserCurrentHost: "/c");
        act.Should().Throw<ArgumentNullException>().WithParameterName("allUsersAllHosts");
    }

    [Fact]
    public void ProfilePaths_Constructor_NullCurrentUserAllHosts_Throws()
    {
        var act = () => new ProfilePaths(
            currentProfile: "/a",
            allUsersAllHosts: "/b",
            currentUserAllHosts: null!,
            currentUserCurrentHost: "/c");
        act.Should().Throw<ArgumentNullException>().WithParameterName("currentUserAllHosts");
    }

    [Fact]
    public void ProfilePaths_Constructor_NullCurrentUserCurrentHost_Throws()
    {
        var act = () => new ProfilePaths(
            currentProfile: "/a",
            allUsersAllHosts: "/b",
            currentUserAllHosts: "/c",
            currentUserCurrentHost: null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("currentUserCurrentHost");
    }
}
