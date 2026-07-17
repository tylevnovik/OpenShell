using System.Text.Json.Serialization;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Events;

// ============================================================================
// Item 事件 (Provider 抛出)。Per ADR-0040 §2.1.
// 全部实现 IRemoteRoutableEvent: 文件系统变更需要跨进程同步刷新 CLI/GUI 视图。
// ============================================================================

/// <summary>Item 创建事件。Provider 在新建文件/目录后发布。</summary>
public sealed record ItemCreatedEvent : IRemoteRoutableEvent
{
    public required ItemPath Path { get; init; }
    public required IItem Item { get; init; }
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TargetSession { get; init; }
    public string? OriginHostId { get; init; }
}

/// <summary>Item 删除事件。Provider 在删除文件/目录后发布。</summary>
public sealed record ItemDeletedEvent : IRemoteRoutableEvent
{
    public required ItemPath Path { get; init; }
    public required IItem Item { get; init; }
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TargetSession { get; init; }
    public string? OriginHostId { get; init; }
}

/// <summary>Item 重命名事件。OldPath → NewPath, Item 为重命名后的实例。</summary>
public sealed record ItemRenamedEvent : IRemoteRoutableEvent
{
    public required ItemPath OldPath { get; init; }
    public required ItemPath NewPath { get; init; }
    public required IItem Item { get; init; }
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TargetSession { get; init; }
    public string? OriginHostId { get; init; }
}

/// <summary>Item 内容/属性修改事件。Provider 检测到外部进程修改后发布。</summary>
public sealed record ItemModifiedEvent : IRemoteRoutableEvent
{
    public required ItemPath Path { get; init; }
    public required IItem Item { get; init; }
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TargetSession { get; init; }
    public string? OriginHostId { get; init; }
}

/// <summary>Item 复制事件。Copy 操作成功后发布。</summary>
public sealed record ItemCopiedEvent : IRemoteRoutableEvent
{
    public required ItemPath SourcePath { get; init; }
    public required ItemPath DestinationPath { get; init; }
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TargetSession { get; init; }
    public string? OriginHostId { get; init; }
}

/// <summary>Item 移动事件。Move 操作成功后发布。</summary>
public sealed record ItemMovedEvent : IRemoteRoutableEvent
{
    public required ItemPath SourcePath { get; init; }
    public required ItemPath DestinationPath { get; init; }
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TargetSession { get; init; }
    public string? OriginHostId { get; init; }
}

// ============================================================================
// Operation 事件 (Operation Engine 抛出)。Per ADR-0040 §2.2 + ADR-0007.
// 全部实现 IRemoteRoutableEvent: 长时操作进度需要跨进程同步 (GUI 显示进度条 / CLI 提示)。
// TaskId 与 ITaskHandle.TaskId 对齐 (ADR-0044)。
// ============================================================================

/// <summary>操作开始事件。Operation Engine 在创建任务后发布。</summary>
public sealed record OperationStartedEvent : IRemoteRoutableEvent
{
    /// <summary>操作类型名 (例如 "copy", "move", "delete")。</summary>
    public required string Operation { get; init; }

    public required IReadOnlyList<ItemPath> Sources { get; init; }
    public required IReadOnlyList<ItemPath> Destinations { get; init; }

    /// <summary>任务 ID, 与 ITaskHandle.TaskId 对齐。Per ADR-0044.</summary>
    public required Guid TaskId { get; init; }

    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TargetSession { get; init; }
    public string? OriginHostId { get; init; }
}

/// <summary>操作进度事件。长时操作 (Copy 大目录) 周期性发布。</summary>
public sealed record OperationProgressEvent : IRemoteRoutableEvent
{
    public required Guid TaskId { get; init; }

    /// <summary>进度 0.0 ~ 1.0。</summary>
    public required double Progress { get; init; }

    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TargetSession { get; init; }
    public string? OriginHostId { get; init; }
}

/// <summary>操作完成事件。无论成功/失败都发布, Success 表示结果。</summary>
public sealed record OperationCompletedEvent : IRemoteRoutableEvent
{
    public required Guid TaskId { get; init; }
    public required string Operation { get; init; }
    public required bool Success { get; init; }

    /// <summary>失败时的异常。跨进程传播时不序列化 (Exception 不可 JSON 序列化)。</summary>
    [JsonIgnore]
    public Exception? Exception { get; init; }

    /// <summary>目标路径 (Per ADR-0044 §7)。可空: 部分操作无明确目标。</summary>
    public string? TargetPath { get; init; }

    /// <summary>操作耗时 (Per ADR-0044 §7)。可空: 未记录开始时间时省略。</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>已处理字节数 (Per ADR-0044 §7)。0 表示不适用或未推进。</summary>
    public long BytesProcessed { get; init; }

    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TargetSession { get; init; }
    public string? OriginHostId { get; init; }
}

/// <summary>操作失败事件。操作抛异常时单独发布, 携带异常详情。</summary>
public sealed record OperationFailedEvent : IRemoteRoutableEvent
{
    public required Guid TaskId { get; init; }
    public required string Operation { get; init; }

    [JsonIgnore]
    public Exception? Exception { get; init; }

    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TargetSession { get; init; }
    public string? OriginHostId { get; init; }
}

/// <summary>操作取消事件。用户取消或 CancellationToken 触发时发布。</summary>
public sealed record OperationCancelledEvent : IRemoteRoutableEvent
{
    public required Guid TaskId { get; init; }
    public required string Operation { get; init; }

    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TargetSession { get; init; }
    public string? OriginHostId { get; init; }
}

// ============================================================================
// System 事件。Per ADR-0040 §2.3.
// 部分实现 IRemoteRoutableEvent (LocationChanged / SelectionChanged / ConfigChanged):
//   这些是 GUI ↔ CLI 同步状态的关键事件。
// 其余 (Session* / ErrorOccurred / ProfileLoaded) 仅本地传播, 不跨进程。
// ============================================================================

/// <summary>位置变更事件。CLI cd / GUI tab 切换时发布, 跨进程同步当前路径。</summary>
public sealed record LocationChangedEvent : IRemoteRoutableEvent
{
    public required ItemPath OldLocation { get; init; }
    public required ItemPath NewLocation { get; init; }

    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TargetSession { get; init; }
    public string? OriginHostId { get; init; }
}

/// <summary>选中项变更事件。GUI ListBox 选中 / CLI pipeline 输出时发布。</summary>
public sealed record SelectionChangedEvent : IRemoteRoutableEvent
{
    public required IReadOnlyList<IItem> Selected { get; init; }

    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TargetSession { get; init; }
    public string? OriginHostId { get; init; }
}

/// <summary>会话启动事件。Host 启动时发布, 仅本地传播。</summary>
public sealed record SessionStartedEvent : IEvent
{
    public required HostKind HostKind { get; init; }
    public required Guid SessionId { get; init; }

    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>会话结束事件。Host 退出时发布, 仅本地传播。</summary>
public sealed record SessionEndedEvent : IEvent
{
    public required Guid SessionId { get; init; }

    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>错误发生事件。命令执行失败时发布, 携带结构化 ErrorRecord。仅本地传播。</summary>
public sealed record ErrorOccurredEvent : IEvent
{
    public required ErrorRecord ErrorRecord { get; init; }

    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>配置变更事件。Set-Config / 主题切换时发布, 跨进程同步。</summary>
public sealed record ConfigChangedEvent : IRemoteRoutableEvent
{
    public required string Key { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }

    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TargetSession { get; init; }
    public string? OriginHostId { get; init; }
}

/// <summary>Profile 加载完成事件。Per ADR-0041. 仅本地传播。</summary>
public sealed record ProfileLoadedEvent : IEvent
{
    public required string ProfilePath { get; init; }
    public required bool Success { get; init; }

    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
