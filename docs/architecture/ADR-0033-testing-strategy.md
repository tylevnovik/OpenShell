# ADR-0033: 测试策略

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: 跨阶段（M0 起，每阶段扩展）
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0001 (Provider 能力), ADR-0004 (命令), ADR-0016 (ALC)

## Context

OpenShell 是框架底座，对 API 稳定性与跨 Provider 一致性要求高。需要：

1. **API 稳定性**：Core 接口变更需契约测试守护
2. **Provider 一致性**：所有 Provider 通过同一组基础测试（Get-Item / Get-Children / Get-Content）
3. **命令一致性**：所有命令通过 `CommandContractTests`
4. **跨平台**：CI 跑 Windows / Linux / macOS
5. **性能基线**：关键路径性能不回归
6. **UI 测试**：GUI 自动化（无显示环境）
7. **e2e**：从命令行输入到输出验证完整流程
8. **覆盖率**：Core 关键路径高覆盖

参考：
- xUnit + FluentAssertions（测试栈）
- Avalonia.Headless（无显示 UI 测试）
- BenchmarkDotNet（基准）
- Playwright / Appium（e2e，可选）

## Decision

### 1. 测试金字塔

```
        ┌─────────────┐
        │   e2e (5%)  │      完整流程
        ├─────────────┤
        │  集成 (20%)  │     跨组件协作
        ├─────────────┤
        │  契约 (25%)  │     API 稳定性
        ├─────────────┤
        │ 单元 (50%)   │     独立类 / 函数
        └─────────────┘
```

### 2. 单元测试

每项目独立测试项目：

```
src/OpenShell.Core/
└── tests/OpenShell.Core.Tests/
    ├── Items/ItemTests.cs
    ├── Paths/ItemPathTests.cs
    ├── Providers/ProviderRegistryTests.cs
    ├── Commands/CommandRegistryTests.cs
    └── ...
```

xUnit + FluentAssertions：

```csharp
public sealed class ItemPathTests
{
    [Fact]
    public void Parse_with_provider_prefix_extracts_correctly()
    {
        var path = ItemPath.Parse("fs::C:/Users/foo");
        path.Provider.Should().Be("fs");
        path.InternalPath.Should().Be("C:/Users/foo");
        path.IsRooted.Should().BeTrue();
    }

    [Theory]
    [InlineData("zip::archive.zip/sub", "zip", "archive.zip/sub")]
    [InlineData("reg::HKLM/Software", "reg", "HKLM/Software")]
    [InlineData("bare/path", "fs", "bare/path")]
    public void Parse_handles_various_formats(string input, string provider, string internalPath)
    {
        var p = ItemPath.Parse(input);
        p.Provider.Should().Be(provider);
        p.InternalPath.Should().Be(internalPath);
    }
}
```

### 3. 契约测试（关键）

#### ProviderContractTests

每个 Provider 必须通过的测试基类：

```csharp
public abstract class ProviderContractTests<TProvider> where TProvider : IProvider
{
    protected abstract TProvider CreateProvider();
    protected abstract ItemPath GetTestRoot();

    [Fact]
    public async Task GetItemAsync_returns_null_for_nonexistent()
    {
        var provider = CreateProvider();
        if (provider is not IItemProvider itemProvider) return;

        var result = await itemProvider.GetItemAsync(
            GetTestRoot().Combine("nonexistent-12345"), default);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetChildrenAsync_returns_enumerable()
    {
        var provider = CreateProvider();
        if (provider is not IContainerProvider container) return;

        var items = await container.GetChildrenAsync(
            GetTestRoot(), new EnumerationOptions(), default).ToListAsync();
        items.Should().NotBeNull();
    }

    [Fact]
    public async Task Capabilities_match_implemented_interfaces()
    {
        var provider = CreateProvider();
        var expected = ComputeExpectedCapabilities(provider);
        provider.Capabilities.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task All_methods_accept_cancellation()
    {
        // 用已取消 token 调用所有方法，验证抛 OperationCanceledException
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var provider = CreateProvider();
        // ... 反射调用所有异步方法
    }
}
```

每 Provider 测试项目继承并实现：

```csharp
public sealed class FileSystemProviderContractTests :
    ProviderContractTests<FileSystemProvider>
{
    protected override FileSystemProvider CreateProvider() => new();
    protected override ItemPath GetTestRoot() =>
        new() { Provider = "fs", InternalPath = TestDataRoot };
}
```

#### CommandContractTests

每个命令必须通过：

- `Args` 是 record，可默认构造
- `ExecuteAsync` 接受 default `CancellationToken` 不抛 NRE
- 无参执行返回非空流（对 Source 类命令）
- 反射访问 `[Verb]` / `[Parameter]` 特性

### 4. 集成测试

跨组件：

- `OpenShell.IntegrationTests` 项目
- 测试 `ICommandDispatcher` + `IProviderRegistry` + `IHost` 完整流程
- 用临时目录、临时 zip 文件等真实 IO

```csharp
public sealed class CopyItemIntegrationTests
{
    [Fact]
    public async Task CopyItem_fs_to_fs_creates_destination()
    {
        using var tempDir = new TempDir();
        var src = tempDir.CreateFile("a.txt", "hello");
        var dst = src + ".copy";

        var dispatcher = BuildDispatcher();
        await dispatcher.InvokeAsync($"copy-item {src} {dst}", BuildContext(), default);

        File.Exists(dst).Should().BeTrue();
        (await File.ReadAllTextAsync(dst)).Should().Be("hello");
    }
}
```

### 5. e2e 测试

CLI e2e：

- 启动子进程 `openshell-cli.exe`
- 通过 stdin 输入命令
- 断言 stdout 输出

```csharp
public sealed class CliE2ETests
{
    [Fact]
    public async Task Ls_command_outputs_items()
    {
        using var tempDir = new TempDir();
        File.WriteAllText(Path.Combine(tempDir.Path, "a.txt"), "x");

        var output = await RunCliAsync($"cd {tempDir.Path}\nls\nexit\n");
        output.Should().Contain("a.txt");
    }
}
```

GUI e2e：

- Avalonia.Headless 模式启动
- 模拟点击 / 输入
- 断言 UI 状态

```csharp
public sealed class GuiE2ETests
{
    [Fact]
    public async Task DoubleClickDirectoryNavigates()
    {
        using var app = StartHeadlessApp();
        var mainWindow = app.GetMainWindow();
        var listBox = mainWindow.Find<ListBox>();
        // 找到子目录项
        var dirItem = listBox.Items.OfType<IItem>().First(i => i.Kind == ItemKind.Directory);
        listBox.SelectedItem = dirItem;
        // 双击
        mainWindow.DoubleClick(listBox);
        // 验证 CurrentLocation 变化
        await Wait.Until(() => vm.CurrentLocation == dirItem.Path);
    }
}
```

### 6. 跨平台 CI

GitHub Actions 矩阵：

```yaml
strategy:
  matrix:
    os: [ubuntu-latest, windows-latest, macos-latest]
runs-on: ${{ matrix.os }}
steps:
  - uses: actions/checkout@v4
  - uses: actions/setup-dotnet@v4
    with:
      dotnet-version: '8.0.x'
  - run: dotnet test --logger trx --collect:"XPlat Code Coverage"
```

Registry Provider 测试仅在 Windows 跑：

```csharp
[Fact]
public async Task RegistryProvider_lists_HKLM()
{
    if (!OperatingSystem.IsWindows()) return;
    // ...
}
```

### 7. 覆盖率

- Coverlet 收集
- 目标：
  - `OpenShell.Core` 80%+
  - `OpenShell.Providers.*` 70%+
  - `OpenShell.Cli.Host` / `OpenShell.Gui.Host` 50%+（UI 难全覆盖）
- PR 不允许降低覆盖率

Codecov / Coveralls 上传，徽章在 README。

### 8. 性能基准

BenchmarkDotNet：

- `ItemPath.Parse` 性能
- `FileSystemProvider.GetChildrenAsync` 吞吐
- `CommandRegistry.Resolve` 延迟
- Pipeline 节点链延迟

```csharp
[MemoryDiagnoser]
public class ItemPathBenchmarks
{
    [Benchmark]
    public ItemPath Parse_with_provider() => ItemPath.Parse("fs::C:/Users/foo");

    [Benchmark]
    public ItemPath Parse_bare() => ItemPath.Parse("C:/Users/foo");
}
```

CI 每周跑一次，结果存档，PR 引入性能回归时警告。

### 9. 随机 / 模糊测试

`property_tests`：

```csharp
[Property]
public void Parse_Display_RoundTrip(ItemPath path)
{
    var parsed = ItemPath.Parse(path.Display);
    parsed.Should().Be(path);
}
```

`FsCheck` 库，针对 ItemPath / DSL Parser 等核心组件。

### 10. 测试数据

`tests/TestData/` 共享数据：

- 小 zip / tar.gz 测试包
- 示例图片 / 文本
- 注册表导出 .reg 文件

测试用临时目录隔离，并发安全。

### 11. 测试覆盖率门禁

CI 检查：

- Core 行覆盖率 < 80% → fail
- 任何 PR 引入未测试的公开方法 → warning
- 契约测试缺失 → fail

### 12. 测试命名约定

- `Method_Scenario_ExpectedBehavior`
- 如：`Parse_with_provider_prefix_extracts_correctly`

### 13. 测试组织

每测试类一个被测类：

- `ItemPathTests` 测 `ItemPath`
- `FileSystemProviderTests` 测 `FileSystemProvider`
- 一个测试文件不超 500 行

### 14. Mock 策略

- 单元测试：用 NSubstitute mock 依赖
- 集成测试：用真实实现 + 临时文件系统
- e2e：不 mock，跑真实流程

避免 over-mock，集成与 e2e 是契约保障。

## Alternatives Considered

1. **仅单元测试**：被否决，跨组件 bug 难发现
2. **仅 e2e**：被否决，调试难、慢
3. **NUnit / MSTest**：被否决，xUnit 主流且现代
4. **Moq**：被否决，NSubstitute API 更直观
5. **手动测试**：被否决，回归无保障
6. **不实现基准测试**：被否决，性能回归难发现
7. **Selenium UI 测试**：被否决，Avalonia.Headless 更轻量

## Consequences

### 优势
- API 稳定性契约守护
- 跨 Provider 一致性保障
- 性能回归可发现
- 跨平台 CI 自动化
- PR 质量有数据支撑

### 代价
- 测试代码量约等于生产代码
- CI 时间（跨平台矩阵约 15-30 分钟）
- 基准测试维护
- e2e 调试复杂

### 约束
- 测试方法名必须遵循 `Method_Scenario_Expected`
- 单元测试不得依赖文件系统 / 网络
- 集成测试必须用 `TempDir` 隔离
- 契约测试必须可继承，便于新 Provider 接入
- e2e 测试必须可在无显示环境跑
- 覆盖率门禁必须可绕过（紧急修复时 `--no-coverage-check`，但需评审）
- 基准测试结果必须存档
- Property 测试用 FsCheck，不引入其他 fuzz 库
- 测试数据必须可重现（无随机种子未指定）
- CI 失败必须阻断合并
- 测试代码必须和生产代码同 PR 提交

## Implementation Notes

### UI Smoke Tests (Avalonia.Headless)

`tests/OpenShell.Gui.Host.Tests/` 已落地 Avalonia.Headless UI 冒烟测试基础设施，对应本文档 §3 测试金字塔顶端与 Context §6。

- **测试栈**：`[AvaloniaFact]`（Avalonia.Headless.XUnit 11.0.10）+ xUnit + FluentAssertions。
- **入口**：`TestAppBuilder.BuildAvaloniaApp()` 通过 `AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).UseReactiveUI().WithInterFont()` 构造无头应用；`[AvaloniaFact]` 通过反射发现此方法。
- **TestApp**：复用生产 `FluentTheme` 但跳过 `OnFrameworkInitializationCompleted` 中的 MainWindow 自动创建（生产 `App` 依赖 `Program.Services` 完整 Generic Host）。测试直接 `new MainWindow()` 并手工注入 `MainViewModel`（真实 Core 服务 + `StubDialogService`）。
- **覆盖范围**：MainWindow 结构（菜单 / 双窗格 / 状态栏 / Task Center 面板可见性切换）+ RefreshCommand 冒烟。共 11 个 `[AvaloniaFact]` 用例。
- **InternalsVisibleTo**：`OpenShell.Gui.Host.csproj` 已向 `OpenShell.Gui.Host.Tests` 暴露 internal 类型（`MainWindow` / `App`）。

