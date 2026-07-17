# ADR-0006: 路径模型采用 Provider 命名空间 + 内部相对路径

- **Status**: Accepted
- **Date**: 2026-07-07
- **Decider**: Architecture
- **Supersedes**: —

## Context

OpenShell 同时支持 4 类 Provider，每类有完全不同的路径语义：

- FileSystem：`C:\Users\foo`、`/home/foo`，盘符 + 反斜杠/正斜杠
- Archive：`archive.zip\subdir\file.txt`，虚拟挂载，无真实盘符
- Registry：`HKLM\Software\Microsoft`，Hive + 树路径
- Remote S3：`s3://bucket/key`，bucket + key

CLI 与 GUI 必须用同一路径语法引用这些位置，且支持：
- 在 CLI 里 `cd zip::archive.zip/subdir` 进入压缩包内
- 在 GUI 侧边栏同时显示 `C:\`、`zip::archive.zip`、`HKLM\`、`s3://bucket` 四类入口
- Pipeline 跨 Provider 引用：`get-childitem fs::C:\ | copy-item -dest zip::archive.zip/`

PowerShell 用 `PSDrive` + `Provider` 前缀（`FileSystem::C:\`、`Registry::HKEY_LOCAL_MACHINE\`）解决了这个问题，但语法不统一（FS 省略前缀，其他强制），且无 `record` 类型支撑。

## Decision

采用 **`ProviderName::ProviderInternalPath`** 双层路径模型：

```csharp
public readonly record struct ItemPath
{
    public string Provider { get; init; }      // "fs" / "zip" / "reg" / "s3"
    public string InternalPath { get; init; }  // "C:/Users/foo" 或 "subdir/file" 或 "HKLM/Software"
    public bool IsRooted { get; init; }        // 区分绝对路径与相对路径

    public string Display        // "fs::C:\Users\foo" — 用于 CLI / 调试
    public string FriendlyName  // "C:\Users\foo"     — 用于 GUI 标签（fs:: 时省略）

    public static ItemPath Parse(string input);     // 支持 fs::... 或裸路径（按当前默认 Provider）
    public ItemPath Combine(string relative);
    public ItemPath GetParent();
    public string GetName();
}
```

格式约定：

| 示例 | 含义 |
|---|---|
| `fs::C:\Users\foo` | FS Provider，绝对路径 `C:\Users\foo` |
| `fs::users/blmpt` | FS Provider，相对路径（相对当前 `cwd`） |
| `zip::archive.zip/subdir/file.txt` | Archive Provider 内路径 |
| `reg::HKLM/Software/Microsoft` | Registry Provider 内路径（统一用 `/` 分隔） |
| `s3://bucket/key` | Remote Provider S3 — 兼容标准 URL 语法 |
| `C:\Users\foo` | 裸路径，按当前默认 Provider 解析（CLI 启动默认 `fs`） |
| `.` `..` | 相对路径段，相对当前 `CurrentLocation` |

## Alternatives Considered

1. **统一 URI**：`openshell://fs/C%3A/Users/foo`。被否决，转义混乱、不直观、与现有工具链不兼容。
2. **每个 Provider 各自定义语法**：被否决，跨 Provider 管道无法统一引用。
3. **PSDrive 单层盘符**（`C:`、`HKLM:`）：被否决，盘符命名空间受限、与 Registry Hive 等"短名"冲突、GUI 侧边栏难以统一展示。
4. **直接 PowerShell 风格（`Microsoft.PowerShell.Core\FileSystem::C:\`）**：被否决，前缀过长、Provider 强类型命名空间不必要。

## Consequences

### 优势
- 跨 Provider 路径统一，Pipeline 引用一致
- GUI 侧边栏可用 `Provider` 字段分组
- 路径解析无歧义，`Parse` 单入口
- 与 `s3://` 等 URL 形式兼容，第三方 Provider 可声明自定义 scheme 别名

### 代价
- 用户在 CLI 需要学习 `provider::path` 语法
- 裸路径默认 Provider 行为需在帮助中明确
- Provider 自实现路径解析时需遵循 `INavigationProvider` 契约（NormalizePath / IsValidPath）

### 约束
- `ItemPath` 是 `readonly record struct`，零分配、值相等、可作为字典键
- `Provider` 字段必须是 `ProviderInfo.Name` 的 lower-case 形式，由 `ProviderRegistry` 校验存在性
- `InternalPath` 在 Provider 间语义不同，但分隔符统一用 `/`（FS Provider 在显示时转回 `\`，仅 Windows）
- 跨 Provider 的 `Copy-Item` 必须显式 `From` 和 `To`，不支持隐式跨 Provider 路径解析
- CLI 的 `cd` 命令默认仅切换当前 `InternalPath`，不切换 Provider；切换 Provider 用 `Set-Location zip::archive.zip` 整路径
