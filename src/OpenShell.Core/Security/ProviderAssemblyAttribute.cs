namespace OpenShell.Security;

/// <summary>
/// Provider 程序集元数据声明。Per ADR-0036 §6.
/// 第三方 Provider 主程序集通过 <c>[assembly: ProviderAssembly("name", "1.0.0", Sandbox = SandboxLevel.ReadOnly)]</c> 声明。
/// 加载时校验声明, 缺失则拒绝加载 (ADR-0036 §15 约束)。
/// </summary>
/// <example>
/// <code>
/// [assembly: ProviderAssembly("my-provider", "1.0.0", Sandbox = SandboxLevel.ReadOnly)]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ProviderAssemblyAttribute : Attribute
{
    public string Name { get; }
    public string Version { get; }

    /// <summary>沙箱级别。默认 <see cref="Security.SandboxLevel.Full"/> (信任内置 Provider)。</summary>
    public SandboxLevel Sandbox { get; init; } = SandboxLevel.Full;

    public ProviderAssemblyAttribute(string name, string version)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Version = version ?? throw new ArgumentNullException(nameof(version));
    }
}
