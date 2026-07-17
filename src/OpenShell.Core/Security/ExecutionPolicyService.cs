using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenShell;
using OpenShell.Configuration;
using OpenShell.Packaging.Signing;
using OpenShell.Providers;

namespace OpenShell.Security;

/// <summary>
/// 默认 <see cref="IExecutionPolicyService"/> 实现。Per ADR-0054 §1-§10.
/// <para>
/// 策略优先级: Process (-ExecutionPolicy flag / $env:OPENSHELL_EXECUTION_POLICY) > User (config.toml) > Machine (注册表 / /etc)。
/// User scope 不能高于 Machine scope 的限制 (Machine 设 Restricted 时 User 设 Bypass 仍为 Restricted)。
/// </para>
/// <para>
/// 签名校验复用 ADR-0039 §8 的 <see cref="ISignatureVerifier"/> (Ed25519), 旁路文件 &lt;script&gt;.sig + &lt;script&gt;.pub。
/// </para>
/// </summary>
public sealed class ExecutionPolicyService : IExecutionPolicyService
{
    /// <summary>环境变量名: 进程级 ExecutionPolicy 覆盖。Per ADR-0054 §7.</summary>
    public const string ProcessEnvVar = "OPENSHELL_EXECUTION_POLICY";

    private const string PragmaPattern = @"^\s*#\s*ExecutionPolicy\s*:\s*(\w+)\s*$";
    private const int PragmaScanLines = 10;

    private static readonly Regex PragmaRegex = new(PragmaPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IConfigurationService? _config;
    private readonly ISignatureVerifier? _signatureVerifier;
    private readonly ILogger<ExecutionPolicyService>? _logger;

    public ExecutionPolicyService(
        IConfigurationService? config = null,
        ISignatureVerifier? signatureVerifier = null,
        ILogger<ExecutionPolicyService>? logger = null)
    {
        _config = config;
        _signatureVerifier = signatureVerifier;
        _logger = logger;
    }

    /// <inheritdoc />
    public ExecutionPolicy GetEffectivePolicy()
    {
        // 1. Process scope (CLI flag / 环境变量): 最高优先级。
        var processPolicy = ReadProcessPolicy();
        if (processPolicy is { } pp)
            return ApplyMachineFloor(pp);

        // 2. User scope (config.toml)。
        var userPolicy = ReadUserPolicy();
        if (userPolicy is { } up)
            return ApplyMachineFloor(up);

        // 3. Machine scope (注册表 / /etc/openshell/policy.toml)。
        var machinePolicy = ReadMachinePolicy();
        if (machinePolicy is { } mp)
            return mp;

        // 4. 默认值: RemoteSigned (per ADR-0054 §1)。
        return ExecutionPolicy.RemoteSigned;
    }

    /// <inheritdoc />
    public ExecutionPolicy? GetPolicy(ExecutionPolicyScope scope) => scope switch
    {
        ExecutionPolicyScope.Process => ReadProcessPolicy(),
        ExecutionPolicyScope.User => ReadUserPolicy(),
        ExecutionPolicyScope.Machine => ReadMachinePolicy(),
        _ => null,
    };

    /// <inheritdoc />
    public void SetPolicy(ExecutionPolicy policy, ExecutionPolicyScope scope)
    {
        switch (scope)
        {
            case ExecutionPolicyScope.Process:
                // Process scope: 写环境变量, 供子进程继承 + 当前进程读取。
                Environment.SetEnvironmentVariable(ProcessEnvVar, policy.ToString());
                break;
            case ExecutionPolicyScope.User:
                // User scope: 写 config.toml 的 executionPolicy 字段。
                if (_config is null)
                    throw new InvalidOperationException("Configuration service unavailable; cannot set User scope policy.");
                _config.Config.ExecutionPolicy = policy.ToString();
                break;
            case ExecutionPolicyScope.Machine:
                // Machine scope: 需管理员权限。Windows: HKLM\SOFTWARE\OpenShell; Unix: /etc/openshell/policy.toml。
                WriteMachinePolicy(policy);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown scope.");
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<ExecutionPolicyScope, ExecutionPolicy?> ListScopes()
        => new Dictionary<ExecutionPolicyScope, ExecutionPolicy?>
        {
            [ExecutionPolicyScope.Machine] = ReadMachinePolicy(),
            [ExecutionPolicyScope.User] = ReadUserPolicy(),
            [ExecutionPolicyScope.Process] = ReadProcessPolicy(),
        };

    /// <inheritdoc />
    public (bool canExecute, string reason) CanExecute(string filePath, bool isRemote)
    {
        var policy = GetEffectivePolicy();

        // 解析文件级 pragma (仅能收紧, 不能放宽)。Per ADR-0054 §8.
        var filePolicy = ReadPragma(filePath);
        if (filePolicy is { } fp && IsMoreRestrictive(fp, policy))
        {
            policy = fp;
        }

        return policy switch
        {
            ExecutionPolicy.Bypass => (true, "Bypass: no restrictions"),
            ExecutionPolicy.Unrestricted => isRemote
                ? (true, "Unrestricted: remote script execution allowed (confirmation prompt recommended)")
                : (true, "Unrestricted: local script execution allowed"),
            ExecutionPolicy.RemoteSigned => isRemote
                ? CheckRemoteSigned(filePath)
                : (true, "RemoteSigned: local script execution allowed"),
            ExecutionPolicy.Restricted => (false, "Restricted: script execution disabled (interactive REPL only)"),
            _ => (false, $"Unknown execution policy: {policy}"),
        };
    }

    /// <inheritdoc />
    public bool IsRemoteFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            return OperatingSystem.IsWindows()
                ? IsRemoteWindows(filePath)
                : IsRemoteUnix(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Failed to check remote status for '{Path}'; treating as non-remote (conservative).", filePath);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<SignatureResult> CheckSignatureAsync(string filePath, CancellationToken ct = default)
    {
        if (_signatureVerifier is null)
            return SignatureResult.Untrusted;
        if (!File.Exists(filePath))
            return SignatureResult.Untrusted;

        var sigPath = filePath + ".sig";
        var pubPath = filePath + ".pub";
        if (!File.Exists(sigPath) || !File.Exists(pubPath))
            return SignatureResult.Untrusted;

        byte[]? signature, publicKey;
        try
        {
            signature = await File.ReadAllBytesAsync(sigPath, ct).ConfigureAwait(false);
            publicKey = await File.ReadAllBytesAsync(pubPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Failed to read signature files for '{Path}'.", filePath);
            return SignatureResult.Invalid;
        }

        // 计算 payloadHash: 脚本内容 SHA256。Per ADR-0054 §4.
        byte[] payloadHash;
        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            payloadHash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Failed to compute payload hash for '{Path}'.", filePath);
            return SignatureResult.Invalid;
        }

        // 调用 ISignatureVerifier (脚本场景 manifest 传 null, 实现仅用 payloadHash)。
        // 注: ISignatureVerifier.VerifyAsync 的 manifest 参数允许 null (Ed25519 实现不依赖 manifest)。
        var manifest = new ProviderManifest { Name = Path.GetFileName(filePath) };
        return await _signatureVerifier.VerifyAsync(
            manifest,
            payloadHash,
            publicKey,
            signature,
            sourceIsTrusted: false,
            ct).ConfigureAwait(false);
    }

    // =========================================================================
    // 私有: 策略读取
    // =========================================================================

    private static ExecutionPolicy? ReadProcessPolicy()
    {
        var env = Environment.GetEnvironmentVariable(ProcessEnvVar);
        return ParsePolicy(env);
    }

    private ExecutionPolicy? ReadUserPolicy()
    {
        var cfgValue = _config?.Config.ExecutionPolicy;
        return ParsePolicy(cfgValue);
    }

    private ExecutionPolicy? ReadMachinePolicy()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ReadMachinePolicyWindows();
            }
            return ReadMachinePolicyUnix();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger?.LogDebug(ex, "Failed to read Machine scope policy; ignoring.");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static ExecutionPolicy? ReadMachinePolicyWindows()
    {
        // HKLM\SOFTWARE\OpenShell\ExecutionPolicy (字符串值)。
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OpenShell");
        if (key?.GetValue("ExecutionPolicy") is string s)
            return ParsePolicy(s);
        return null;
    }

    private static ExecutionPolicy? ReadMachinePolicyUnix()
    {
        // /etc/openshell/policy.toml 的 [security] executionPolicy = "..."。
        var path = "/etc/openshell/policy.toml";
        if (!File.Exists(path)) return null;
        var lines = File.ReadAllLines(path);
        bool inSecuritySection = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                inSecuritySection = trimmed.Equals("[security]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inSecuritySection && trimmed.StartsWith("executionPolicy", StringComparison.OrdinalIgnoreCase))
            {
                var eq = trimmed.IndexOf('=');
                if (eq > 0)
                {
                    var val = trimmed[(eq + 1)..].Trim().Trim('"', '\'');
                    return ParsePolicy(val);
                }
            }
        }
        return null;
    }

    private void WriteMachinePolicy(ExecutionPolicy policy)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                WriteMachinePolicyWindows(policy);
            }
            else
            {
                WriteMachinePolicyUnix(policy);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"Setting Machine scope ExecutionPolicy requires administrator/root privileges. {ex.Message}", ex);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteMachinePolicyWindows(ExecutionPolicy policy)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\OpenShell");
        key.SetValue("ExecutionPolicy", policy.ToString(), Microsoft.Win32.RegistryValueKind.String);
    }

    private static void WriteMachinePolicyUnix(ExecutionPolicy policy)
    {
        var path = "/etc/openshell/policy.toml";
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, $"[security]\nexecutionPolicy = \"{policy}\"\n");
    }

    // =========================================================================
    // 私有: 决策矩阵
    // =========================================================================

    private (bool, string) CheckRemoteSigned(string filePath)
    {
        // RemoteSigned + 远程脚本: 需 Ed25519 签名。Per ADR-0054 §5.
        if (_signatureVerifier is null)
            return (false, "RemoteSigned: remote script requires signature, but no ISignatureVerifier registered");

        var sigResult = CheckSignatureAsync(filePath).GetAwaiter().GetResult();
        return sigResult switch
        {
            SignatureResult.Valid => (true, "RemoteSigned: remote script signature valid"),
            SignatureResult.TrustedSource => (true, "RemoteSigned: remote script from trusted source"),
            SignatureResult.Invalid => (false, "RemoteSigned: remote script signature INVALID (content tampered or wrong key)"),
            SignatureResult.Untrusted => (false, "RemoteSigned: remote script lacks trusted signature (add .sig + .pub files)"),
            _ => (false, $"RemoteSigned: signature check returned {sigResult}"),
        };
    }

    // =========================================================================
    // 私有: 远程文件检测
    // =========================================================================

    private static bool IsRemoteWindows(string filePath)
    {
        // 读取 Zone.Identifier ADS, 解析 ZoneId。Per ADR-0054 §3.
        // ZoneId 0/1/2 = 本地; 3/4 = 远程。
        var adsPath = filePath + ":Zone.Identifier";
        if (!File.Exists(adsPath)) return false;
        var content = File.ReadAllText(adsPath);
        foreach (var line in content.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("ZoneId", StringComparison.OrdinalIgnoreCase))
            {
                var eq = trimmed.IndexOf('=');
                if (eq > 0 && int.TryParse(trimmed[(eq + 1)..].Trim(), out var zoneId))
                    return zoneId >= 3;
            }
        }
        return false;
    }

    private static bool IsRemoteUnix(string filePath)
    {
        // 调用 `xattr -p <attr> <file>` (macOS) 或 `getfattr -n <attr> <file>` (Linux)。
        // 简化: 调用 `xattr` (macOS) / `getfattr` (Linux), 失败视为非远程。
        // Per ADR-0054 §3: 失败时保守不视为远程 (但签名仍需校验)。
        var attrs = OperatingSystem.IsMacOS()
            ? ReadXattrMacOS(filePath)
            : ReadXattrLinux(filePath);

        if (attrs is null) return false;

        // macOS: com.apple.quarantine 存在即视为远程。
        // Linux: user.xdg.origin.url / user.openshell.remote 存在即视为远程。
        foreach (var attr in attrs)
        {
            if (attr.StartsWith("com.apple.quarantine", StringComparison.OrdinalIgnoreCase))
                return true;
            if (attr.StartsWith("user.xdg.origin.url", StringComparison.OrdinalIgnoreCase))
                return true;
            if (attr.StartsWith("user.openshell.remote", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static List<string>? ReadXattrMacOS(string filePath)
    {
        // macOS: `xattr <file>` 列出所有扩展属性名 (每行一个)。
        var (output, exitCode) = RunProcess("xattr", filePath);
        if (exitCode != 0) return null;
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static List<string>? ReadXattrLinux(string filePath)
    {
        // Linux: `getfattr -d --absolute-names <file>` 列出 user.* 属性。
        var (output, exitCode) = RunProcess("getfattr", $"-d --absolute-names \"{filePath}\"");
        if (exitCode != 0) return null;
        // getfattr 输出: `# file: path\nuser.attr="value"\n`。提取 user.* 行。
        var result = new List<string>();
        foreach (var line in output.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("user.", StringComparison.OrdinalIgnoreCase))
                result.Add(trimmed);
        }
        return result;
    }

    private static (string output, int exitCode) RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return ("", -1);
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(milliseconds: 2000);
            return (stdout, proc.ExitCode);
        }
        catch
        {
            return ("", -1);
        }
    }

    // =========================================================================
    // 私有: pragma 解析
    // =========================================================================

    private static ExecutionPolicy? ReadPragma(string filePath)
    {
        // 解析文件首 10 行的 `# ExecutionPolicy: <level>` pragma。Per ADR-0054 §8.
        if (!File.Exists(filePath)) return null;
        try
        {
            using var reader = new StreamReader(filePath);
            for (int i = 0; i < PragmaScanLines; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                var m = PragmaRegex.Match(line);
                if (m.Success && m.Groups[1].Success)
                    return ParsePolicy(m.Groups[1].Value);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读取失败: 忽略 pragma。
        }
        return null;
    }

    // =========================================================================
    // 私有: 辅助
    // =========================================================================

    private static ExecutionPolicy? ParsePolicy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Enum.TryParse<ExecutionPolicy>(value, ignoreCase: true, out var p) ? p : null;
    }

    /// <summary>
    /// 应用 Machine floor: User / Process scope 不能比 Machine scope 更宽松。
    /// Per ADR-0054 §2: Machine 设 Restricted 时 User 设 Bypass 仍为 Restricted。
    /// </summary>
    private ExecutionPolicy ApplyMachineFloor(ExecutionPolicy candidate)
    {
        var machine = ReadMachinePolicy();
        if (machine is not { } mp) return candidate;
        // Restricted=0, RemoteSigned=1, Unrestricted=2, Bypass=3 (按枚举顺序递增宽松度)。
        // 若 candidate 比 mp 更宽松 (数值更大), 用 mp 替换。
        if ((int)candidate > (int)mp)
            return mp;
        return candidate;
    }

    /// <summary>
    /// 判断 a 是否比 b 更严格 (用于 pragma: 仅能收紧, 不能放宽)。Per ADR-0054 §8.
    /// </summary>
    private static bool IsMoreRestrictive(ExecutionPolicy a, ExecutionPolicy b)
        => (int)a < (int)b;
}
