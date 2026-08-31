namespace OpenShell.Security;

/// <summary>
/// IH-012: macOS Keychain 秘密存储。通过系统 <c>/usr/bin/security</c> 工具读写
/// generic-password 条目 (账户名固定 <c>openshell</c>, 服务名 <c>openshell:&lt;key&gt;</c>)。
/// 秘密不落盘、不以文件形式存在; 进程仅通过命令行接口访问钥匙串。
/// </summary>
/// <remarks>
/// 设计:
/// <list type="bullet">
///   <item>构造函数接受可注入的进程运行器, 便于在非 macOS 环境做行为测试。</item>
///   <item>exit code 44 (errSecItemNotFound) 在 Get/Remove 时语义化为 "不存在"。</item>
///   <item>其余失败显式抛异常——持久化失败不得静默 (per IH-003 验收标准)。</item>
/// </list>
/// </remarks>
public sealed class KeychainSecretStore : ISecretStore
{
    /// <summary>errSecItemNotFound: security 工具对缺失条目的退出码。</summary>
    public const int ExitCodeItemNotFound = 44;

    /// <summary>钥匙串账户名 (所有 OpenShell 条目共享)。</summary>
    public const string AccountName = "openshell";

    /// <summary>服务名前缀, 避免与用户其他钥匙串条目冲突。</summary>
    public const string ServicePrefix = "openshell:";

    private readonly Func<string, IReadOnlyList<string>, ProcessResult> _runner;

    /// <summary>security 工具一次调用的结果。</summary>
    public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    public KeychainSecretStore(Func<string, IReadOnlyList<string>, ProcessResult>? runner = null)
    {
        _runner = runner ?? DefaultRunner;
    }

    /// <inheritdoc />
    public string? GetSecret(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var result = _runner("/usr/bin/security",
        [
            "find-generic-password", "-a", AccountName, "-s", ServicePrefix + key, "-w",
        ]);
        if (result.ExitCode == 0)
            return result.Stdout.TrimEnd('\r', '\n');
        if (result.ExitCode == ExitCodeItemNotFound)
            return null;
        throw new InvalidOperationException(
            $"Keychain lookup failed for '{key}' (exit {result.ExitCode}): {result.Stderr.Trim()}");
    }

    /// <inheritdoc />
    public void SetSecret(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        // -U: 存在则更新; -w: 密码值。注意值会短暂出现在进程参数列表中——
        // 这是 security CLI 的已知限制, 桌面单用户场景可接受 (见审计文档)。
        var result = _runner("/usr/bin/security",
        [
            "add-generic-password", "-U", "-a", AccountName, "-s", ServicePrefix + key, "-w", value,
        ]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Keychain write failed for '{key}' (exit {result.ExitCode}): {result.Stderr.Trim()}");
    }

    /// <inheritdoc />
    public void RemoveSecret(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var result = _runner("/usr/bin/security",
        [
            "delete-generic-password", "-a", AccountName, "-s", ServicePrefix + key,
        ]);
        // 幂等: 条目不存在等同删除成功。
        if (result.ExitCode != 0 && result.ExitCode != ExitCodeItemNotFound)
            throw new InvalidOperationException(
                $"Keychain delete failed for '{key}' (exit {result.ExitCode}): {result.Stderr.Trim()}");
    }

    private static ProcessResult DefaultRunner(string fileName, IReadOnlyList<string> arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return new ProcessResult(-1, "", $"Failed to start '{fileName}'.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", ex.Message);
        }
    }

    /// <summary>
    /// 探测当前会话的钥匙串是否可用。只读查询一个必然不存在的条目:
    /// 返回 44 (条目不存在) = 服务正常; 其他错误 (无默认钥匙串 / 锁定, 常见于 CI 会话) = 不可用。
    /// </summary>
    public static bool IsAvailable(Func<string, IReadOnlyList<string>, ProcessResult>? runner = null)
    {
        runner ??= DefaultRunner;
        var probe = runner("/usr/bin/security",
        [
            "find-generic-password", "-a", AccountName, "-s", ServicePrefix + "__probe__", "-w",
        ]);
        return probe.ExitCode == ExitCodeItemNotFound;
    }
}

/// <summary>
/// IH-012: 默认 <see cref="ISecretStore"/> 选择工厂。
/// <list type="bullet">
///   <item>macOS: Keychain (<see cref="KeychainSecretStore"/>); security 工具缺失时回退受保护文件。</item>
///   <item>Windows: <see cref="ProtectedFileSecretStore"/> (内部用当前用户 DPAPI)。</item>
///   <item>Linux: <see cref="ProtectedFileSecretStore"/> (0600 密钥 + AES-GCM; 原建议明确允许受保护文件)。</item>
/// </list>
/// 宿主可通过 <c>InMemoryCredentialProvider</c> (OpenShell.Providers.Remote) 构造函数注入任意实现替换。
/// </summary>
public static class SecretStoreFactory
{
    /// <summary>按当前操作系统选择默认秘密存储。</summary>
    public static ISecretStore CreateDefault(string filePath)
        => CreateDefault(
            filePath,
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS(),
            File.Exists("/usr/bin/security"),
            () => KeychainSecretStore.IsAvailable());

    /// <summary>可注入平台判定的重载 (测试用)。</summary>
    internal static ISecretStore CreateDefault(
        string filePath,
        bool isWindows,
        bool isMacOS,
        bool keychainToolAvailable,
        Func<bool> keychainProbe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        // macOS: 工具存在且钥匙串会话可用才用 Keychain (CI 会话常无默认钥匙串, 必须回退)。
        if (isMacOS && keychainToolAvailable && keychainProbe())
            return new KeychainSecretStore();
        // Windows (DPAPI) 与 Linux (0600 受保护文件) 都走 ProtectedFileSecretStore。
        _ = isWindows;
        return new ProtectedFileSecretStore(filePath);
    }
}
