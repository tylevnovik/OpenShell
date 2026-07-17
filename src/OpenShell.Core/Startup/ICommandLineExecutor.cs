namespace OpenShell.Startup;

/// <summary>
/// 命令行调度抽象。Per ADR-0041.
/// 将"把一行命令送入 host 调度管线"的能力抽象为接口，
/// 使 <see cref="ProfileLoader" /> 与 <see cref="Commands.Builtins.ReloadProfileCommand" />
/// 无需直接依赖 <c>CliHost</c>（后者位于独立的 OpenShell.Cli.Host 程序集）。
/// </summary>
public interface ICommandLineExecutor
{
    /// <summary>
    /// 将一行命令送入 host 的命令调度管线。
    /// 实现方负责参数解析、别名展开、命令查找与执行。
    /// 执行期间产生的错误通过 <see cref="Errors.IErrorStream" /> 流出，不抛出非取消型异常。
    /// </summary>
    /// <param name="line">单行命令（已去除行尾续行符、已 trim）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ExecuteAsync(string line, CancellationToken cancellationToken = default);
}
