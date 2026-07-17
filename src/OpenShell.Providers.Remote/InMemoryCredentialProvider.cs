using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenShell.Providers.Remote;

/// <summary>
/// 默认的 <see cref="ICredentialProvider"/> 实现: 从 <c>~/.openshell/sftp-credentials.json</c>
/// 加载凭据到内存, 支持 Set/Get/Remove 操作并持久化。
/// Per ADR-0019 §3: M4 简化版, password 暂以明文 JSON 存储, 文件权限 0600 (Unix)。
/// TODO(M4+): 用 DPAPI (Windows) / OS keychain (Unix) 加密存储 password 字段。
/// </summary>
/// <remarks>
/// 凭据文件格式 (JSON 数组):
/// <code>
/// [
///   {
///     "host": "example.com",
///     "user": "alice",
///     "port": 22,
///     "password": "secret",
///     "privateKeyPath": null,
///     "privateKeyPassphrase": null
///   }
/// ]
/// </code>
/// 文件不存在时返回空列表 (用户通过 set-sftpcredential 命令配置); 之后自动创建并写入。
/// </remarks>
public sealed class InMemoryCredentialProvider : ICredentialProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly List<SftpCredentials> _creds = new();
    private readonly object _lock = new();

    /// <summary>使用默认路径 <c>~/.openshell/sftp-credentials.json</c>。</summary>
    public InMemoryCredentialProvider() : this(GetDefaultFilePath()) { }

    /// <summary>使用指定路径, 主要用于测试。</summary>
    public InMemoryCredentialProvider(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        TryLoadFromFile();
    }

    /// <inheritdoc />
    public SftpCredentials? GetCredentials(string host, string user)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(user);
        lock (_lock)
        {
            return _creds.FirstOrDefault(c =>
                string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.User, user, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>列出所有已配置的凭据 (按 host 排序, 不含 password 字段值, 仅保留 HasPassword 标志)。</summary>
    public IReadOnlyList<SftpCredentials> ListCredentials(string? hostFilter = null)
    {
        lock (_lock)
        {
            var query = _creds.AsEnumerable();
            if (!string.IsNullOrEmpty(hostFilter))
                query = query.Where(c => string.Equals(c.Host, hostFilter, StringComparison.OrdinalIgnoreCase));
            return query
                .OrderBy(c => c.Host, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.User, StringComparer.OrdinalIgnoreCase)
                .Select(c => c with { Password = c.Password is null ? null : "****" })
                .ToImmutableList();
        }
    }

    /// <summary>
    /// 新增或更新凭据 (按 host + user 主键去重)。修改后立即持久化到磁盘。
    /// </summary>
    public void SetCredentials(SftpCredentials cred)
    {
        ArgumentNullException.ThrowIfNull(cred);
        lock (_lock)
        {
            // 先移除同 host+user 的旧凭据, 再追加新的。
            _creds.RemoveAll(c =>
                string.Equals(c.Host, cred.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.User, cred.User, StringComparison.OrdinalIgnoreCase));
            _creds.Add(cred);
            TrySaveToFile();
        }
    }

    /// <summary>
    /// 删除凭据。返回是否删除成功。
    /// </summary>
    public bool RemoveCredentials(string host, string? user = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (_lock)
        {
            var removed = _creds.RemoveAll(c =>
                string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase)
                && (user is null || string.Equals(c.User, user, StringComparison.OrdinalIgnoreCase)));
            if (removed > 0)
                TrySaveToFile();
            return removed > 0;
        }
    }

    // ---- 内部: 文件 IO ----

    private void TryLoadFromFile()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var list = JsonSerializer.Deserialize<List<CredRecord>>(json, JsonOpts);
            if (list is null)
                return;

            lock (_lock)
            {
                _creds.Clear();
                foreach (var r in list)
                {
                    if (string.IsNullOrEmpty(r.Host) || string.IsNullOrEmpty(r.User))
                        continue;   // 跳过无效条目
                    _creds.Add(new SftpCredentials
                    {
                        Host = r.Host,
                        User = r.User,
                        Port = r.Port <= 0 ? 22 : r.Port,
                        Password = r.Password,
                        PrivateKeyPath = r.PrivateKeyPath,
                        PrivateKeyPassphrase = r.PrivateKeyPassphrase,
                    });
                }
            }
        }
        catch
        {
            // Per ADR-0019: 凭据加载失败不阻塞启动, 降级到空凭据列表。
            // 真实部署中用户可手动修复或删除文件后重新配置。
        }
    }

    private void TrySaveToFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var list = _creds.Select(c => new CredRecord
            {
                Host = c.Host,
                User = c.User,
                Port = c.Port,
                Password = c.Password,
                PrivateKeyPath = c.PrivateKeyPath,
                PrivateKeyPassphrase = c.PrivateKeyPassphrase,
            }).ToList();

            var json = JsonSerializer.Serialize(list, JsonOpts);

            // 先写入临时文件再原子替换, 避免中途崩溃导致凭据文件损坏。
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_filePath))
                File.Replace(tmp, _filePath, destinationBackupFileName: null);
            else
                File.Move(tmp, _filePath);

            // Unix: 设置文件权限 0600 (仅 owner 可读写)。Windows 用 ACL, 此处不处理。
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(_filePath,
                        System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
                }
                catch
                {
                    // 平台不支持 SetUnixFileMode 时忽略。
                }
            }
        }
        catch
        {
            // 持久化失败不抛: 内存中的凭据仍可用于本次会话, 下次启动丢失。
        }
    }

    private static string GetDefaultFilePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".openshell", "sftp-credentials.json");
    }

    /// <summary>JSON 序列化 DTO, 与 <see cref="SftpCredentials"/> 字段对应 (camelCase)。</summary>
    private sealed record CredRecord
    {
        public string Host { get; init; } = "";
        public string User { get; init; } = "";
        public int Port { get; init; } = 22;
        public string? Password { get; init; }
        public string? PrivateKeyPath { get; init; }
        public string? PrivateKeyPassphrase { get; init; }
    }
}
