# Project Stability Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 修复 OpenShell 当前可复现的稳定性缺陷，将本机可处理的跳过测试清零，并恢复可运行的 CI、CLI 与 GUI 基线。

**Architecture:** 保持现有模块边界，不引入新运行时服务。Core 行为在原类内做局部修复；Provider 取消语义由共享合约测试定义，各 Provider 在公开异步边界统一执行预取消检查；CI 仅调整 SDK 版本。

**Tech Stack:** .NET 8 target framework、C#、xUnit、FluentAssertions、GitHub Actions、Avalonia。

---

### Task 1: 建立稳定性合规基线

**Files:**
- Create: `docs/project-stability-audit.md`
- Create: `docs/project-stability-tasks.md`
- Create: `tests/OpenShell.IntegrationTests/ProjectStabilityComplianceTests.cs`
- Modify: `tests/OpenShell.IntegrationTests/OpenShell.IntegrationTests.csproj`

**Steps:**
1. 为 T-500~T-505 写 `[Fact(Skip = "pending T-XXX")]`。
2. 运行 `dotnet test tests/OpenShell.IntegrationTests/OpenShell.IntegrationTests.csproj`。
3. 预期原有 8 个测试通过，新合规测试全部跳过。
4. 将 T-590 标记 `[x]`。

### Task 2: 修复 Core 三项行为缺陷

**Files:**
- Modify: `src/OpenShell.Core/Events/InProcessEventBus.cs`
- Modify: `src/OpenShell.Core/Errors/ErrorRecord.cs`
- Modify: `src/OpenShell.Core/Filter/ExprParser.cs`
- Modify: `tests/OpenShell.Core.Tests/Events/InProcessEventBusTests.cs`
- Modify: `tests/OpenShell.Core.Tests/Errors/ErrorRecordTests.cs`
- Modify: `tests/OpenShell.Core.Tests/Filter/ExprParserTests.cs`
- Modify: `tests/OpenShell.IntegrationTests/ProjectStabilityComplianceTests.cs`

**Steps:**
1. 移除三个既有 bug 测试及对应合规测试的 `Skip`，确认失败。
2. Dispose 使用 `Interlocked.Exchange` 保证只清理一次。
3. `ErrorRecord` 在 `IOException` 等分支前映射 `ArgumentException`。
4. Lexer 在 `LexNumber` 前扫描并验证完整 ISO 日期 token，保留普通数字行为。
5. 运行三个定向测试类，预期全部通过。

### Task 3: 修复 Provider 取消契约

**Files:**
- Modify: `tests/OpenShell.TestUtils/Contract/ProviderContractTests.cs`
- Modify: `src/OpenShell.Providers.FileSystem/FileSystemProvider.cs`
- Modify: `src/OpenShell.Providers.Remote/SftpProvider.cs`
- Modify: `tests/OpenShell.Providers.FileSystem.Tests/FileSystemProviderContractTests.cs`
- Modify: `tests/OpenShell.Providers.Remote.Tests/SftpProviderContractTests.cs`

**Steps:**
1. 用 `GetTestRoot()`、非空字符串和空属性集合构造反射参数。
2. 统一等待 Task、ValueTask 与 IAsyncEnumerable，并断言每个方法观察到取消。
3. 移除文件系统与 SFTP 取消测试的 `Skip`，确认失败。
4. 在两个 Provider 的公共异步入口首行调用 `cancellationToken.ThrowIfCancellationRequested()`。
5. 运行两个 Provider 测试项目，预期只剩 2 个真实 SFTP 集成 Skip。

### Task 4: 修复 CI SDK

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `tests/OpenShell.IntegrationTests/ProjectStabilityComplianceTests.cs`

**Steps:**
1. 移除 T-505 合规测试的 `Skip`，确认工作流仍声明 `8.0.x` 而失败。
2. 将 CI `dotnet-version` 改为 `10.0.x`。
3. 运行 T-505 合规测试，预期通过。

### Task 5: 全量验证与烟测

**Files:**
- Modify: `docs/project-stability-audit.md`
- Modify: `docs/project-stability-tasks.md`

**Steps:**
1. 运行 `dotnet build OpenShell.slnx --nologo`，预期 0 警告 / 0 错误。
2. 用 TRX 运行全解决方案测试并汇总，预期 0 失败、仅 2 个 SFTP Skip。
3. 运行真实 CLI `-Command` 和 `-File` 烟测，校验退出码与输出。
4. 启动 GUI，确认窗口进程稳定运行且无启动异常，然后正常终止测试进程。
5. 将 T-500~T-591 全部标记 `[x]`，在审计中记录最终计数。

