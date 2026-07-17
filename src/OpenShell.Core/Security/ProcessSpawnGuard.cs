namespace OpenShell.Security;

/// <summary>
/// 进程生成守卫。Per ADR-0036 §12.
/// 在 <c>Start-Process</c> / Provider 代码生成子进程前调用, 强制第三方 Provider 沙箱与 GUI 宿主配置策略。
/// </summary>
/// <remarks>
/// 策略 (保守):
/// <list type="bullet">
///   <item>当前在第三方 Provider 沙箱内 (<c>currentSandbox != null</c>) → 始终拒绝 (第三方代码不得生成进程)。
///     内置 Provider 不经过 PluginLoader 加载, 沙箱为 null, 不受此限制。</item>
///   <item>GUI 宿主且配置未显式允许 (<c>isGuiHost &amp;&amp; !configAllowProcessSpawnInGui</c>) → 拒绝。
///     CLI 宿主不受此限制。</item>
///   <item>其他情况 (CLI 宿主 + 内置上下文) → 允许。</item>
/// </list>
/// </remarks>
public static class ProcessSpawnGuard
{
    /// <summary>
    /// 检查当前是否允许生成进程, 不允许则抛出 <see cref="SecuritySandboxViolationException"/>。
    /// </summary>
    /// <param name="currentSandbox">当前异步流的 Provider 沙箱 (来自 <see cref="SandboxContext.Current"/>); null 表示内置/用户上下文。</param>
    /// <param name="isGuiHost">是否在 GUI 宿主中执行。</param>
    /// <param name="configAllowProcessSpawnInGui">配置项 <c>[security].allowProcessSpawnInGui</c> 的值。</param>
    public static void EnsureAllowed(ProviderSandbox? currentSandbox, bool isGuiHost, bool configAllowProcessSpawnInGui)
    {
        if (currentSandbox is not null)
        {
            throw new SecuritySandboxViolationException(
                "Process spawn denied: third-party provider sandbox prohibits spawning child processes. " +
                "Built-in providers (null sandbox) are allowed; third-party providers (non-null sandbox) are always denied for safety.");
        }

        if (isGuiHost && !configAllowProcessSpawnInGui)
        {
            throw new SecuritySandboxViolationException(
                "Process spawn denied in GUI host. Set [security].allowProcessSpawnInGui = true in config.toml " +
                "to allow start-process from the GUI host.");
        }
    }
}
