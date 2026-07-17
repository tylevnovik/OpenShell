# ADR-0039: Provider 包生态与发现

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: Cross-cutting
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0005 (ALC 加载), ADR-0016 (ProviderLoadContext 热卸载), ADR-0019 (远程 Provider), ADR-0032 (打包分发), ADR-0036 (安全沙箱), ADR-0037 (自动更新), ADR-0038 (API 版本)

## Context

ADR-0016 解决了本地 DLL 通过 ALC 加载与热卸载，ADR-0038 定义了版本兼容性矩阵，但缺：

- Provider 包从哪里来（用户怎么发现、安装第三方 Provider）
- Provider 包的清单格式（依赖、签名、版本范围）
- 在线仓库（registry）协议与索引格式
- 安装/更新/卸载/校验的生命周期
- 与 OpenShell 主程序自动更新（ADR-0037）的关系
- Provider 之间的依赖解析（如 `OpenShell.Providers.S3` 依赖 `OpenShell.Providers.Remote`）
- 镜像源（自建镜像、私有源、官方源）

无规范会导致：用户只能从 GitHub Release 手动下载 DLL、版本碎片化、安全风险（任意未签名 DLL 被加载）、依赖冲突、Provider 生态无法形成。

## Decision

### 1. 包格式（OpenShell Provider Package, `.osp`）

一个 `.osp` 包实质是 ZIP 压缩文件，扩展名为 `.osp`，结构：

```
my-provider-1.2.0.osp
├── openshell.provider.json   # 清单（ADR-0038）
├── signature.sig             # 签名（detached，对清单 + DLL 摘要签名）
├── signature.pub             # 签名公钥
├── MyProvider.dll            # 实现
├── MyProvider.deps.json      # .NET 依赖描述
├── MyProvider.pdb            # 调试符号（可选）
├── assets/                   # 图标、本地化资源
└── README.md
```

### 2. 清单 `openshell.provider.json` 完整规范

```json
{
  "$schema": "https://openshell.dev/schemas/provider.json",
  "name": "OpenShell.Providers.S3",
  "displayName": "AWS S3 Provider",
  "version": "1.2.0",
  "requiredApiVersion": "1.0.0",
  "apiStability": "Stable",
  "authors": ["jane@example.com"],
  "owners": ["jane@example.com"],
  "repository": "https://github.com/jane/openshell-s3",
  "license": "MIT",
  "licenseUrl": "https://opensource.org/licenses/MIT",
  "description": "S3 remote provider with multipart upload",
  "icon": "assets/s3.png",
  "tags": ["remote", "cloud", "aws"],
  "capabilities": ["Item", "Container", "Navigation", "Content", "Property", "Drive"],
  "dependencies": [
    {
      "name": "OpenShell.Providers.Remote",
      "version": ">= 1.0.0 < 2.0.0"
    },
    {
      "name": "AWSSDK.S3",
      "version": ">= 3.7.0",
      "kind": "external"
    }
  ],
  "minimumHostVersion": "1.0.0",
  "supportedPlatforms": ["win-x64", "linux-x64", "osx-arm64"],
  "releaseNotes": "https://github.com/jane/openshell-s3/releases/v1.2.0",
  "configSchema": {
    "type": "object",
    "properties": {
      "Region": { "type": "string" },
      "Profile": { "type": "string" }
    }
  }
}
```

字段说明：
- `dependencies[].kind`：`provider`（OpenShell Provider 包）/ `external`（NuGet 库）
- `version`：遵循 NuGet 版本范围语法（`[1.0,2.0)` / `>= 1.2.0`）
- `configSchema`：JSON Schema，用于 GUI 配置面板自动生成

### 3. 注册源（Registry Sources）

支持多注册源，配置在 `~/.openshell/registries.toml`：

```toml
[[source]]
name = "official"
url = "https://registry.openshell.dev/v1/"
priority = 1
trusted = true   # 官方源，签名校验放宽

[[source]]
name = "private-company"
url = "https://npm.corp.example.com/openshell/"
priority = 2
trusted = false   # 私有源，强制签名校验
auth = "env:CORP_REGISTRY_TOKEN"

[[source]]
name = "local-dev"
url = "file:///C:/dev/my-providers/"
priority = 3
trusted = true   # 本地开发，放宽
```

### 4. 注册源 HTTP API（v1）

所有注册源遵循统一 REST API：

| 端点 | 方法 | 用途 |
|---|---|---|
| `/v1/packages` | GET | 列出所有 Provider 包（分页） |
| `/v1/packages/{name}` | GET | 包元信息 + 所有版本 |
| `/v1/packages/{name}/{version}` | GET | 指定版本元信息 |
| `/v1/packages/{name}/{version}.osp` | GET | 下载 .osp 包 |
| `/v1/packages/{name}/latest` | GET | 最新稳定版 |
| `/v1/search?q=aws` | GET | 关键词搜索 |
| `/v1/indices/index.json` | GET | 全量索引（离线缓存用） |

响应示例：

```json
{
  "name": "OpenShell.Providers.S3",
  "versions": [
    { "version": "1.2.0", "apiVersion": "1.0.0", "stability": "Stable", "publishedAt": "2026-06-01T10:00:00Z" },
    { "version": "1.1.0", "apiVersion": "1.0.0", "stability": "Stable", "publishedAt": "2026-04-15T10:00:00Z", "deprecated": true }
  ]
}
```

### 5. 命令清单

新增 CLI 命令：

| 命令 | 别名 | 说明 |
|---|---|---|
| `Get-Provider` | gpr | 列出已安装 Provider |
| `Find-Provider` | fp | 在线搜索 Provider 包 |
| `Install-Provider` | ipr | 安装 Provider |
| `Update-Provider` | upr | 升级 Provider |
| `Uninstall-Provider` | rmpr | 卸载 Provider |
| `Publish-Provider` | pbpr | 发布 Provider 包到注册源 |
| `Get-ProviderSource` | gpsrc | 列出注册源 |
| `Register-ProviderSource` | npsrc | 添加注册源 |
| `Unregister-ProviderSource` | rmpsrc | 移除注册源 |

示例：

```
> Find-Provider aws
Name                            Version  Stability  Downloads
OpenShell.Providers.S3          1.2.0    Stable     1234
OpenShell.Providers.DynamoDB    0.5.0    Preview    89

> Install-Provider OpenShell.Providers.S3
Resolving dependencies...
  OpenShell.Providers.Remote >= 1.0.0 (already installed)
  AWSSDK.S3 >= 3.7.0 (external)
Verifying signature... OK
Downloading 1.2.0... 240KB
Installing to ~/.openshell/providers/s3/1.2.0/...
Registering with ProviderLoadContext...
Loaded. Provider 's3' ready.
```

### 6. 安装目录结构

```
~/.openshell/
├── providers/
│   ├── s3/
│   │   ├── 1.2.0/
│   │   │   ├── openshell.provider.json
│   │   │   ├── OpenShell.Providers.S3.dll
│   │   │   └── ...
│   │   └── current -> 1.2.0/   # 符号链接，便于版本切换
│   └── remote/
│       └── 1.0.0/
├── cache/
│   ├── downloads/        # 下载缓存（按哈希）
│   └── indices/          # 注册源索引快照
├── plugins.config.toml   # 启用/禁用、加载顺序
└── trash/                # 卸载前的备份（保留 N 天）
```

`plugins.config.toml`：

```toml
[[provider]]
name = "s3"
enabled = true
loadOrder = 10
autoUpdate = true
config = { Region = "us-east-1", Profile = "default" }

[[provider]]
name = "reg"
enabled = true
loadOrder = 5
autoUpdate = false
```

### 7. 依赖解析

- 用 NuGet 的 `NuGet.Versioning` 库做版本范围解析
- 拓扑排序确定加载顺序（`loadOrder` 字段优先，其次拓扑）
- 冲突策略：
  - Provider 间版本冲突 → 取最低公共版本，否则提示用户
  - 外部 NuGet 依赖 → 解析到 `~/.opensshell/providers/<pkg>/lib/`，独立于 host 的 NuGet 缓存
- 外部依赖通过 `RuntimeIdentifier` 选择 native 库

### 8. 签名校验

- `.osp` 包必须签名（除非来自 `trusted` 源且 `trusted` 标记允许跳过）
- 签名算法：Ed25519 detached signature，对 `openshell.provider.json` + 所有 DLL 的 SHA256 摘要
- 公钥通过以下方式信任：
  - 官方源预置根证书
  - 私有源：首次安装时 `Register-ProviderSource --trust-key <pubkey>`
  - 用户源：`Install-Provider --trust-key <file>`
- 校验失败：拒绝加载并提示用户

### 9. 与 OpenShell 主程序自动更新协同

- 主程序更新（ADR-0037）触发 Provider 兼容性重新检查
- 主版本提升时：
  - 调用 `Get-Provider --check-compatibility` 列出所有不兼容 Provider
  - 自动尝试升级（若新版本可用）
  - 不可用的 Provider 进入 Disabled 状态，提示用户手动处理

### 10. 发布工作流

```
[开发者]
  dotnet openshell pack -c Release
  -> 生成 my-provider-1.2.0.osp
  dotnet openshell sign --key private.pem
  -> 生成 signature.sig
  dotnet openshell push --source official --api-key $TOKEN
  -> 上传到注册源
```

`dotnet openshell` 全局工具提供 `pack`/`sign`/`push`/`install`/`restore` 子命令。

### 11. 离线场景

- `dotnet openshell restore` 在 CI 中预先下载所有 Provider 到 `~/.openshell/providers/`
- `--source ./local-cache/` 优先本地缓存
- 注册源索引快照可定时拉取（`Get-ProviderSource --refresh-indices`）

## Alternatives Considered

1. **复用 NuGet 包格式（.nupkg）**：被否决，NuGet 不支持签名、清单语义弱、运行时加载不便
2. **复用 PowerShell Gallery**：被否决，PS Gallery 面向 PowerShell 模块，与 OpenShell 接口无关
3. **单源强管制（类似 iOS App Store）**：被否决，违背开源生态
4. **不提供包管理，全靠用户拷贝 DLL**：被否决，无法形成生态
5. **每个 Provider 独立 native binary，无统一 manifest**：被否决，无法依赖解析
6. **不允许外部 NuGet 依赖**：被否决，第三方生态受限

## Consequences

### 优势
- 用户一条命令安装第三方 Provider
- 依赖与版本管理自动化
- 签名机制保证供应链安全
- 多源支持企业内部私有 Provider 分发
- 主程序升级时自动重检兼容性
- `dotnet openshell` 工具链对开发者友好

### 代价
- 注册源需要独立部署与维护（官方 registry.openshell.dev）
- Provider 作者需学习清单与签名流程
- 依赖解析存在边界 case 失败可能
- 多源场景下信任链管理复杂

### 约束
- `.osp` 包内文件禁止绝对路径
- 包大小上限 50MB（超出需拆分或单独协商）
- Provider 包禁止以管理员权限执行（与 ADR-0036 沙箱协同）
- 卸载前的 Trash 备份保留 7 天
- 注册源 HTTP API 必须支持 ETag/Last-Modified 以减少带宽
- `Install-Provider` 必须支持 `--dry-run` 预览依赖变更
- 全局工具 `dotnet openshell` 版本必须与 OpenShell 主程序同步发布
- Provider 包禁止依赖 host 进程内的全局状态（必须通过 DI 注入）
