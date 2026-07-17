# ADR-0043: GUI 对话框服务

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M3
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0013 (GUI MVVM), ADR-0014 (Host Bridge), ADR-0026 (错误模型), ADR-0040 (事件总线), ADR-0044 (进度报告 UI)

## Context

ADR-0013 强制约束 ViewModel 不直接依赖 Avalonia 类型。但 GUI 文件管理器在 M3 必须提供下列交互入口，而这些交互天然依赖 Avalonia 的 `StorageProvider` / `Window` API：

- **Open / Save File 对话框**：支持 "另存为..." / "打开..." 之类的标准文件操作，需要文件过滤器、多选、默认扩展名
- **Folder Browser 对话框**：选择 Copy / Move 操作的目标目录、`Set-Location` 的目标
- **MessageBox**：确认 / 警告 / 错误 / 询问，参考 ADR-0026 的错误呈现模型
- **自定义输入对话框**：如 "重命名为..." / "新建文件夹名..." / "Go to folder..."，需要内联校验
- **进度对话框**：与 ADR-0044 配合，长操作的进度展示
- **高级对话框**：About / Preferences / Find / Properties 详情，内容随主题、版本变化

如果直接在 ViewModel 调用 `StorageProvider.OpenFilePickerAsync(...)` 或 `new ContentDialog().ShowAsync()`，会破坏：

1. **可测试性**：单测需引用 Avalonia，且 `StorageProvider` 依赖拓扑复杂
2. **CLI 复用**：CliHost 同样调用 ViewModel 时无法弹原生 GUI 对话框
3. **主题一致性**：每个 ViewModel 各自 `new ContentDialog` 难统一 Style / ThemeDictionaries
4. **测试覆盖**：难以验证 "用户取消后是否真的没执行副作用"

需求约束：

- ViewModel 单测不引用 `Avalonia.*`
- 同一 ViewModel 在 GUI Host 弹 Avalonia 原生对话框，在 CLI Host 退化为 Console 文本交互
- 接口在 Core / Abstractions 层，实现在 Host 层（参考 ADR-0014 的 Host Bridge 做法）
- 所有异步方法返回 `Task`，支持 `CancellationToken`，与 `ICommandDispatcher` 取消模型一致
- 对话框样式必须随主题（Light/Dark）切换，并满足 a11y（Tab 焦点链 / 屏幕阅读器 / Esc 关闭）
- 最近一次使用的目录需要持久化到缓存，避免每次从 cwd 起步

## Decision

引入 **`IDialogService`** 作为所有 GUI 对话框的统一入口，按 ADR-0014 的 Host Bridge 模式分层：接口在 `OpenShell.Gui.Abstractions`，实现在 Host（Avalonia / Console 各一）。

### 1. 设计原则

| 原则 | 实现要点 |
|---|---|
| **接口在 Core** | `OpenShell.Gui.Abstractions` 项目里定义 `IDialogService` 与所有 Options / Result 类型，Core 引用即可，参考 ADR-0014 Host Bridge 的做法 |
| **实现在 Gui.Host** | `OpenShell.Gui.Host` 用 Avalonia 的 `IStorageProvider` / `MainWindow` 实现 `AvaloniaDialogService` |
| **CLI 退化实现** | `OpenShell.Cli.Host` 提供 `ConsoleDialogService`：MessageBox 用 `Console.WriteLine` + `ReadLine` 询问；文件/文件夹对话框退化为路径输入 prompt |
| **可单测** | ViewModel 单测注入 mock `IDialogService`（如 NSubstitute / Moq），断言调用次数与参数，无需启动 Avalonia.Headless |
| **统一入口** | 所有对话框走 `IDialogService`，避免每 ViewModel 各自 `new ContentDialog()`；主题、a11y、最近路径等横切关注点集中处理 |
| **Options + Result 模式** | 对话框参数用 `record` 类型表达（不可变、结构相等），返回值用 `record` / `enum` 表达，便于断言 |

### 2. 接口契约

```csharp
namespace OpenShell.Gui.Abstractions;

/// <summary>
/// 所有 GUI 对话框的统一入口。Per ADR-0043.
/// ViewModel 通过 DI 注入；CLI Host 提供文本降级实现。
/// </summary>
public interface IDialogService
{
    Task<DialogResult> ShowMessageBoxAsync(MessageBoxOptions options, CancellationToken ct = default);
    Task<IReadOnlyList<ItemPath>?> ShowOpenFileDialogAsync(FileDialogOptions options, CancellationToken ct = default);
    Task<ItemPath?> ShowSaveFileDialogAsync(FileDialogOptions options, CancellationToken ct = default);
    Task<ItemPath?> ShowFolderBrowserAsync(FolderDialogOptions options, CancellationToken ct = default);
    Task<string?> ShowInputAsync(InputDialogOptions options, CancellationToken ct = default);
    Task<T?> ShowCustomAsync<T>(IDialogView<T> view, CancellationToken ct = default);
}
```

**MessageBox Options / Result**：

```csharp
public sealed record MessageBoxOptions
{
    public required string Title { get; init; }
    public required string Message { get; init; }
    public MessageBoxKind Kind { get; init; } = MessageBoxKind.Information;
    public MessageBoxButtons Buttons { get; init; } = MessageBoxButtons.OK;
    public string? Detail { get; init; }      // 折叠区域，参考 VS Code
    public ItemPath? RelatedPath { get; init; } // 用于在文件管理器里跳转
}

public enum MessageBoxKind { Information, Warning, Error, Question }
public enum MessageBoxButtons { OK, OKCancel, YesNo, YesNoCancel }
public enum DialogResult { OK, Cancel, Yes, No }
```

**文件 / 文件夹对话框**：

```csharp
public sealed record FileDialogOptions
{
    public string? Title { get; init; }
    public ItemPath? InitialDirectory { get; init; }
    public IReadOnlyList<FileFilter> Filters { get; init; } = Array.Empty<FileFilter>();
    public bool AllowMultiple { get; init; } = false;
    public string? DefaultExtension { get; init; }
    public string? DefaultFileName { get; init; }
}

public sealed record FileFilter(string Name, IReadOnlyList<string> Patterns);
// 用法: new FileFilter("Text Files", new[] { "*.txt", "*.md" })

public sealed record FolderDialogOptions
{
    public string? Title { get; init; }
    public ItemPath? InitialDirectory { get; init; }
}
```

**输入对话框**：

```csharp
public sealed record InputDialogOptions
{
    public required string Title { get; init; }
    public string? Label { get; init; }
    public string? DefaultValue { get; init; }
    public string? Placeholder { get; init; }
    public Func<string, string?>? Validator { get; init; }  // 返回 null 表示通过，否则错误消息
}
```

**自定义对话框**：About / Preferences / Find / Properties 等高级视图走通用 `IDialogView<T>` 接口，ViewModel 提供数据，View 提供渲染：

```csharp
public interface IDialogView<T>
{
    string Title { get; }
    Task<T> ShowAsync(IDialogHost host, CancellationToken ct);
}

public interface IDialogHost
{
    Task<TResult> ShowAsync<TResult>(object view, CancellationToken ct);
}
```

### 3. Avalonia 实现（在 `OpenShell.Gui.Host`）

```csharp
internal sealed class AvaloniaDialogService : IDialogService
{
    private readonly Window _mainWindow;
    private readonly IStorageProvider _storage;

    public AvaloniaDialogService(Window mainWindow)
    {
        _mainWindow = mainWindow;
        _storage = mainWindow.StorageProvider;
    }

    public async Task<DialogResult> ShowMessageBoxAsync(MessageBoxOptions opts, CancellationToken ct)
    {
        var dialog = new ContentDialog  // 或自定义 Window
        {
            Title = opts.Title,
            Content = BuildMessageBoxContent(opts),
            PrimaryButtonText = MapPrimary(opts.Buttons),
            SecondaryButtonText = MapSecondary(opts.Buttons),
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync(_mainWindow);
        return MapResult(result);
    }

    public async Task<ItemPath?> ShowFolderBrowserAsync(FolderDialogOptions opts, CancellationToken ct)
    {
        var startLocation = await ResolveStartLocationAsync(opts.InitialDirectory);
        var folders = await _storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = opts.Title ?? "Select Folder",
            SuggestedStartLocation = startLocation,
            AllowMultiple = false,
        });
        if (folders.Count == 0) return null;
        var path = ToItemPath(folders[0]);
        await RememberRecentDirectoryAsync(path);
        return path;
    }

    public async Task<ItemPath?> ShowSaveFileDialogAsync(FileDialogOptions opts, CancellationToken ct)
    {
        var startLocation = await ResolveStartLocationAsync(opts.InitialDirectory);
        var file = await _storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = opts.Title ?? "Save As",
            SuggestedStartLocation = startLocation,
            FileTypeFilter = MapFilters(opts.Filters),
            DefaultExtension = opts.DefaultExtension,
            SuggestedFileName = opts.DefaultFileName,
        });
        return file is null ? null : ToItemPath(file);
    }

    public async Task<string?> ShowInputAsync(InputDialogOptions opts, CancellationToken ct)
    {
        var vm = new InputDialogViewModel(opts);   // ReactiveViewModel
        var view = new InputDialogView { DataContext = vm };
        var result = await view.ShowDialog<DialogResult>(_mainWindow);
        return result == DialogResult.OK ? vm.Value : null;
    }

    public async Task<T?> ShowCustomAsync<T>(IDialogView<T> view, CancellationToken ct)
        => await view.ShowAsync(_dialogHost, ct);

    // ShowProgressDialogAsync 契约见 ADR-0044
}
```

实现要点：

- **`ContentDialog` vs 自定义 `Window`**：MessageBox 用 `ContentDialog`（轻量、原生 a11y），高级对话框（About / Preferences）用自定义 `Window` 以承载复杂布局
- **`StorageProvider` 通过 `MainWindow.StorageProvider` 获取**，不要 `new StorageProvider()`，保证生命周期与窗口一致
- **`ShowAsync` 调用必须传 `_mainWindow`** 作为 owner，否则对话框无父窗口、任务栏会出现孤儿条目
- **`MapPrimary` / `MapSecondary` / `MapResult`** 是纯函数，单测覆盖 `MessageBoxButtons` ↔ `ContentDialogButton` 的全排列
- **`BuildMessageBoxContent`** 根据 `Kind` 选择图标 + 根据 `Detail` 折叠区域 + 根据 `RelatedPath` 渲染 "Show in files" 链接
- **`RememberRecentDirectoryAsync`** 在对话框成功关闭后写入 `~/.openshell/cache/dialog-recent.toml`，下次 `ResolveStartLocationAsync` 默认读它

### 4. CLI 退化实现（在 `OpenShell.Cli.Host`）

```csharp
internal sealed class ConsoleDialogService : IDialogService
{
    public async Task<DialogResult> ShowMessageBoxAsync(MessageBoxOptions opts, CancellationToken ct)
    {
        var icon = opts.Kind switch
        {
            MessageBoxKind.Error   => "[ERROR] ",
            MessageBoxKind.Warning => "[WARN]  ",
            MessageBoxKind.Question=> "[?]     ",
            _                       => "[i]     ",
        };
        Console.WriteLine($"{icon}{opts.Title}");
        Console.WriteLine($"        {opts.Message}");
        if (opts.Detail is { } d) Console.WriteLine($"        {d}");

        return opts.Buttons switch
        {
            MessageBoxButtons.OK          => DialogResult.OK,
            MessageBoxButtons.OKCancel    => PromptYesNo("OK/Cancel? ", defaultYes: true)  ? DialogResult.OK     : DialogResult.Cancel,
            MessageBoxButtons.YesNo       => PromptYesNo("y/n? ",      defaultYes: true)  ? DialogResult.Yes    : DialogResult.No,
            MessageBoxButtons.YesNoCancel => PromptYesNoCancel("y/n/c? ", defaultYes: true),
            _                              => DialogResult.Cancel,
        };
    }

    public Task<ItemPath?> ShowFolderBrowserAsync(FolderDialogOptions opts, CancellationToken ct)
    {
        Console.Write($"{opts.Title ?? "Path"}: ");
        var input = Console.ReadLine();
        return Task.FromResult(string.IsNullOrEmpty(input) ? null : ItemPath.Parse(input));
    }

    public Task<string?> ShowInputAsync(InputDialogOptions opts, CancellationToken ct)
    {
        Console.WriteLine(opts.Title);
        if (opts.Label is { } l) Console.WriteLine($"  {l}");
        while (true)
        {
            Console.Write(opts.Placeholder is { } p ? $"  [{p}] " : "  > ");
            var input = Console.ReadLine();
            if (string.IsNullOrEmpty(input) && opts.DefaultValue is { } def) return Task.FromResult<string?>(def);
            if (opts.Validator is { } v && v(input!) is { } err)
            {
                Console.WriteLine($"  ! {err}");
                continue;
            }
            return Task.FromResult(string.IsNullOrEmpty(input) ? null : input);
        }
    }

    public Task<IReadOnlyList<ItemPath>?> ShowOpenFileDialogAsync(FileDialogOptions opts, CancellationToken ct)
    {
        Console.WriteLine(opts.Title ?? "Open File");
        Console.Write("  path(s), comma-separated: ");
        var line = Console.ReadLine();
        if (string.IsNullOrEmpty(line)) return Task.FromResult<IReadOnlyList<ItemPath>?>(null);
        var paths = line.Split(',').Select(s => ItemPath.Parse(s.Trim())).ToList();
        return Task.FromResult<IReadOnlyList<ItemPath>?>(paths);
    }

    public Task<ItemPath?> ShowSaveFileDialogAsync(FileDialogOptions opts, CancellationToken ct)
    {
        Console.WriteLine(opts.Title ?? "Save As");
        Console.Write("  path: ");
        var line = Console.ReadLine();
        return Task.FromResult(string.IsNullOrEmpty(line) ? null : ItemPath.Parse(line));
    }

    public Task<T?> ShowCustomAsync<T>(IDialogView<T> view, CancellationToken ct)
        => view.ShowAsync(_consoleDialogHost, ct).ContinueWith(t => (T?)t.Result);
}
```

实现要点：

- **MessageBox 文本对齐**：标题行 + 缩进的消息行 + 可选 Detail，保持终端宽度 < 100 字符的换行
- **`PromptYesNo` / `PromptYesNoCancel`**：单字符匹配（y/n/c），不区分大小写，空回车走 `defaultYes`
- **文件 / 文件夹对话框退化为路径输入**：CLI 用户习惯直接打字，不需要原生 picker
- **`ShowInputAsync` 循环校验**：调用 `Validator`，不通过则打印错误并重试，直到通过或用户取消（空回车且无 DefaultValue）
- **`IDialogView<T>` 在 CLI 退化为文本菜单**：About 输出版本号、Preferences 输出键值列表、Find 输出搜索结果、Properties 输出 `IItem` 详情

### 5. DI 注册

```csharp
// OpenShell.Gui.Host / Program.cs
services.AddSingleton<IDialogService, AvaloniaDialogService>();
services.AddSingleton<IDialogHost, AvaloniaDialogHost>();  // 用于 IDialogView<T> 的子窗口托管

// OpenShell.Cli.Host / Program.cs
services.AddSingleton<IDialogService, ConsoleDialogService>();
services.AddSingleton<IDialogHost, ConsoleDialogHost>();
```

注入方式：

- `PaneViewModel` / `TabViewModel` 等通过构造函数注入 `IDialogService`
- `IDialogHost` 仅由 `IDialogView<T>` 实现使用，不暴露给 ViewModel
- 单测项目用 `services.AddSingleton<IDialogService, MockDialogService>()` 或直接 `new ViewModel(new Mock<IDialogService>())`

### 6. ViewModel 使用模式

ADR-0013 约束：ViewModel 不直接依赖 Avalonia。本模式通过 `IDialogService` 完全隐藏 Avalonia 类型：

```csharp
public class PaneViewModel : ReactiveViewModel
{
    private readonly IDialogService _dialogs;
    private readonly ICommandDispatcher _dispatcher;

    public ReactiveCommand<Unit, Unit> MoveTo => ReactiveCommand.CreateFromTask(async () =>
    {
        var target = await _dialogs.ShowFolderBrowserAsync(new FolderDialogOptions
        {
            Title = "Move to...",
            InitialDirectory = CurrentLocation,
        });
        if (target is null) return;  // 用户取消

        var confirm = await _dialogs.ShowMessageBoxAsync(new MessageBoxOptions
        {
            Title = "Confirm Move",
            Message = $"Move {SelectedItems.Count} item(s) to {target.Display}?",
            Kind = MessageBoxKind.Question,
            Buttons = MessageBoxButtons.YesNo,
        });
        if (confirm != DialogResult.Yes) return;

        var ctx = BuildContext(currentLocation: CurrentLocation);
        await _dispatcher.InvokeAsync($"move-item {SelectedItems[0].Path} {target}", ctx, CancellationToken);
    });

    public ReactiveCommand<Unit, Unit> Rename => ReactiveCommand.CreateFromTask(async () =>
    {
        if (SelectedItem is null) return;
        var newName = await _dialogs.ShowInputAsync(new InputDialogOptions
        {
            Title = "Rename",
            Label = "New name",
            DefaultValue = SelectedItem.Name,
            Validator = v => string.IsNullOrWhiteSpace(v) ? "Name cannot be empty" : null,
        });
        if (newName is null) return;
        await _dispatcher.InvokeAsync($"rename-item {SelectedItem.Path} {newName}", ...);
    });
}
```

ViewModel 单测示例：

```csharp
[Test]
public async Task MoveTo_user_cancels_folder_picker_does_not_invoke_dispatcher()
{
    var dialogs = new Mock<IDialogService>();
    dialogs.Setup(d => d.ShowFolderBrowserAsync(It.IsAny<FolderDialogOptions>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync((ItemPath?)null);
    var dispatcher = new Mock<ICommandDispatcher>();
    var vm = new PaneViewModel(dialogs.Object, dispatcher.Object) { CurrentLocation = ItemPath.Parse("C:/Temp") };

    await vm.MoveTo.Execute();

    dispatcher.Verify(d => d.InvokeAsync(It.IsAny<string>(), It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
}
```

### 7. 错误呈现集成（与 ADR-0026 联动）

`ErrorRecord` 通过 `IDialogService.ShowMessageBoxAsync` 转为 MessageBox 弹窗：

```csharp
public static class DialogErrorExtensions
{
    public static Task<DialogResult> ShowErrorAsync(
        this IDialogService dialogs, ErrorRecord err, CancellationToken ct = default)
    {
        return dialogs.ShowMessageBoxAsync(new MessageBoxOptions
        {
            Title = err.Severity switch
            {
                ErrorSeverity.Critical => "Critical Error",
                ErrorSeverity.Error    => "Error",
                ErrorSeverity.Warning  => "Warning",
                _                       => "Notice",
            },
            Message = err.Message,
            Kind = err.Severity switch
            {
                ErrorSeverity.Critical or ErrorSeverity.Error => MessageBoxKind.Error,
                ErrorSeverity.Warning                            => MessageBoxKind.Warning,
                _                                                 => MessageBoxKind.Information,
            },
            Buttons = MessageBoxButtons.OK,
            Detail = err.FullTrace,           // 折叠区域显示堆栈 / 上下文
            RelatedPath = err.RelatedPath,    // 用于"在文件管理器里显示"
        }, ct);
    }
}
```

汇总规则：

- **单错误**：直接 `ShowErrorAsync`
- **多错误**（`OperationResult.Errors` 含多项）：汇总到一个 MessageBox + Detail 折叠列表，标题形如 "5 errors occurred"
- **严重错误**（`CircuitBroken` / `OperationTimeout`）：弹 "是否重试" 对话框，`Buttons.YesNo`；用户选 Yes 则重跑命令，No 则放弃
- **`RelatedPath`**：MessageBox 内容区底部渲染 "Show in files" 链接，点击触发 `IEventBus.Publish(new NavigationRequested(path))`（见 ADR-0040 事件总线），由 `MainViewModel` 订阅切到目标 tab / pane

### 8. About / Preferences / Find / Properties 对话框

作为 `IDialogView<T>` 自定义视图实现：

```csharp
public sealed class AboutDialogView : IDialogView<DialogResult>
{
    public string Title => "About OpenShell";
    public async Task<DialogResult> ShowAsync(IDialogHost host, CancellationToken ct)
    {
        var view = new AboutDialogWindow  // 自定义 Avalonia Window
        {
            DataContext = new AboutViewModel(),  // 版本号 / 第三方许可 / 链接
        };
        await host.ShowAsync<DialogResult>(view, ct);
        return DialogResult.OK;
    }
}

public sealed class PreferencesDialogView : IDialogView<DialogResult>
{
    public string Title => "Preferences";
    public async Task<DialogResult> ShowAsync(IDialogHost host, CancellationToken ct)
    {
        var view = new PreferencesDialogWindow  // 含主题切换 / 默认 Provider / 快捷键
        {
            DataContext = new PreferencesViewModel(),
        };
        return await host.ShowAsync<DialogResult>(view, ct);
    }
}
```

要点：

- ViewModel 提供数据（版本号、设置项、搜索结果、`IItem` 详情），View 提供渲染（控件 / 主题 / 布局）
- ViewModel 同样不依赖 Avalonia，仅依赖 Core 抽象（`IConfigStore` / `IProviderRegistry`）
- `IDialogView<T>` 实现位于 `OpenShell.Gui.Views`，由 `IDialogHost` 在 Avalonia 端转 `Window.ShowDialog`，在 CLI 端转文本渲染
- Properties 对话框订阅 `IEventBus` 的 `SelectionChanged`（ADR-0040），选中项变化时自动刷新

### 9. 进度对话框（与 ADR-0044 联动）

完整契约见 ADR-0044。本 ADR 仅定义接口入口，避免双 ADR 重复：

```csharp
public interface IDialogService
{
    // ... 上述方法 ...

    /// <summary>展示带进度条 + 取消按钮的模态对话框。完整契约见 ADR-0044。</summary>
    Task<DialogResult> ShowProgressDialogAsync(
        IProgress<OperationProgress> progress,
        ProgressDialogOptions options,
        CancellationToken ct = default);
}

public sealed record ProgressDialogOptions
{
    public required string Title { get; init; }
    public string? Status { get; init; }
    public bool AllowCancel { get; init; } = true;
    public bool ShowSubProgress { get; init; } = true;  // 是否显示嵌套 Depth
}
```

### 10. 线程模型

- **所有 `ShowXxxAsync` 方法必须在 UI 线程调用**（Avalonia 要求 `ShowAsync` 在 UI 线程）
- ViewModel 调用前用 `Dispatcher.UIThread.Post` 切回 UI 线程；`ReactiveCommand.CreateFromTask` 默认在 `RxApp.MainThreadScheduler` 跑，已满足
- 返回 `Task` 在 UI 线程完成，调用方继续操作 UI 控件（如修改 `ObservableCollection`）无需再次切线程
- CLI 实现无 UI 线程概念，直接在调用方线程同步执行（`Console.ReadLine` 阻塞）

### 11. 可访问性（a11y）

对话框必须满足：

- **Tab 焦点链**：按 Tab 在所有可交互控件之间循环，焦点顺序自上而下 / 自左而右
- **屏幕阅读器 AutomationPeer**：所有按钮 / 文本框 / 列表暴露 `AutomationProperties.Name`，避免"未命名按钮"
- **Esc 关闭**：所有对话框 Esc 键返回 `DialogResult.Cancel`（或 `null`，根据返回类型）
- **Enter 默认按钮**：MessageBox 默认 Primary 按钮，Input 默认 OK 按钮
- **高对比度**：随主题切换，Light / Dark / System 三套色彩方案
- **键盘可达**：所有操作仅键盘可完成，不强制鼠标

Avalonia 默认支持以上能力，无需额外代码。CLI 退化实现无 a11y 概念。

### 12. 最近路径持久化

`FileDialogOptions.InitialDirectory` 与 `FolderDialogOptions.InitialDirectory` 若为 `null`，默认从 `~/.openshell/cache/dialog-recent.toml` 读取最近一次使用的目录：

```toml
# ~/.openshell/cache/dialog-recent.toml
[last_directory]
open_file = "C:/Users/me/Documents"
save_file = "C:/Users/me/Desktop"
folder    = "D:/Projects/openshell"
```

- **写入时机**：对话框成功关闭（用户选了路径）后，按对话框类型更新对应字段
- **读取时机**：`ResolveStartLocationAsync` 在 `opts.InitialDirectory` 为 `null` 时回退到缓存
- **缓存清理**：受 ADR-0022 缓存清理策略约束，5MB 上限；TOML 文件本身仅几 KB，但若记录最近 N 条路径（而非仅最新一条）需控制 N
- **隐私**：路径可能含敏感信息（如 `C:/Users/me/Secret`），缓存文件权限设为 0600（仅当前用户可读写）
- **跨 host 共享**：GUI 与 CLI 共用同一缓存文件，CLI 退化实现同样读写

## Alternatives Considered

1. **直接在 ViewModel 调 Avalonia API**（如 `_storage.OpenFilePickerAsync`）：被否决
   - 违反 ADR-0013「ViewModel 不引用 `Avalonia.*`」
   - ViewModel 单测需引用 Avalonia + 启动无头 Avalonia 应用
   - CLI Host 无法复用同一 ViewModel

2. **`MessageBox.Avalonia` 第三方库**（如 `Avalonia.Controls.Notifications`）：被否决
   - 第三方库 API 不稳定，升级 Avalonia 时可能锁死
   - 样式 / 主题定制能力受限，难以匹配 OpenShell 自有主题
   - CLI 退化需自己另写一套，无法用同一抽象

3. **每对话框独立接口**（`IMessageBoxService` / `IFilePickerService` / `IFolderBrowserService` / `IInputService` / ...）：被否决
   - 接口爆炸：5 个对话框 5 个接口 + 5 套 Options + 5 套 Result 类型
   - DI 注册与构造函数注入参数列表过长
   - 实际 5 个方法语义高度相似（都返回 `Task<T?>`），合并无信息损失
   - 横切关注点（最近路径、a11y、主题）需重复实现 5 次

4. **事件总线解耦**（ViewModel 发 `ShowMessageBoxRequested` 事件，View 订阅）：被否决
   - 异步结果回传困难：事件是单向广播，需另一路 `MessageBoxClosed` 事件 + `TaskCompletionSource` 桥接
   - 调用栈断裂，调试困难
   - 多对话框同时打开时事件难以配对（哪个 Closed 对应哪个 Requested）
   - 与 ADR-0014 已有的 `UserInputEvent` 语义重叠

5. **同步阻塞 API**（`DialogResult ShowMessageBox(...)` 而非 `Task<DialogResult>`）：被否决
   - 阻塞 UI 线程，与 Avalonia 异步模型不兼容
   - 无法响应 `CancellationToken`，取消传播失效
   - 与 ADR-0010 Pipeline 的异步命令链不兼容

6. **每对话框返回不同 Result 类型**（如 `OpenFileResult` 含 `SelectedPaths` + `FilterIndex`）：被否决
   - 当前 `ItemPath?` / `IReadOnlyList<ItemPath>?` 已足够
   - `FilterIndex` 等 Avalonia 特有信息对 ViewModel 无意义，不应跨抽象层
   - 若未来需要可向后兼容添加（接口默认方法）

## Consequences

### 优势

- **ViewModel 可单测**：注入 mock `IDialogService` 即可在 .NET 控制台测试项目跑，无需 Avalonia.Headless
- **双端复用**：同一 `PaneViewModel` 在 GUI Host 弹原生 Avalonia 对话框，在 CLI Host 退化为 Console prompt，零代码改动
- **a11y 内建**：Avalonia 默认支持 Tab 焦点链 / AutomationPeer / Esc 关闭 / Enter 默认，无需额外代码
- **主题统一**：所有对话框由 `AvaloniaDialogService` 集中创建，主题切换一次到位，避免每 ViewModel 各自 `new` 的样式漂移
- **错误呈现统一**：`ErrorRecord` → MessageBox 的转换集中在 `DialogErrorExtensions`，与 ADR-0026 错误模型联动
- **最近路径记忆**：用户无需每次从 cwd 起步，体验与原生 OS 文件管理器一致
- **横切关注点集中**：最近路径持久化、a11y 校验、错误汇总等只在 `AvaloniaDialogService` 实现一次
- **取消传播一致**：所有 `ShowXxxAsync` 接受 `CancellationToken`，与 `ICommandDispatcher` / `IHost.CommandCancellation` 取消模型一致

### 代价

- **多一层抽象**：ViewModel → `IDialogService` → `AvaloniaDialogService` → Avalonia API，调用栈多 2 帧
- **CLI 退化体验差**：Console prompt 无法显示过滤器 / 多选 / 图标，CLI 用户需手动输入路径
- **对话框样式与主题需统一**：自定义 `Window`（About / Preferences）需手动应用 `ThemeDictionaries`，与 `ContentDialog` 默认主题保持一致
- **接口演化需谨慎**：`IDialogService` 一旦发布，添加方法需默认实现，否则破坏下游 mock
- **`IDialogView<T>` 增加学习成本**：About / Preferences 等高级对话框需理解 `IDialogView<T>` + `IDialogHost` 双接口，比直接 `new Window().Show()` 多一步
- **测试 mock 编写量**：每个 ViewModel 测试场景需配置 `IDialogService` mock 返回值，比直接断言 UI 控件更冗长

### 约束

- **接口在 Core**：`IDialogService` / `MessageBoxOptions` / `FileDialogOptions` / `FolderDialogOptions` / `InputDialogOptions` / `IDialogView<T>` / `IDialogHost` / 所有 `enum` 必须在 `OpenShell.Gui.Abstractions`，不引用 `Avalonia.*`
- **实现在 Host**：`AvaloniaDialogService` 在 `OpenShell.Gui.Host`，`ConsoleDialogService` 在 `OpenShell.Cli.Host`，均标记为 `internal sealed`
- **ViewModel 禁止直接 `new` 对话框**：不允许在 ViewModel 出现 `new ContentDialog()` / `new Window()` / `StorageProvider` 调用，代码评审作为硬性检查项
- **最近路径缓存 5MB 上限**：受 ADR-0022 约束，`dialog-recent.toml` 若改为记录最近 N 条路径需控制 N × 平均路径长度 ≤ 5MB
- **Esc 永远返回 `Cancel`**：所有对话框 Esc 键返回 `DialogResult.Cancel`（或 `null`），不允许 Esc 触发其他行为
- **`ShowXxxAsync` 必须在 UI 线程调用**（Avalonia 端）；CLI 端无此约束
- **`CancellationToken` 必须透传**：所有 `ShowXxxAsync` 实现必须接受并响应 `CancellationToken`，不得吞掉
- **对话框样式必须随主题切换**：Light / Dark / System 三套，切换时 `Application.Styles` 重置并添加
- **`IDialogView<T>` 实现必须位于 `OpenShell.Gui.Views`**：ViewModel 不引用 Views 项目，仅通过 `IDialogService.ShowCustomAsync<T>` 间接调用
- **`AvaloniaDialogService` 是单例**：所有对话框共享同一 `_mainWindow` owner，避免多窗口 owner 错乱
- **测试项目 `OpenShell.Gui.Abstractions.Tests` 与 `OpenShell.Gui.Host.Tests` 分离**：前者测 Options / Result 的不可变语义，后者测 Avalonia 实现（用 Avalonia.Headless）
- **`MapPrimary` / `MapSecondary` / `MapResult` 是纯函数**：必须 100% 单测覆盖 `MessageBoxButtons` × `ContentDialogResult` 全排列
