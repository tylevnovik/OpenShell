# ADR-0017: Archive Provider 抽象

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M4
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0001 (能力), ADR-0006 (路径), ADR-0007 (操作引擎)

## Context

M4 需要让用户像浏览目录一样浏览 zip / tar / gz / 7z 等压缩包：

```
cd zip::archive.zip/subdir/file.txt
get-childitem zip::archive.zip
copy-item zip::archive.zip/readme.md fs::C:/temp/
```

需求：

1. **只读浏览**：M4 不支持修改（M5+ 才考虑写）
2. **流式访问**：不预先解压整个包到磁盘，按需读取 entry
3. **虚拟挂载**：`zip::archive.zip/sub` 像真实目录，但实际是 zip 内部
4. **多格式支持**：zip / tar / gz / 7z / rar 至少支持前三种
5. **嵌套压缩**：`tar.gz` = gz(tar(...))，作为单层还是双层暴露？
6. **大文件支持**：100GB zip 不应 OOM
7. **进度反馈**：枚举大 zip 的 entry 时可取消
8. **缓存**：同一 zip 的多次访问应共享 zip 目录索引

PowerShell 通过 `System.IO.Compression.ZipArchive` 直接读 zip，但暴露的是 `ZipArchiveEntry` 而非 `IItem`，需 Provider 适配。tar.gz 需 `SharpZipLib` 或 `ZstdNet`。

## Decision

### 1. ArchiveProvider 实现的能力

| 能力 | 实现 | 说明 |
|---|---|---|
| Item | ✅ | entry 元信息 |
| Container | ✅ | 包内目录结构 |
| Navigation | ✅ | 虚拟路径 |
| Content | ✅ | entry 内容流 |
| ContentWrite | ❌ | M5+ 支持（需重打包） |
| Property | ✅ | 压缩方式、CRC、原始大小等 |
| Security | ❌ | zip 一般无 ACL |
| Drive | ✅(虚拟) | `zip::archive.zip` 整包挂载 |

### 2. 多格式适配器

```csharp
public interface IArchiveAdapter : IDisposable
{
    /// <summary>枚举包内所有 entry（含目录），流式返回。</summary>
    IAsyncEnumerable<ArchiveEntry> EnumerateEntriesAsync(CancellationToken ct);

    /// <summary>打开指定 entry 的读取流。调用方负责 dispose。</summary>
    Stream OpenEntryStream(ArchiveEntry entry);

    /// <summary>包的根路径（如 "C:/path/to/archive.zip"）。</summary>
    string ArchivePath { get; }
}

public sealed record ArchiveEntry(
    string Path,                    // 相对包根的内部路径，统一用 '/' 分隔
    bool IsDirectory,
    long CompressedSize,
    long UncompressedSize,
    DateTimeOffset LastModified,
    string? CompressionMethod);

public interface IArchiveAdapterFactory
{
    /// <summary>能否处理该文件类型（按扩展名 + magic number 判断）。</summary>
    bool CanHandle(string filePath, byte[]? magicBytes);

    /// <summary>打开一个 archive。</summary>
    ValueTask<IArchiveAdapter> OpenAsync(string filePath, CancellationToken ct);
}
```

实现：
- `ZipArchiveAdapterFactory`：基于 `System.IO.Compression.ZipFile`（BCL 内置）
- `TarArchiveAdapterFactory`：基于 `SharpZipLib.Tar`
- `GZipArchiveAdapterFactory`：基于 `System.IO.Compression.GZipStream`（单文件场景，.gz 内是普通文件）
- `TarGzArchiveAdapterFactory`：组合 `GZipStream` + `TarArchive`，作为单层暴露（不暴露中间 tar）

### 3. 虚拟挂载为 ProviderDrive

`zip::archive.zip/sub` 的语义：

- 用户执行 `mount archive.zip` → 创建虚拟 Drive
- `ProviderDrive.Name = "archive.zip"`
- `ProviderDrive.Root = ItemPath { Provider = "zip", InternalPath = "archive.zip" }`
- `ProviderDrive.DisplayLabel = "Archive: archive.zip"`
- 卸载用 `unmount archive.zip`

未显式 mount 时，`cd zip::archive.zip/sub` 自动按需打开 archive（缓存 adapter）。

### 4. 路径映射

`ItemPath` 的 `InternalPath` 格式：

```
zip::C:/path/to/archive.zip/subdir/file.txt
└─provider──┘└──archive path────────┘└─internal entry path─┘
```

实现内部把路径切成：
- `archivePath = "C:/path/to/archive.zip"`
- `entryPath = "subdir/file.txt"`

切分规则：第一个 `/` 之后的路径作为 entry 路径；archive 路径本身用 FS Provider 校验存在性。

### 5. Cache 与并发

`ArchiveProvider` 内部维护 `ConcurrentDictionary<string, IArchiveAdapter>`：

- key = `archivePath`
- `GetChildrenAsync` 检查缓存，未命中时调 `IArchiveAdapterFactory.OpenAsync` 加载
- 同一 archive 可被多个命令并发读，entry stream 各自独立
- `IDisposable.Dispose` 在 provider unload 时关闭所有 adapter

LRU 驱逐：默认保留 5 个 archive 打开，超出按访问时间驱逐。

### 6. 嵌套压缩

`tar.gz` 由 `TarGzArchiveAdapterFactory` 透明处理为单层 tar。用户若想访问 gz 内的 tar：

- `tar.gz` 整体作为一个 ProviderDrive 暴露
- 内部 entry 路径就是 tar 的 entry 路径
- 不暴露"中间 gz 流"作为单独 Item

嵌套 `zip::a.zip/b.zip/...` 暂不支持，文档明确说明（M5+ 评估）。

### 7. 操作引擎集成

`Copy-Item zip::archive.zip/file.txt fs::C:/temp/` 流程：

1. `IOperationEngine.CopyAsync` 解析 source / dest
2. Source 走 `IContentProvider.OpenReadAsync`（Archive 返回 entry stream）
3. Dest 走 `IContentWriterProvider.OpenWriteAsync`（FS 创建新文件）
4. Stream 中转，按 64KB 块拷贝，进度更新

不支持反向（FS → zip），M5+ 重打包后支持。

### 8. 删除语义

`Remove-Item zip::archive.zip/file.txt`：

- M4 不支持，抛 `NotSupportedException("Archive is read-only in M4")`
- M5+ 实现"重建 archive 排除该 entry"

## Alternatives Considered

1. **临时解压到磁盘**：被否决，大 archive 爆炸
2. **不挂载为 Provider，仅 `extract-archive` 命令**：被否决，体验差，无法 `cd` 进去
3. **PowerShell `Expand-Archive` 风格**：被否决，只支持 zip，且无路径抽象
4. **暴露 gz(tar) 两层**：被否决，用户心智负担重
5. **`System.IO.Compression` 之外不用其他库**：被否决，tar/7z 无法覆盖

## Consequences

### 优势
- zip/tar/gz 统一浏览体验
- 流式访问，大 archive 内存可控
- 多格式通过 Factory 扩展
- 与 FS 一致的命令接口
- 跨 Provider 复制（zip → FS）开箱可用

### 代价
- `SharpZipLib` 依赖（约 200KB）
- 多格式适配器维护成本
- 嵌套压缩有限支持
- 写入需要重打包（M5+）

### 约束
- Archive Provider 必须 `IDisposable`，unload 时关闭所有打开的 adapter
- `IArchiveAdapter` 实现必须线程安全（多命令并发读）
- entry stream 必须 dispose 后才能再打开同一 entry（避免 ZIP 限制）
- LRU 驱逐必须检查无 in-flight 操作
- `archivePath` 必须用 `IItemProvider.GetItemAsync` 校验存在性（通过 FS Provider）
- 嵌套压缩（zip in zip）M4 明确不支持，路径解析时检测并报错
- `CompressionMethod` 字段必须暴露为 Property，便于用户过滤（如只要 deflated）
- `OpenEntryStream` 返回的 Stream 必须 `CanSeek = false`（除非底层支持），调用方不能假设可定位
