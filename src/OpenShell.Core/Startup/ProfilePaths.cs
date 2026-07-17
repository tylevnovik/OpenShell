namespace OpenShell.Startup;

/// <summary>
/// <c>$PROFILE</c> 自动变量的值类型。Per ADR-0041 §7.
/// 暴露 <see cref="AllUsersAllHosts"/> / <see cref="CurrentUserAllHosts"/> /
/// <see cref="CurrentUserCurrentHost"/> 子字段，首期均返回同一文件路径
/// （简化实现，预留 <see cref="AllUsersAllHosts"/> 为未来系统级 profile 扩展占位）。
/// <see cref="ToString"/> 返回当前 profile 文件路径，用于字符串插值与比较。
/// </summary>
public sealed class ProfilePaths
{
    /// <summary>当前 profile 文件路径（<c>$PROFILE</c> 默认引用值）。</summary>
    public string CurrentProfile { get; }

    /// <summary>全用户全 Host profile 路径（保留字段，首期返回当前 profile 路径）。</summary>
    public string AllUsersAllHosts { get; }

    /// <summary>当前用户全 Host profile 路径。</summary>
    public string CurrentUserAllHosts { get; }

    /// <summary>当前用户当前 Host profile 路径（Cli / Gui 区分，首期与全 Host 同路径）。</summary>
    public string CurrentUserCurrentHost { get; }

    /// <summary>构造 ProfilePaths。</summary>
    /// <param name="currentProfile">当前 profile 文件路径（<c>$PROFILE</c> 默认值）。</param>
    /// <param name="allUsersAllHosts">全用户全 Host profile 路径。</param>
    /// <param name="currentUserAllHosts">当前用户全 Host profile 路径。</param>
    /// <param name="currentUserCurrentHost">当前用户当前 Host profile 路径。</param>
    public ProfilePaths(
        string currentProfile,
        string allUsersAllHosts,
        string currentUserAllHosts,
        string currentUserCurrentHost)
    {
        CurrentProfile = currentProfile ?? throw new ArgumentNullException(nameof(currentProfile));
        AllUsersAllHosts = allUsersAllHosts ?? throw new ArgumentNullException(nameof(allUsersAllHosts));
        CurrentUserAllHosts = currentUserAllHosts ?? throw new ArgumentNullException(nameof(currentUserAllHosts));
        CurrentUserCurrentHost = currentUserCurrentHost ?? throw new ArgumentNullException(nameof(currentUserCurrentHost));
    }

    /// <summary>返回当前 profile 文件路径，供字符串插值 / 比较使用。</summary>
    public override string ToString() => CurrentProfile;
}
