# ADR-0021: 命令互转（GUI ↔ CLI 互通）

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M5
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0008 (CLI REPL), ADR-0013 (GUI MVVM), ADR-0014 (Bridge)

## Context

M5 实现 CLI 与 GUI 之间的命令互转：

1. **GUI → CLI**：在 GUI 当前目录打开终端，CLI 的 `CurrentLocation` 同步到 GUI 的位置
2. **CLI → GUI**：`out-gridview` 命令弹出 GUI 窗口，展示对象流
3. **GUI → CLI → GUI**：GUI 启动 CLI 子进程跑命令，结果回 GUI（如 GUI 没实现的复杂 Pipeline）
4. **CLI → GUI → CLI**：CLI 启动 GUI 浏览，用户在 GUI 选中文件后，路径回 CLI 作为下条命令参数
5. **多窗口状态同步**：GUI 多 tab + 多 CLI 进程，位置/选中需同步

需求约束：

- 同进程不能同时跑 CLI 与 GUI（Avalonia 与 Console 互斥）
- 子进程启动延迟 < 500ms
- IPC 协议稳定（版本兼容）
- 跨平台 IPC（Windows 命名管道 / Unix Domain Socket）
- 用户可关闭互转功能（脱机模式）

## Decision

### 1. IPC 协议

采用 **长度前缀 JSON over Named Pipe (Windows) / Unix Domain Socket (Linux/Mac)**：

```
[4 字节 big-endian 长度][JSON payload]
```

消息类型：

```csharp
public abstract record IpcMessage
{
    public abstract string Type { get; }
}

public sealed record IpcHandshake(
    int ProtocolVersion,
    HostKind SourceKind,
    Guid SessionId) : IpcMessage;

public sealed record IpcLocationChanged(ItemPath NewLocation) : IpcMessage;

public sealed record IpcSelectionChanged(IReadOnlyList<IItem> Selected) : IpcMessage;

public sealed record IpcCommandRequest(
    string CommandLine,
    ItemPath WorkingDirectory,
    Guid RequestId) : IpcMessage;

public sealed record IpcCommandResponse(
    Guid RequestId,
    int ExitCode,
    IReadOnlyList<IItem>? ResultItems,
    string? Error) : IpcMessage;

public sealed record IpcShowGridRequest(
    IReadOnlyList<IItem> Items,
    ViewSpec Spec,
    Guid RequestId) : IpcMessage;

public sealed record IpcShowGridResponse(
    Guid RequestId,
    IReadOnlyList<IItem>? SelectedItems) : IpcMessage;

public sealed record IpcShutdown() : IpcMessage;
```

### 2. IPC 端点命名

- Windows：`\\.\pipe\openshell-{sessionId}`
- Linux/Mac：`/tmp/openshell-{sessionId}.sock`

`sessionId` 来自启动进程的环境变量 `OPENSHELL_SESSION_ID`，子进程继承父进程的 session ID，建立 IPC 通道。

### 3. IpcTransport

```csharp
public interface IIpcTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    IObservable<IpcMessage> Messages { get; }
    ValueTask SendAsync(IpcMessage message, CancellationToken ct);
    ValueTask<IpcMessage> RequestAsync(IpcMessage request, TimeSpan timeout, CancellationToken ct);
}
```

实现：
- `NamedPipeIpcTransport` (Windows)
- `UnixSocketIpcTransport` (Linux/Mac)

启动时父进程创建 transport，子进程连接。

### 4. IGuiLauncher

```csharp
public interface IGuiLauncher
{
    /// <summary>启动 GUI 子进程，并建立 IPC 通道。</summary>
    ValueTask<IIpcTransport> LaunchAsync(ItemPath? initialLocation, CancellationToken ct);

    /// <summary>请求 GUI 弹出 grid view 展示对象流。</summary>
    async ValueTask<IReadOnlyList<IItem>?> ShowGridAsync(
        IAsyncEnumerable<IItem> items, ViewSpec spec, CancellationToken ct)
    {
        var transport = await LaunchAsync(null, ct);
        var collected = await items.ToListAsync(ct);
        var req = new IpcShowGridRequest(collected, spec, Guid.NewGuid());
        var resp = (IpcShowGridResponse)await transport.RequestAsync(req, TimeSpan.FromMinutes(10), ct);
        return resp.SelectedItems;
    }
}
```

CLI host 的 `out-gridview` 命令调用此接口：

```csharp
[Verb("Out", Noun = "Gridview", PipelineOnly = true)]
public sealed class OutGridviewCommand : IPipelineSink
{
    public record Args([property: Parameter] ViewSpec? Spec);

    public async ValueTask Consume(IAsyncEnumerable<IItem> input, CommandContext ctx, CancellationToken ct)
    {
        var launcher = ctx.Services.GetRequiredService<IGuiLauncher>();
        await launcher.ShowGridAsync(input, Args.Spec, ct);
    }
}
```

### 5. ICliLauncher

GUI host 调用，启动 CLI 子进程：

```csharp
public interface ICliLauncher
{
    /// <summary>在指定位置启动 CLI，建立 IPC。</summary>
    ValueTask<IIpcTransport> LaunchAsync(ItemPath initialLocation, CancellationToken ct);
}
```

GUI "在此处打开终端" 按钮调用。

### 6. 状态同步

通过 IPC 消息：

- **位置同步**：GUI tab 切换 → 推 `IpcLocationChanged` → CLI 子进程更新 `CurrentLocation`
- **选中同步**：GUI ListBox 选中 → `IpcSelectionChanged` → CLI 子进程的 Selection 更新
- **CLI → GUI**：CLI 跑完命令 → `IpcCommandResponse` → GUI 更新列表

同步策略：
- 单向同步默认（GUI 主导，CLI 跟随）
- 双向同步需用户配置（避免反馈循环）
- 同一 session 内多窗口的位置同步通过 IPC 广播

### 7. 子进程生命周期

- 子进程崩溃：父进程收到 IPC 断开，清理资源
- 父进程退出：子进程通过 IPC 检测断开，自行退出（5 秒超时）
- 用户主动关闭子进程窗口：发 `IpcShutdown` 给父进程

### 8. 协议版本兼容

- `IpcHandshake.ProtocolVersion` 必须匹配
- 不匹配时子进程拒绝连接，提示用户升级
- 字段新增向后兼容（旧版本忽略未知字段）
- 字段删除需提升主版本号

### 9. 跨平台注意事项

- Windows 命名管道支持多客户端
- Unix Domain Socket 单服务器，多客户端通过 `accept` 循环
- 路径中的特殊字符（空格、Unicode）在 IPC JSON 中正确转义

### 10. Out-GridView 子流程

```
CLI: out-gridview 命令
    ↓
启动 GUI 子进程（带 session ID）
    ↓
IPC 握手
    ↓
发送 IpcShowGridRequest（含 Items + ViewSpec）
    ↓
GUI 弹窗显示 DataGrid
    ↓
用户选中 / 取消
    ↓
GUI 发送 IpcShowGridResponse
    ↓
CLI 收到响应，作为命令输出
    ↓
关闭 GUI 子进程
```

## Alternatives Considered

1. **同进程双 host**：被否决，Avalonia 与 Console 不能同进程
2. **HTTP IPC**：被否决，端口冲突风险，本地 IPC 杀鸡用牛刀
3. **gRPC**：被否决，依赖较重，schema 维护成本
4. **stdin/stdout JSON 行**：被否决，无法双向（CLI 子进程 stdout 给用户）
5. **共享内存 + 事件**：被否决，跨平台实现复杂，无序列化层
6. **不实现互转，CLI 与 GUI 完全独立**：被否决，体验割裂，与 PowerShell Out-GridView 等核心场景不符

## Consequences

### 优势
- GUI 与 CLI 可互相调用
- 状态实时同步
- IPC 协议稳定可扩展
- 跨平台

### 代价
- 子进程启动延迟（< 500ms，可接受）
- IPC 协议版本管理
- JSON 序列化开销（大对象流时显式，但 Out-GridView 一次性数据集不大）
- 多窗口状态同步有反馈循环风险

### 约束
- IPC 端点路径必须含 `sessionId`，避免多用户冲突
- `IpcMessage` 必须是 `sealed record`，禁止继承
- 协议版本不匹配必须 fail-fast，禁止"尽力而为"
- 子进程启动失败必须报错，不静默回退
- IPC 通道断开后必须 5 秒内检测到，清理资源
- `IpcShowGridRequest.Items` 必须全部序列化（不允许流式），单次请求上限 10000 项
- 用户配置可关闭互转（`config.toml` 的 `[ipc] enabled = false`），关闭后 `out-gridview` 报错
- 同步方向必须可配置，禁止双向默认开启（避免循环）
- 子进程退出码 0 = 正常关闭，非 0 = 异常，父进程需记录
- IPC JSON 序列化必须配置 `ReferenceHandler.IgnoreCycles`，避免 IItem 循环引用
