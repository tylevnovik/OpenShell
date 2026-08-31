namespace OpenShell.Providers.Remote;

/// <summary>
/// SFTP 凭据提供者接口 (ADR-0019 §3)。
/// Per ADR-0019 约束: <c>GetCredentials</c> 失败不抛异常, 返回 null —— 由命令层提示用户配置。
/// 这是任务规格里的简化同步接口 (区别于 ADR-0019 的异步 <c>GetAsync</c>)，因为 M4 仅需
/// 进程内缓存查询；持久化由具体实现交给受保护的秘密存储。
/// </summary>
public interface ICredentialProvider
{
    /// <summary>
    /// 按 host + user 查询凭据。Per ADR-0019: 未配置时返回 null, 不抛异常。
    /// </summary>
    SftpCredentials? GetCredentials(string host, string user);
}

/// <summary>
/// SFTP 凭据记录。immutable record，host/user/port 必填；password 与 private key 二选一。
/// Per ADR-0019: 凭据禁止以明文形式记录到日志。
/// password 与 private-key passphrase 不应进入凭据 JSON 或日志。
/// </summary>
public sealed record SftpCredentials
{
    /// <summary>远程主机 (hostname or IP)。不含端口。</summary>
    public required string Host { get; init; }

    /// <summary>登录用户名。</summary>
    public required string User { get; init; }

    /// <summary>SSH 端口，默认 22。</summary>
    public int Port { get; init; } = 22;

    /// <summary>
    /// 密码 (与 <see cref="PrivateKeyPath"/> 二选一)。明文存储 TODO 加密。
    /// </summary>
    public string? Password { get; init; }

    /// <summary>SSH 私钥文件本地路径。</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>私钥文件的 passphrase；无保护则为 null。</summary>
    public string? PrivateKeyPassphrase { get; init; }
}
