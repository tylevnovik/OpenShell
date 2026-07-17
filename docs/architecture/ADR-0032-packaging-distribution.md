# ADR-0032: 打包与分发

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: 跨阶段（M2+ 起步）
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0016 (ALC), ADR-0022 (配置), ADR-0031 (日志)

## Context

OpenShell 需要分发给最终用户：

1. **多个发行物**：CLI / GUI / Core 库 / Provider 包
2. **跨平台**：Windows / Linux / macOS
3. **架构差异**：x64 / arm64
4. **依赖**：自包含 vs 框架依赖
5. **AOT 评估**：Avalonia NativeAOT 部分支持，但 ALC（ADR-0016）不支持
6. **安装器**：MSI / DMG / deb / rpm / tarball
7. **更新**：自动检查 + 手动下载
8. **包管理集成**：winget / Homebrew / Scoop / AUR
9. **签名**：代码签名（macOS 必须，Windows 推荐）
10. **版本号**：SemVer 规范

PowerShell 的分发：

- Windows 内置（旧版）
- MSI / tarball / Homebrew / Snap
- `dotnet tool install`（PowerShell Global Tool 模式）

参考但简化。

## Decision

### 1. 发行物清单

| 名称 | 类型 | 内容 |
|---|---|---|
| `openshell-cli` | 可执行 | CLI host，单文件 |
| `openshell-gui` | 可执行 | Avalonia GUI host |
| `OpenShell.Core` | NuGet | 核心库，第三方 Provider 引用 |
| `OpenShell.Providers.FileSystem` | NuGet | 内置 Provider（独立包便于参考） |
| `OpenShell.Providers.Archive` | NuGet | |
| `OpenShell.Providers.Registry` | NuGet（Windows only） | |
| `OpenShell.Providers.Remote` | NuGet | |
| `OpenShell.Extensions.*` | NuGet | 模板 / 工具 |

### 2. 单文件可执行

`openshell-cli` 与 `openshell-gui` 发布为单文件：

```bash
dotnet publish -c Release -r win-x64 \
    -p:PublishSingleFile=true \
    -p:SelfContained=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true
```

参数：

- `PublishSingleFile=true` — 单文件
- `SelfContained=true` — 包含 .NET 运行时（无需用户安装 .NET）
- `IncludeNativeLibrariesForSelfExtract=true` — 原生依赖打包进单文件
- `EnableCompressionInSingleFile=true` — 压缩减小体积

输出大小估计：~80MB（含 .NET + Avalonia + 依赖）。

可选 `SelfContained=false`（框架依赖）减到 ~10MB，但需用户预装 .NET 8。

### 3. 平台 RID

| RID | 平台 |
|---|---|
| `win-x64` | Windows x64 |
| `win-arm64` | Windows ARM64 |
| `linux-x64` | Linux x64 |
| `linux-arm64` | Linux ARM64 |
| `osx-x64` | macOS Intel |
| `osx-arm64` | macOS Apple Silicon |

每平台独立构建。

### 4. NativeAOT 限制

Avalonia 11+ 部分支持 NativeAOT，但：

- ❌ ALC（`AssemblyLoadContext`）不支持 — 我们的插件加载必须用运行时反射
- ❌ 反射动态生成代码受限 — `[Verb]` 特性反射读取 OK，但动态 emit 不行
- ⚠️ `System.Text.Json` 源生成模式需配置

策略：

- **不使用 NativeAOT**，保持单文件 + Self-contained
- 启动速度 < 200ms（单文件解压 + .NET 启动）
- 体积可通过 `PublishTrimmed=true` 优化，但反射可能被裁剪，需小心

### 5. NuGet 包结构

`OpenShell.Core` 包：

```
OpenShell.Core.0.1.0.nupkg
├── lib/net8.0/
│   ├── OpenShell.Core.dll
│   └── OpenShell.Core.xml
├── ref/net8.0/              ← 编译时引用（仅接口）
│   └── OpenShell.Core.dll
├── readme.md
└── icon.png
```

第三方 Provider 项目 `csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="OpenShell.Core" Version="0.1.0" />
  </ItemGroup>
</Project>
```

### 6. Provider NuGet 包

每个 Provider 独立 NuGet 包：

- 第三方可只引用需要的 Provider
- 主程序 `OpenShell.Cli.Host` / `OpenShell.Gui.Host` 引用所有内置 Provider 作为依赖
- 第三方 Provider 包按 ADR-0016 加载

### 7. 安装器

#### Windows MSI

- WiX Toolset v4 生成 MSI
- 安装到 `%ProgramFiles%\OpenShell\`
- 添加 PATH 环境变量
- 关联文件类型（可选）
- 代码签名（Authenticode）

#### macOS DMG

- `create-dmg` 工具生成
- 安装到 `/Applications/OpenShell/`
- 签名 + 公证（Apple Developer ID）

#### Linux tarball

- `tar.gz` 包含 `openshell-cli` / `openshell-gui` / `lib/`
- 安装到 `/opt/openshell/` 或 `~/.local/share/openshell/`
- 符号链接到 `/usr/local/bin/openshell-cli`

#### Linux 包管理

- **deb**（Ubuntu / Debian）：`dpkg-deb` 生成
- **rpm**（Fedora / RHEL）：`rpmbuild` 生成
- **AUR**（Arch）：社区维护 PKGBUILD
- **Homebrew**（macOS）：tap 仓库

### 8. 包管理集成

- **winget**（Windows）：提交 manifest 到 `microsoft/winget-pkgs`
- **Scoop**（Windows）：scoop bucket
- **Homebrew**（macOS）：`brew install openshell`
- **snap**（Linux）：可选

每发布新版本更新对应仓库。

### 9. 版本号策略

SemVer 2.0：

```
MAJOR.MINOR.PATCH[-prerelease][+build]
0.1.0-alpha
0.1.0-alpha.1
0.1.0
0.2.0
1.0.0
```

- MAJOR：不兼容 API 变更
- MINOR：向后兼容新功能
- PATCH：bug 修复
- prerelease：`alpha` / `beta` / `rc`

版本号在 `Directory.Build.props`：

```xml
<Version>0.1.0-alpha</Version>
<FileVersion>0.1.0.0</FileVersion>
<AssemblyVersion>0.1.0.0</AssemblyVersion>
```

### 10. 自动更新

`check-update` 命令：

- 访问 GitHub Releases API（或自建更新服务）
- 比较版本号
- 提示用户下载
- 不自动安装（需用户确认）

未来可考虑自动下载 + 重启更新。

### 11. CI/CD

GitHub Actions：

- `build` workflow：每 PR 构建 + 测试
- `release` workflow：tag 触发，构建多平台，上传 GitHub Release
- 包发布到 nuget.org

矩阵：

```yaml
strategy:
  matrix:
    os: [windows-latest, ubuntu-latest, macos-latest]
    arch: [x64, arm64]
```

### 12. 签名

- Windows：Authenticode 证书，`signtool sign`
- macOS：Developer ID Application 证书，`codesign --deep --sign`
- Linux：GPG 签名 deb / rpm

无证书时构建未签名版本，文档说明用户验证 checksum。

### 13. 校验和

每发布附带 `SHA256SUMS`：

```
openshell-cli-0.1.0-win-x64.exe   <sha256>
openshell-gui-0.1.0-win-x64.exe   <sha256>
...
```

用户 `Get-FileHash` / `shasum` 校验。

### 14. Desktop Entry（Linux）

`openshell-gui.desktop`：

```ini
[Desktop Entry]
Version=1.0
Type=Application
Name=OpenShell
Comment=Cross-platform shell and file manager
Exec=/opt/openshell/openshell-gui %F
Icon=openshell
Categories=System;FileManager;
Terminal=false
MimeType=inode/directory;
```

文件管理器集成（"Open With OpenShell"）。

## Alternatives Considered

1. **`dotnet tool install`**：被否决，仅 CLI 不便 GUI，且需用户预装 .NET SDK
2. **Docker 容器**：被否决，桌面应用不适合
3. **PortableApp（Windows）**：被否决，平台局限
4. **AppImage（Linux）**：可选，但需评估 Avalonia 兼容性
5. **完整 NativeAOT 单文件**：被否决，ALC 不支持
6. **MSIX（Windows）**：可选，但 sandbox 限制文件访问
7. **不分平台包，仅 tarball**：被否决，集成体验差

## Consequences

### 优势
- 多种安装方式覆盖各平台
- 单文件自包含易分发
- NuGet 包便于第三方开发
- CI/CD 自动化
- 签名验证可信

### 代价
- 单文件体积较大（~80MB）
- NativeAOT 不支持，启动速度有限
- 多平台构建矩阵维护
- 包管理仓库同步成本

### 约束
- 版本号必须遵循 SemVer 2.0
- 单文件必须 `Self-contained=true`
- 第三方 NuGet 包必须只引用 `OpenShell.Core`，不引用主程序
- Windows MSI 必须添加到 PATH
- macOS DMG 必须签名 + 公证
- Linux deb / rpm 必须包含 desktop entry
- GitHub Release 必须附带 SHA256SUMS
- 签名证书过期前必须更新
- CI/CD 构建产物必须可重现（相同 commit 生成相同二进制）
- 包发布到 nuget.org 前必须本地测试加载
- `check-update` 不得自动下载安装，必须用户确认
