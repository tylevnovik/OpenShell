using OpenShell.Errors;

namespace OpenShell.Startup;

/// <summary>
/// 加载并执行 OpenShell 启动 profile 脚本。Per ADR-0041.
/// 三层加载机制：用户全局 → 项目级 → 命令行覆盖。
/// </summary>
public interface IProfileLoader
{
    /// <summary>
    /// 加载并执行所有 profile 脚本（用户全局 + 项目级，或 <c>--profile</c> 指定的单一文件）。
    /// 每行通过 <paramref name="lineExecutor" /> 委托送入 host 的命令调度管线（<c>DispatchAsync</c>），
    /// 不引入对具体 host 的依赖。
    /// </summary>
    /// <param name="lineExecutor">逐行执行委托，由调用方提供（通常是 <c>CliHost.DispatchAsync</c> 的包装）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>执行结果，含已执行文件列表、错误列表与已执行行数。</returns>
    Task<ProfileExecutionResult> ExecuteAsync(Func<string, Task> lineExecutor, CancellationToken ct = default);

    /// <summary>跳过 profile 加载（<c>--noprofile</c> 命令行参数）。命中时跳过所有 profile 文件。</summary>
    bool SkipProfile { get; set; }

    /// <summary>自定义 profile 路径（<c>--profile &lt;path&gt;</c> 参数）。指定后仅加载该文件，跳过默认查找。</summary>
    string? CustomProfilePath { get; set; }
}

/// <summary>
/// Profile 执行结果。Per ADR-0041 §4.
/// profile 执行期间产生的 <see cref="ErrorRecord" /> 全部通过 <see cref="IErrorStream" /> 流出，
/// 此处汇总仅用于返回值，便于 host 显示汇总信息。
/// </summary>
public sealed record ProfileExecutionResult
{
    /// <summary>是否成功完成（未因致命错误中断）。文件缺失不算失败。</summary>
    public bool Success { get; init; }

    /// <summary>实际被执行（即文件存在且可读）的 profile 文件绝对路径列表。</summary>
    public IReadOnlyList<string> ExecutedFiles { get; init; } = Array.Empty<string>();

    /// <summary>执行期间产生的所有 <see cref="ErrorRecord" />（含致命与非致命）。</summary>
    public IReadOnlyList<ErrorRecord> Errors { get; init; } = Array.Empty<ErrorRecord>();

    /// <summary>已执行的逻辑行数（不含空行与注释行）。</summary>
    public int LinesExecuted { get; init; }
}
