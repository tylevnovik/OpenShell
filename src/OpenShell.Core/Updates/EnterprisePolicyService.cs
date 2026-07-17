using Tomlyn;
using Tomlyn.Model;

namespace OpenShell.Updates;

/// <summary>
/// 默认 <see cref="IEnterprisePolicyService"/> 实现。Per ADR-0037 §12.
/// 单例: 构造时一次性读取策略文件, 文件不存在视为全部默认值 (UpdatesEnabled=true, TargetVersion=null)。
/// 策略路径:
/// <list type="bullet">
///   <item>Windows: <c>%ProgramData%\OpenShell\policy.toml</c></item>
///   <item>Linux/macOS: <c>/etc/openshell/policy.toml</c></item>
/// </list>
/// </summary>
public sealed class EnterprisePolicyService : IEnterprisePolicyService
{
    private const string WindowsPolicyRelativePath = "OpenShell\\policy.toml";
    private const string UnixPolicyPath = "/etc/openshell/policy.toml";

    /// <inheritdoc />
    public bool IsPolicyFilePresent { get; }

    /// <inheritdoc />
    public bool UpdatesEnabled { get; }

    /// <inheritdoc />
    public string? TargetVersion { get; }

    /// <summary>构造 EnterprisePolicyService 并立即读取策略文件。</summary>
    public EnterprisePolicyService() : this(ResolveDefaultPolicyPath()) { }

    /// <summary>构造时指定自定义策略文件路径 (测试可注入)。</summary>
    /// <param name="policyPath">策略文件绝对路径。不存在视为默认值。</param>
    public EnterprisePolicyService(string policyPath)
    {
        ArgumentNullException.ThrowIfNull(policyPath);
        PolicyPath = policyPath;

        // 默认值: 不存在文件时视为允许更新 + 不锁定版本。
        IsPolicyFilePresent = File.Exists(policyPath);
        UpdatesEnabled = true;
        TargetVersion = null;

        if (!IsPolicyFilePresent) return;

        try
        {
            var text = File.ReadAllText(policyPath);
            var root = Toml.ToModel(text, policyPath);
            if (root.TryGetValue("updates", out var updatesObj) && updatesObj is TomlTable updates)
            {
                if (updates.TryGetValue("enabled", out var en) && en is bool enabled)
                    UpdatesEnabled = enabled;
                if (updates.TryGetValue("targetVersion", out var tv) && tv is string tvs && !string.IsNullOrWhiteSpace(tvs))
                    TargetVersion = tvs;
            }
        }
        catch
        {
            // 策略文件解析失败: 降级到默认值 (允许更新), 不抛异常以避免阻塞主程序启动。
            // 与 ADR-0022 配置加载容错一致: 策略文件应被视为 advisory。
        }
    }

    /// <summary>策略文件绝对路径 (实际加载的, 可能不存在)。</summary>
    public string PolicyPath { get; }

    /// <summary>解析当前平台的默认策略文件路径。</summary>
    public static string ResolveDefaultPolicyPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrEmpty(programData)) programData = @"C:\ProgramData";
            return Path.Combine(programData, WindowsPolicyRelativePath);
        }
        return UnixPolicyPath;
    }
}
