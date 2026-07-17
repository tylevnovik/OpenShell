namespace OpenShell.Security;

/// <summary>
/// Provider 沙箱异步上下文。Per ADR-0036 §6 / §11 / §12.
/// <see cref="OpenShell.Plugins.PluginLoader"/> 进入第三方 Provider 代码时通过 <see cref="EnterScope"/> 设置当前沙箱;
/// <see cref="SandboxAwareDelegatingHandler"/> (§11) 与 <see cref="ProcessSpawnGuard"/> (§12) 读取
/// <see cref="Current"/> 进行网络/进程生成的强制检查。基于 <see cref="AsyncLocal{T}"/>, 异步流自动传递。
/// </summary>
/// <remarks>
/// 内置 Provider 不经过 PluginLoader 加载, <see cref="Current"/> 保持 null (无沙箱限制, 完全信任)。
/// 第三方 Provider 加载时设置非 null 沙箱, 触发 NetworkAccess / ProcessSpawn 强制策略。
/// </remarks>
public static class SandboxContext
{
    private static readonly AsyncLocal<ProviderSandbox?> _current = new();

    /// <summary>
    /// 当前异步流的 Provider 沙箱; null 表示在内置 Provider / 用户命令上下文中 (无沙箱限制)。
    /// </summary>
    public static ProviderSandbox? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <summary>
    /// 进入沙箱作用域: 设置 <see cref="Current"/> 为 <paramref name="sandbox"/>, 返回 <see cref="IDisposable"/>;
    /// dispose 时恢复之前的值。典型用法: <c>using var scope = SandboxContext.EnterScope(sandbox);</c>
    /// </summary>
    /// <param name="sandbox">要设置的沙箱; null 表示清除当前沙箱 (恢复到内置上下文)。</param>
    public static IDisposable EnterScope(ProviderSandbox? sandbox) => new SandboxScope(sandbox);

    private sealed class SandboxScope : IDisposable
    {
        private readonly ProviderSandbox? _previous;
        private bool _disposed;

        public SandboxScope(ProviderSandbox? sandbox)
        {
            _previous = _current.Value;
            _current.Value = sandbox;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = _previous;
        }
    }
}
