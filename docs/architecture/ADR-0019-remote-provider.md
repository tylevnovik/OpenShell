# ADR-0019: Remote Provider 抽象

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M4
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0001 (能力), ADR-0007 (操作引擎), ADR-0009 (补全缓存)

## Context

M4 需要支持远程 / 云存储：

- **S3**：AWS / MinIO / 阿里云 OSS / 腾讯云 COS（S3 兼容）
- **WebDAV**：Nextcloud / ownCloud / 常见 WebDAV 服务
- **SFTP / SSH**：远程主机文件系统
- **Azure Blob**（可选）
- **Google Cloud Storage**（可选）

需求：

1. **凭据管理**：access key / secret / token / SSH 私钥，需安全存储
2. **重试与熔断**：网络抖动自动重试，连续失败熔断
3. **断流续传**：大文件上传中断后可续传
4. **并发控制**：S3 列举可分页并发，单连接限速
5. **缓存**：列举结果短期缓存（补全用 ADR-0009）
6. **配置文件**：用户配置多个远程账户，`s3://my-bucket/` 自动选凭据
7. **跨平台**：Linux/Mac 同样可用
8. **凭据安全**：access key 不能落明文，必须 OS 加密存储
9. **流量统计**：上传 / 下载字节数可观测

挑战：

- 不同协议 API 差异大（S3 是对象存储无目录概念，WebDAV 是层级，SFTP 是 POSIX）
- 凭据刷新（STS 临时 token 过期需刷新）
- 大文件分块（S3 multipart upload，SFTP 必须单流）
- 错误恢复语义不同

## Decision

### 1. 抽象基类 + 协议适配器

```csharp
public abstract class RemoteProviderBase : IProvider,
    IItemProvider, IContainerProvider, INavigationProvider,
    IContentProvider, IContentWriterProvider, IPropertyProvider, IDriveProvider
{
    protected abstract IRemoteAdapter Adapter { get; }

    public async ValueTask<IItem?> GetItemAsync(ItemPath path, CancellationToken ct)
    {
        var (account, key) = ParsePath(path);
        var entry = await Adapter.StatAsync(account, key, ct);
        return entry is null ? null : ToItem(path, entry);
    }

    public async IAsyncEnumerable<IItem> GetChildrenAsync(
        ItemPath path, EnumerationOptions opts, [EnumeratorCancellation] CancellationToken ct)
    {
        var (account, prefix) = ParsePath(path);
        await foreach (var entry in Adapter.ListAsync(account, prefix, opts, ct))
            yield return ToItem(path.Combine(entry.Name), entry);
    }

    // 其他方法类似，全部委托 Adapter
}

public interface IRemoteAdapter
{
    ValueTask<RemoteEntry?> StatAsync(string account, string key, CancellationToken ct);
    IAsyncEnumerable<RemoteEntry> ListAsync(string account, string prefix, EnumerationOptions opts, CancellationToken ct);
    ValueTask<Stream> OpenReadAsync(string account, string key, CancellationToken ct);
    ValueTask<Stream> OpenWriteAsync(string account, string key, long? expectedSize, CancellationToken ct);
    ValueTask DeleteAsync(string account, string key, CancellationToken ct);
    ValueTask RenameAsync(string account, string oldKey, string newKey, CancellationToken ct);
    ValueTask<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct);
}
```

实现：

- `S3Adapter`：基于 `AWSSDK.S3`
- `WebDAVAdapter`：基于 `WebDav.Client`
- `SFTPAdapter`：基于 `SSH.NET`

### 2. 路径模型

| 协议 | 路径示例 | 解析 |
|---|---|---|
| S3 | `s3://my-bucket/path/to/file.txt` | account = `my-bucket`, key = `path/to/file.txt` |
| S3 (兼容 URL) | `s3://my-bucket.s3.amazonaws.com/file` | account = `my-bucket`, key = `file` |
| WebDAV | `webdav::https://server/dav/path` | account = `https://server/dav/`, key = `path` |
| SFTP | `sftp://user@host:22/path/to/file` | account = `user@host:22`, key = `path/to/file` |

`ParsePath` 在 `RemoteProviderBase` 中实现，按 Provider 类型分发。

### 3. 凭据管理

```csharp
public interface ICredentialProvider
{
    ValueTask<Credentials?> GetAsync(string accountKey, CancellationToken ct);
    ValueTask SetAsync(string accountKey, Credentials credentials, CancellationToken ct);
    ValueTask DeleteAsync(string accountKey, CancellationToken ct);
}

public sealed record Credentials(
    string Type,                    // "access-key" / "bearer-token" / "ssh-key" / "basic"
    IReadOnlyDictionary<string, string> Fields);
```

实现：
- **Windows**：DPAPI 加密，存于 `%APPDATA%/OpenShell/credentials.enc`
- **Linux/Mac**：OS keychain（`SecretStorage` / `Security.framework`），fallback 文件 + 600 权限
- 内存中缓存解密后的凭据，进程退出后释放

凭据刷新（STS 临时 token）：`IRemoteAdapter.RefreshCredentialsAsync` 接口，Provider 在 401/403 时调用。

### 4. 重试与熔断

引入 `Polly` 策略：

```csharp
var retry = Policy
    .Handle<HttpRequestException>()
    .Or<TimeoutException>()
    .Or<S3Exception>(ex => ex.StatusCode is >= 500 or >= 503)
    .WaitAndRetryAsync(
        retryCount: 5,
        sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(new Random().Next(0, 500)),
        onRetry: (ex, delay, attempt, ctx) =>
        {
            _log.LogWarning("Retry {Attempt} after {Delay}s: {Message}", attempt, delay.TotalSeconds, ex.Message);
        });

var breaker = Policy.Handle<Exception>().CircuitBreakerAsync(
    exceptionsAllowedBeforeBreaking: 10,
    durationOfBreak: TimeSpan.FromSeconds(30));
```

`RemoteProviderBase` 包装每个 Adapter 调用：`retry.Wrap(breaker).ExecuteAsync(...)`。

熔断打开时 Provider 抛 `CircuitBrokenException`，上层显示"远程不可用，请稍后重试"。

### 5. 断流续传

S3 multipart upload：

- 文件 > 8MB 启用 multipart
- 每块 8MB，并发 4 块上传
- 上传中断后，记录 `UploadId` 到 `~/.openshell/uploads/{hash}.json`
- 重新上传同一文件时检查 `UploadId` 是否有效（S3 list parts），从断点续传

WebDAV / SFTP 无原生 multipart：

- WebDAV 用 `Content-Range` 头（服务端支持时）
- SFTP 用 `FileStream.Seek` 续传（要求服务端保留部分文件）

### 6. 配置文件

`~/.openshell/remotes.toml`：

```toml
[[s3]]
name = "my-aws"
endpoint = "s3.amazonaws.com"
region = "us-east-1"
accessKeyId = "AKIA..."        # 仅参考，真实凭据在 keychain
# credentialKey = "s3::my-aws"   # 凭据在 ICredentialProvider 中查找的 key

[[s3]]
name = "minio-local"
endpoint = "http://localhost:9000"
forcePathStyle = true

[[webdav]]
name = "nextcloud"
url = "https://nc.example.com/remote.php/dav/files/user"

[[sftp]]
name = "home-server"
host = "192.168.1.10"
port = 22
user = "me"
```

启动时 `RemoteProvider` 加载配置，注册到 `ICredentialProvider`。

### 7. 缓存策略

| 数据 | TTL | 失效条件 |
|---|---|---|
| 目录列举 | 5s | TTL |
| 单 Item 元信息 | 5s | TTL |
| 文件内容 | 不缓存 | - |
| 凭据 | 进程生命周期 | 用户主动删除 |

写操作（upload / delete）成功后，相关路径缓存立即失效。

### 8. 流量统计

`IRemoteAdapter` 调用包装 `CountingStream`，统计字节，发布到 `IMetricsCollector`：

- 每秒字节数
- 累计上传 / 下载
- 错误率

GUI 状态栏显示实时速率，CLI 用 `--verbose` 输出。

### 9. Provider 注册

每个协议一个 Provider 实例：

- `s3` Provider 处理所有 S3 兼容（含 MinIO）
- `webdav` Provider 处理 WebDAV
- `sftp` Provider 处理 SFTP

用户配置多个账户时，`s3://bucket1/...` 与 `s3://bucket2/...` 用同一 Provider，按 endpoint 选凭据。

### 10. 大目录列举

S3 `ListObjectsV2` 单次最多 1000 项，需分页：

- `IAsyncEnumerable<RemoteEntry>` 内部自动翻页
- 用户中断时立即停止
- 分页 token 不暴露给用户

## Alternatives Considered

1. **每协议独立 Provider（无基类）**：被否决，凭据 / 重试 / 缓存代码大量重复
2. **rclone 子进程**：被否决，依赖外部二进制，调试困难
3. **WebDAV 用 `WebClient`**：被否决，无重试 / 无异步
4. **不实现断流续传**：被否决，大文件用户体验差
5. **凭据存配置文件明文**：被否决，安全风险
6. **rclone 风格的协议无关抽象**：被否决，过度抽象，丢协议特性

## Consequences

### 优势
- 统一凭据 / 重试 / 缓存 / 流量统计
- 新协议只需实现 `IRemoteAdapter`
- 跨平台（DPAPI / keychain）
- 断流续传提升体验
- 大目录自动分页

### 代价
- `AWSSDK.S3` + `SSH.NET` + `WebDav.Client` 依赖较重（总 ~5MB）
- 凭据 OS 集成在 CI 环境难测（需 mock）
- multipart 续传的 `UploadId` 持久化需小心
- 协议差异（S3 无目录）需在 Provider 层模拟

### 约束
- `RemoteProviderBase` 必须对所有 Adapter 调用包装 Polly 重试 + 熔断
- 凭据禁止以明文形式记录到日志（`LogWarning("retry for {Account}", account)` 而非 key）
- `ICredentialProvider.GetAsync` 失败不抛异常，返回 null（命令层提示用户配置）
- 大文件分块大小默认 8MB，可通过 Provider 配置覆盖
- multipart upload 的 `UploadId` 持久化路径必须可由用户清理（`~/.openshell/uploads/`）
- 熔断打开后 Provider 必须立即返回错误，不尝试新请求
- S3 list 必须自动分页，单页限制不暴露给上层
- `CountingStream` 必须在 dispose 时发布最终统计
- 凭据刷新失败时必须通知用户（GUI 状态栏 / CLI 错误），不静默重试到无限
- 远程 Provider 必须 `IDisposable`，unload 时关闭所有 HTTP / SSH 连接
