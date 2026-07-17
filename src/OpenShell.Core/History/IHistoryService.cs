using OpenShell.Paths;

namespace OpenShell.History;

/// <summary>
/// 命令历史服务。Per ADR-0020, ADR-0022 §6.
/// 记录每条执行的命令及其结果，支持检索与清除。
/// 默认持久化到 <c>~/.openshell/history.jsonl</c> (JSON Lines)。
/// </summary>
public interface IHistoryService
{
    /// <summary>最近 N 条历史记录 (默认全部已加载的, 受容量上限约束)。</summary>
    IReadOnlyList<HistoryEntry> Recent { get; }

    /// <summary>追加一条命令历史。实现负责持久化 (debounce flush)。</summary>
    /// <param name="command">执行的命令行 (原始输入)。</param>
    /// <param name="success">是否成功 (exit code == 0)。</param>
    /// <param name="exitCode">进程退出码。</param>
    void Add(string command, bool success, int exitCode);

    /// <summary>清除全部历史 (含持久化文件)。</summary>
    void Clear();

    /// <summary>按关键字搜索历史 (大小写不敏感, 命令行子串匹配)。</summary>
    /// <param name="query">搜索关键字。</param>
    /// <returns>匹配的历史记录, 按时间倒序 (最近在前)。</returns>
    IReadOnlyList<HistoryEntry> Search(string query);
}

/// <summary>
/// 单条命令历史记录。Per ADR-0020, ADR-0022 §6.
/// </summary>
public sealed record HistoryEntry
{
    /// <summary>记录唯一标识。</summary>
    public required Guid Id { get; init; }

    /// <summary>命令执行时间 (UTC)。</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>原始命令行文本。</summary>
    public required string Command { get; init; }

    /// <summary>是否成功。</summary>
    public required bool Success { get; init; }

    /// <summary>退出码。</summary>
    public required int ExitCode { get; init; }

    /// <summary>执行时的工作目录 (provider-namespaced)。</summary>
    public required ItemPath WorkingDirectory { get; init; }
}
