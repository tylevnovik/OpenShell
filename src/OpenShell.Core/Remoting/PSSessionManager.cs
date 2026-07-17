#nullable enable
// ADR-0059 §3: PSSession 生命周期管理器。
// 设计：
//   1. 分配递增 Id，维护 ConcurrentDictionary<int, IPSSession> 会话表。
//   2. Create 启动 SshPSSession，Remove 触发 DisposeAsync。
//   3. 单例 (DI 注册)，跨命令共享会话。

using System.Collections.Concurrent;

namespace OpenShell.Remoting;

/// <summary>ADR-0059 §3: PSSession 管理器 (单例)。</summary>
public sealed class PSSessionManager
{
    private readonly ConcurrentDictionary<int, IPSSession> _sessions = new();
    private int _nextId;

    /// <summary>ADR-0059 §6: 当前交互式会话 Id (Enter-PSSession 设置, Exit-PSSession 清除)。null 表示本地 REPL。</summary>
    public int? ActiveSessionId { get; set; }

    /// <summary>创建新 SSH 会话。Per ADR-0059 §3/§6.</summary>
    public IPSSession Create(PSSessionOptions options)
    {
        var id = Interlocked.Increment(ref _nextId);
        var session = new SshPSSession(id, options);
        _sessions[id] = session;
        return session;
    }

    /// <summary>按 Id 获取会话。未找到返回 null。</summary>
    public IPSSession? Get(int id) =>
        _sessions.TryGetValue(id, out var session) ? session : null;

    /// <summary>列出所有活跃会话。</summary>
    public IReadOnlyList<IPSSession> GetAll() =>
        _sessions.Values.ToList();

    /// <summary>关闭并移除会话。Per ADR-0059 §3.</summary>
    public async void Remove(int id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
