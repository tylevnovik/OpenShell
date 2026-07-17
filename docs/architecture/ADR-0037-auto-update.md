# ADR-0037: 自动更新机制

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: 长期可选
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0032 (打包), ADR-0022 (配置), ADR-0031 (日志)

## Context

OpenShell 发布新版本后，用户需要更新：

1. **检查更新**：启动时后台检查，或用户主动 `check-update`
2. **下载**：增量 vs 全量
3. **签名校验**：防中间人
4. **安装**：替换二进制，需重启
5. **回滚**：更新失败可恢复
6. **跨平台**：Win / Linux / macOS 各自机制
7. **后台下载**：不阻塞使用
8. **企业策略**：禁用自动更新（统一部署）

需求约束：

- 不强制更新
- 默认每日检查一次（可配置）
- 下载不显著影响网络
- 安装需用户确认

参考：

- VS Code 自动更新
- Sparkle (macOS)
- Squirrel (Windows)
- PowerShell `Update-Help` 风格（仅检查提示）

## Decision

### 1. 更新检查

```csharp
public interface IUpdateService
{
    ValueTask<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct);
    ValueTask DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct);
    ValueTask InstallAsync(UpdateInfo info, CancellationToken ct);
    IObservable<UpdateStatus> StatusChanged { get; }
}

public sealed record UpdateInfo(
    Version Version,
    string ReleaseNotes,
    Uri DownloadUrl,
    string Sha256,
    long SizeBytes,
    DateTimeOffset PublishedAt,
    bool IsPrerelease);
```

### 2. 检查频率

```toml
[updates]
checkFrequency = "daily"    # never / startup / daily / weekly
channel = "stable"          # stable / beta / dev
includePrerelease = false
```

启动时如距上次检查 > 频率阈值，后台异步检查。

### 3. 更新源

- **GitHub Releases**（默认）：API `https://api.github.com/repos/<owner>/openshell/releases`
- **自建更新服务**（可选）：`https://updates.openshell.dev/api/manifest`

Manifest JSON：

```json
{
  "version": "0.2.0",
  "releaseNotes": "...",
  "assets": {
    "win-x64": {"url":"https://...", "sha256":"...", "size": 82000000},
    "linux-x64": {...},
    "osx-arm64": {...}
  }
}
```

按当前平台 RID 选择对应 asset。

### 4. 下载

- 后台 `HttpClient` 流式下载
- 进度通过 `IProgress<double>`
- 默认下载到 `~/.opensshell/updates/<version>/`
- 临时文件名 `.partial`，完成后改名
- 支持断点续传（HTTP Range header）

### 5. 签名校验

下载完成后：

1. 计算 SHA256
2. 与 manifest 中的 `sha256` 对比
3. 不匹配删除文件 + 警告
4. Windows / macOS 额外验证代码签名

### 6. 安装流程

#### Windows

- 当前 exe 路径：`C:\Program Files\OpenShell\openshell-cli.exe`
- 下载的新版本到 `~/.opensshell/updates/0.2.0/openshell-cli.exe`
- 启动一个独立的更新器进程 `openshell-updater.exe`
- 更新器：
  1. 等待主进程退出（用户确认）
  2. 重命名当前 exe 为 `openshell-cli.exe.old`
  3. 移动新 exe 到目标位置
  4. 启动新版本
  5. 删除 `.old` 文件

#### Linux / macOS

类似，但用 `mv` + atomic rename：

```bash
mv openshell-cli openshell-cli.old
mv openshell-cli.new openshell-cli
chmod +x openshell-cli
```

### 7. 回滚

- `.old` 文件保留 7 天
- `rollback-update` 命令恢复上一版本
- 配置文件与用户数据不动（仅替换二进制）

### 8. 增量更新（可选）

- 基于 bsdiff / xdelta 生成 patch
- manifest 含 `patchUrl`（从 0.1.0 到 0.2.0 的 patch）
- 检测当前版本，下载对应 patch
- 应用 patch 生成新二进制
- 失败回退到全量下载

M5+ 评估，首版仅全量更新。

### 9. GUI 更新 UI

- 状态栏显示"有新版本 0.2.0"
- 点击弹出对话框：版本说明 + 下载按钮
- 下载进度条
- 下载完成"重启更新"按钮

### 10. CLI 更新提示

```
OpenShell CLI 0.1.0
A new version 0.2.0 is available.
Run 'update-openshell' to download and install.
```

启动时显示一次，24 小时内不重复。

### 11. 命令

- `check-update` — 主动检查
- `update-openshell` — 下载 + 安装
- `rollback-update` — 回滚上一版本
- `set-update-channel <channel>` — 配置通道

### 12. 企业策略

`/etc/openshell/policy.toml`（Linux）/ `%ProgramData%/OpenShell/policy.toml`（Windows）：

```toml
[updates]
enabled = false        # 企业禁用自动更新
targetVersion = "0.1.0" # 强制版本
```

策略文件优先级高于用户配置。

### 13. 离线更新

`update-openshell --offline <path>` 从本地路径安装：

- 用于无网络环境
- 用于企业统一分发

### 14. 安全

- HTTPS 强制（拒绝 HTTP）
- 签名校验失败必须拒绝安装
- 更新源 URL 可配置（自建镜像）
- 下载临时文件权限 0600

### 15. 失败处理

- 下载失败：保留 `.partial`，下次续传
- 校验失败：删除 + 重新检查
- 安装失败：恢复 `.old`，记录 error
- 主进程崩溃后 updater 自动检测并恢复

## Alternatives Considered

1. **仅提示有新版本，不自动下载**：被否决，用户体验差
2. **包管理器更新（winget / brew）**：保留，作为另一更新渠道
3. **ClickOnce**：被否决，仅 Windows
4. **Squirrel**：被否决，仅 Windows
5. **强制自动更新**：被否决，用户控制
6. **容器化部署**：被否决，桌面应用不适合
7. **完整增量更新**：首期不做，复杂度高

## Consequences

### 优势
- 自动检查 + 手动确认
- 后台下载不影响使用
- 签名校验安全
- 回滚机制
- 跨平台一致流程

### 代价
- 更新器独立进程维护
- 跨平台文件操作差异
- 增量更新（首期不做）下载量大
- 企业策略文件管理

### 约束
- 更新源必须 HTTPS
- 下载文件必须校验 SHA256
- 安装前必须等待主进程退出
- `.old` 文件必须保留至少 7 天
- 企业策略文件优先级高于用户配置
- 下载失败必须支持续传
- 安装失败必须自动回滚
- 更新器进程必须独立于主程序（避免自己更新自己冲突）
- 预发布版本默认不检查，必须显式 `includePrerelease = true`
- 强制版本（`targetVersion`）必须严格匹配，禁止模糊
- 离线更新包必须可校验签名
