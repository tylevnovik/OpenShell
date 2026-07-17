# ADR-0023: 命令清单与命名规范

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M1
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0004 (Verb-Noun 系统), ADR-0024 (别名), ADR-0025 (帮助)

## Context

ADR-0004 定义了 `Verb-Noun` 命令系统和受约束动词枚举，但未给出：

- 完整内置命令清单（M1-M5 各阶段交付哪些命令）
- Noun 命名约定（单数 vs 复数、是否含 Provider 前缀）
- 命令版本演进（新增 / 弃用 / 重命名的策略）
- 命令分组（Core / Provider-specific / Pipeline-only / Host-only）
- 与 PowerShell 命令的对照（用户迁移成本）

无规范会导致：命令名混乱（`get-file` vs `get-item` vs `list-file`）、Noun 单复数不一、新增命令无评审流程、用户无法预期命令存在。

## Decision

### 1. 完整动词枚举（受约束）

```csharp
public enum Verb
{
    // Common（最常用，所有 Provider 通用）
    Get, Set, New, Remove, Move, Copy, Rename, Invoke,

    // Data（Pipeline 节点）
    Select, Where, Sort, Group, Measure, Compare,

    // Output（格式化与导出）
    Format, Out,

    // Navigation
    Push, Pop, Clear,

    // Lifecycle / Discovery
    Help, Exit, Start, Stop, Wait, Update,

    // Host / Session
    Connect, Disconnect, Mount, Unmount,
}
```

新增 Verb 必须经评审，提供至少 3 个候选 Noun 的用例。

### 2. Noun 命名规范

- **单数**：`Item` / `ChildItem` / `Location` / `Drive` / `Content` / `ItemProperty`
- **不含 Provider 前缀**：命令是 Provider 无关的，`Get-ChildItem` 适用于 fs/zip/reg/s3
- **Provider 特化命令加 Provider 前缀**：`Get-S3Object` / `Invoke-WebRequest`（少见）
- **复合 Noun 用 PascalCase**：`ChildItem` / `ItemProperty` / `PSDrive`（沿用 PS 习惯）

### 3. 完整内置命令清单

#### M1 — 核心操作与导航

| 命令 | 别名 | 说明 |
|---|---|---|
| `Get-ChildItem` | ls, dir, gci | 列出容器子项 |
| `Get-Item` | gi | 取单项 |
| `Get-Location` | pwd, gl | 当前位置 |
| `Set-Location` | cd, chdir, sl | 切换位置 |
| `Push-Location` | pushd, push | 位置压栈 |
| `Pop-Location` | popd, pop | 位置出栈 |
| `Get-PSDrive` | gdr, drives | 列出挂载的 Drive |
| `New-PSDrive` | ndr, mount | 挂载虚拟 Drive（zip/s3 bucket） |
| `Remove-PSDrive` | rdr, unmount | 卸载 Drive |
| `Copy-Item` | cp, cpi, copy | 复制 |
| `Move-Item` | mv, mi, move | 移动 |
| `Remove-Item` | rm, del, ri | 删除（默认走 Trash） |
| `Rename-Item` | rn, rni | 重命名 |
| `New-Item` | ni, mkdir, touch | 新建文件/目录 |
| `Get-Content` | cat, gc, type | 读内容流 |
| `Set-Content` | sc, write | 写内容 |
| `Clear-Host` | cls, clear | 清屏 |
| `Get-Help` | help, man, h | 帮助 |
| `Get-Command` | gcm | 列出所有命令 |
| `Get-Verb` | gv | 列出受约束动词 |
| `Exit` | quit, q | 退出 |

#### M2 — Pipeline 与格式化

| 命令 | 别名 | 说明 |
|---|---|---|
| `Where-Object` | where, ?, filter | 过滤 |
| `Select-Object` | select, projection | 投影列 |
| `Sort-Object` | sort | 排序 |
| `Group-Object` | group | 分组 |
| `Measure-Object` | measure | 统计（count/sum/avg） |
| `Compare-Object` | compare | 差异对比 |
| `Take-Object` | take, first | 取前 N |
| `Skip-Object` | skip | 跳过 N |
| `Format-Table` | ft | 表格输出 |
| `Format-List` | fl | 列表输出 |
| `Format-Json` | fj | JSON 输出 |
| `Format-Csv` | fcsv | CSV 输出 |
| `Out-Default` | od | 默认输出 |
| `Out-Host` | oh | 输出到 host |
| `Out-File` | > | 输出到文件 |
| `Out-Null` | 2>&1 >$null | 丢弃 |
| `Out-GridView` | ogv | GUI 弹窗（M5） |

#### M3 — GUI Host 命令

| 命令 | 说明 |
|---|---|
| `Show-Window` | 显示主窗口 |
| `Close-Window` | 关闭当前 tab |
| `New-Tab` | 新建 tab |
| `Next-Tab` / `Prev-Tab` | tab 切换 |
| `Set-Theme` | 切换主题 |
| `Show-CommandPalette` | Ctrl+Shift+P |
| `Show-Properties` | 属性面板 |

#### M4 — Provider 特化

| 命令 | 说明 |
|---|---|
| `Compress-Archive` | 创建压缩包 |
| `Expand-Archive` | 解压 |
| `Get-ItemProperty` | 取属性（Registry 用） |
| `Set-ItemProperty` | 改属性 |
| `Remove-ItemProperty` | 删属性 |
| `Invoke-WebRequest` | HTTP 请求（S3 Presigned URL 等） |

#### M5 — 历史 / 会话 / 配置

| 命令 | 说明 |
|---|---|
| `Get-History` | 命令历史 |
| `Invoke-History` | 重跑历史 |
| `Clear-History` | 清历史 |
| `Undo-Operation` | undo, z |
| `Redo-Operation` | redo, y |
| `Get-OperationLog` | 操作日志 |
| `Clear-Cache` | 清缓存 |
| `Export-Config` | 导出配置 |
| `Import-Config` | 导入配置 |

### 4. 命令版本演进

- **新增**：在下一 milestone 加入，文档记录
- **弃用**：用 `[Obsolete("Use X instead")]`，保留 2 个 milestone 后移除
- **重命名**：保留旧名为别名，新名为主，文档说明
- **删除**：仅在主版本提升时，且文档明确迁移路径

命令清单维护在 `docs/commands/registry.md`，每 PR 改命令必更新此文件。

### 5. 命令分组（注册时标记）

```csharp
public enum CommandGroup
{
    Core,           // Get-ChildItem 等通用
    Pipeline,       // where/select/sort，不进 GUI 菜单
    Host,           // Show-Window 等 host 特化
    Provider,       // Compress-Archive 等 Provider 特化
    Diagnostics,    // Get-Verb, Get-Command
    Lifecycle,      // Undo, History
}
```

`[Verb("Get", Noun = "ChildItem", Group = CommandGroup.Core)]`

GUI 工具栏只显示 `Core` 组，菜单按 `Provider` 组特化。

### 6. 与 PowerShell 对照

为降低迁移成本，命令名尽量对齐 PowerShell：

- 同名：`Get-ChildItem` / `Set-Location` / `Copy-Item`
- 别名兼容：`ls` / `cd` / `cp` / `mv` / `rm` / `cat` / `pwd`
- 差异点文档化：
  - `Where-Object { $_.X -gt 1 }` 脚本块与 `where x > 1` DSL 均原生支持（per ADR-0012 revised）；前者为 PowerShell 兼容主形式，后者为语法糖
  - PowerShell 的 `Format-Table -Property X,Y` → 我们的 `format-table x,y`
  - 我们新增 `out-gridview` 在 M5 才实现（PS 在 Windows 平台早就有）

## Alternatives Considered

1. **Unix 命令名（ls/cp/mv 直接作主命令）**：被否决，失去 Verb-Noun 的一致性
2. **完全自创命令名**：被否决，迁移成本高
3. **不限制 Verb**：被否决，命令爆炸、命名混乱
4. **每 Provider 一套命令（Get-FsItem / Get-ZipItem）**：被否决，违反 Provider 抽象目标

## Consequences

### 优势
- 命令清单明确，每阶段交付可预期
- 命名一致，迁移成本低
- 版本演进有规范
- GUI 菜单按组自动组织

### 代价
- Verb 受约束，新场景需评审
- 完全对齐 PS 不可能（DSL 不同），文档需说明差异
- 命令清单维护成本

### 约束
- 新增命令必须更新 `docs/commands/registry.md`
- Verb 新增需评审，至少 3 个 Noun 用例
- 弃用必须保留 2 个 milestone
- Provider 特化命令必须以 Provider 关键词开头（如 `Compress-Archive` 不以 `Zip-` 开头，因 Archive 是抽象）
- 别名禁止冲突（`ls` 只能指向一个命令）
- 命令名禁止使用下划线、连字符（除 Verb-Noun 的单个连字符）
- `CommandGroup` 必须在 `[Verb]` 特性中声明，默认 `Core`
