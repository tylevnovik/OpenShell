# ADR-0022: 配置中心与持久化

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M5
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0008 (CLI 历史), ADR-0011 (视图文件), ADR-0019 (远程配置), ADR-0020 (Journal)

## Context

OpenShell 需要持久化多种用户数据：

| 数据类型 | 举例 | 大小 | 频率 | 安全要求 |
|---|---|---|---|---|
| 主配置 | 主题、默认 Provider、性能参数 | < 10KB | 启动读 + 偶尔写 | 一般 |
| 命令历史 | 最近 10000 条命令 | < 1MB | 每条命令追加 | 中（含路径名） |
| 操作日志 | Undo/Redo 历史 | < 10MB | 每操作追加 | 中（含路径名） |
| Trash | 临时备份目录 | 可达 1GB | 删除时写、purge 删 | 中 |
| 凭据 | access key / SSH key | < 1KB | 远程操作读 | 高 |
| 视图文件 | 自定义 Format-Table 列定义 | < 100KB | 启动读 | 低 |
| 远程配置 | S3 / WebDAV 账户列表 | < 10KB | 启动读 + 偶尔写 | 中 |
| 缓存 | 补全缓存、缩略图 | < 100MB | 频繁读写 | 低 |

需求：

1. **统一目录**：所有持久化数据在 `~/.openshell/` 下
2. **跨平台**：Windows `%USERPROFILE%`、Linux/Mac `$HOME`
3. **热重载**：用户编辑 `config.toml` 后生效，无需重启
4. **类型安全**：强类型配置对象，避免 typo
5. **凭据特殊处理**：必须 OS 加密，不混在普通配置里
6. **缓存可清理**：用户可一键清理缓存，不影响配置
7. **迁移**：版本升级时配置 schema 变更需迁移
8. **导入导出**：用户可导出 / 导入配置（不含凭据）

参考：
- PowerShell `$PROFILE` / `$PROFILE.CurrentUserCurrentHost`
- VS Code `settings.json`
- Git `~/.gitconfig`

## Decision

### 1. 目录结构

```
~/.openshell/
├── config.toml                    # 主配置
├── remotes.toml                   # 远程账户配置（不含凭据）
├── history.jsonl                   # 命令历史
├── journal.jsonl                   # 操作日志（Undo/Redo）
├── views/                          # 自定义视图
│   ├── default.toml
│   ├── fs-file.toml
│   └── reg-key.toml
├── trash/                          # Trash 临时备份
│   └── 2026-07-07T15-30-00/
├── cache/                          # 缓存（可清理）
│   ├── completion/
│   ├── thumbnails/
│   └── archive-index/
├── uploads/                        # 未完成的 multipart uploads
└── credentials.enc                 # 加密的凭据存储
```

跨平台路径：

```csharp
public static class OpenShellPaths
{
    public static string Root { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        + Path.DirectorySeparatorChar + ".openshell";

    public static string Config => Path.Combine(Root, "config.toml");
    public static string History => Path.Combine(Root, "history.jsonl");
    public static string Journal => Path.Combine(Root, "journal.jsonl");
    public static string Views => Path.Combine(Root, "views");
    public static string Trash => Path.Combine(Root, "trash");
    public static string Cache => Path.Combine(Root, "cache");
    public static string Credentials => Path.Combine(Root, "credentials.enc");
    public static string Remotes => Path.Combine(Root, "remotes.toml");
    public static string Uploads => Path.Combine(Root, "uploads");
}
```

### 2. 主配置 config.toml

```toml
[shell]
defaultProvider = "fs"
promptStyle = "full"               # full / minimal / custom
historySize = 10000

[theme]
mode = "system"                    # light / dark / system
fontName = "Inter"
fontSize = 14

[performance]
completionCacheTtl = 5             # seconds
thumbnailCacheSize = 1000
thumbnailSize = 32
listPageSize = 200

[operations]
useTrashByDefault = true
trashRetentionDays = 7
trashMaxSizeMb = 1024
journalMaxEntries = 10000

[undo]
enabled = true
maxSteps = 100

[ipc]
enabled = true
syncLocation = true                # GUI ↔ CLI 位置同步
syncSelection = false               # 单向（避免循环）

[plugins]
watch = false                      # 开发模式热重载
directory = "~/.openshell/providers"

[remote]
defaultTimeoutSec = 30
retryCount = 5
circuitBreakerThreshold = 10
```

### 3. 强类型配置对象

```csharp
public sealed class OpenShellSettings
{
    public ShellSettings Shell { get; set; } = new();
    public ThemeSettings Theme { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public OperationsSettings Operations { get; set; } = new();
    public UndoSettings Undo { get; set; } = new();
    public IpcSettings Ipc { get; set; } = new();
    public PluginsSettings Plugins { get; set; } = new();
    public RemoteSettings Remote { get; set; } = new();
}
```

通过 `IOptionsMonitor<OpenShellSettings>` 注入，支持热重载。

### 4. 配置加载与热重载

```csharp
public sealed class TomlConfigurationSource : IFileConfigurationSource
{
    public string Path { get; init; }
}

public sealed class TomlConfigurationProvider : ConfigurationProvider
{
    public override void Load()
    {
        if (!File.Exists(Source.Path)) { Data = new(); return; }
        var toml = File.ReadAllText(Source.Path);
        Data = TomletParser.Parse(toml).ToDictionary();
    }
}
```

`IFileWatcher` 监视 `config.toml` 变化 → 触发 `IOptionsMonitor<T>.OnReload` → 各模块订阅 `OnChange`：

```csharp
_settingsMonitor.OnChange(settings => {
    if (settings.Theme.Mode != _currentTheme)
        ApplyTheme(settings.Theme.Mode);
});
```

### 5. 凭据独立存储

凭据不写入 `config.toml` / `remotes.toml`，统一存 `credentials.enc`：

- **Windows**：DPAPI `ProtectedData.Protect` 加密整个文件
- **Linux**：`SecretStorage`（D-Bus）或文件 600 + AES-256（用户密码派生密钥）
- **macOS**：`Security.framework` Keychain

`remotes.toml` 中只存 `credentialKey = "s3::my-aws"`，凭据查找走 `ICredentialProvider`（见 ADR-0019）。

### 6. 历史与日志的追加格式

`history.jsonl` 与 `journal.jsonl` 是 JSON Lines 格式：

- 每行一条 JSON 记录，append-only
- 写入用 `File.AppendAllText` + 文件锁
- 读取用 `File.ReadLines` 逐行解析
- 损坏的行跳过，不阻断整体加载

### 7. 视图文件加载

启动时扫描 `~/.openshell/views/*.toml`，按文件名前缀匹配 Item 类型：

- `default.toml` → 默认视图
- `fs-file.toml` → FS Provider 的 File 项
- `reg-key.toml` → Registry Provider 的 Key 项
- `*` 通配符 → 兜底视图

文件解析失败时降级到内置默认，记录 warning。

### 8. 缓存目录

`~/.openshell/cache/` 子目录：

| 子目录 | 内容 | 清理策略 |
|---|---|---|
| `completion/` | 路径补全缓存 | TTL（5s 默认） |
| `thumbnails/` | 图片缩略图 | LRU（1000 张） |
| `archive-index/` | Archive entry 索引 | 进程生命周期 |

用户可通过 `clear-cache` 命令一键清理整个 `cache/`。

### 9. 迁移机制

每次启动时检查 `config.toml` 中的 `schemaVersion`：

```toml
[schema]
version = "0.1.0"
```

当前版本与代码版本不匹配时执行迁移：

```csharp
public interface IConfigurationMigration
{
    Version From { get; }
    Version To { get; }
    void Migrate(TomlDocument config);
}
```

迁移脚本按版本顺序执行，备份原文件到 `config.toml.bak.{timestamp}`。

### 10. 导入导出

`export-config` 命令：

- 输出 `config.toml` / `remotes.toml` / `views/` 到指定目录
- 不导出 `credentials.enc` / `journal.jsonl` / `cache/` / `trash/`
- 导出的 `remotes.toml` 中 `credentialKey` 保留但凭据缺失

`import-config` 命令：

- 从指定目录读取并覆盖（备份原文件）
- 凭据需用户重新配置

### 11. 配置项校验

启动时校验配置：

- 类型检查（int 不能是字符串）
- 范围检查（`historySize` 必须 > 0）
- 未知字段警告（可能拼写错误）
- 校验失败时降级到默认值，记录 warning

## Alternatives Considered

1. **JSON 配置**：被否决，TOML 注释友好、类型清晰
2. **Windows Registry 存配置**：被否决，跨平台难
3. **环境变量**：被否决，结构化配置难表达
4. **SQLite 数据库存所有数据**：被否决，单文件易于备份/迁移
5. **每配置一个文件**：被否决，文件爆炸
6. **YAML**：被否决，缩进敏感易错
7. **.ini**：被否决，无嵌套结构

## Consequences

### 优势
- 统一目录易管理
- TOML 可读性好
- 热重载即时生效
- 凭据独立加密
- 导入导出便于迁移
- 缓存可清理

### 代价
- 多文件管理（config.toml / remotes.toml / views/）
- 热重载需 FileSystemWatcher（跨平台行为差异）
- 迁移脚本维护

### 约束
- `~/.openshell/` 目录权限必须 0700（Unix）
- `credentials.enc` 权限 0600
- `history.jsonl` / `journal.jsonl` 权限 0600
- 配置文件解析失败必须降级到默认，不阻断启动
- 配置迁移前必须备份原文件
- `IOptionsMonitor.OnChange` 订阅必须在 UI 线程处理（涉及主题切换等）
- 凭据禁止以明文形式写入任何日志（包括异常堆栈）
- 缓存目录可清理，但清理时必须确保无 in-flight 操作
- TOML 解析器必须支持注释、多行字符串、数组等标准特性
- 配置项校验失败时降级而非崩溃
- 视图文件解析失败时降级到内置默认，记录 warning 而非 error
- 导出配置时必须明确告知用户凭据不包含，需在新机器重新配置
