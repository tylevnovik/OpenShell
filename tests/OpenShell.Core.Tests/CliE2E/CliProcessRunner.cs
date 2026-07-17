#nullable enable
// CLI 进程级 E2E 测试工具（CliProcessRunner）
// 参照 PowerShell 参考源的 ProcessStartInfo 模式（ConsoleHost.Tests.ps1）：
//   - 启动真实 openshell-cli.exe 进程
//   - 捕获 stdout / stderr / exit code
//   - 支持设置工作目录（TestDrive 等价物：TempDir）
//
// 用法：
//   var result = await CliProcessRunner.RunAsync(
//       new[] { "-Command", "cd ..; pwd" },
//       workingDir: tempDir.FullPath);
//   result.Stdout.Should().Contain(parentPath);
//   result.ExitCode.Should().Be(0);

using System.Diagnostics;
using OpenShell.TestUtils;

namespace OpenShell.Core.Tests.CliE2E;

/// <summary>
/// 启动真实 openshell-cli.exe 进程并捕获输出。Per T-301.
/// 等价 PowerShell 参考的 NewProcessStartInfo + RunPowerShell + EnsureChildHasExited 模式。
/// </summary>
public static class CliProcessRunner
{
    /// <summary>
    /// 启动 openshell-cli.exe 并等待退出。
    /// </summary>
    /// <param name="args">CLI 参数（如 ["-Command", "cd ..; pwd"] 或 ["-File", "script.osh"]）。</param>
    /// <param name="workingDir">工作目录（TestDrive 等价物，用 TempDir.FullPath）。</param>
    /// <param="timeoutMs">超时毫秒（默认 30s，等价 PS 参考的 15s 但给更宽裕）。</param>
    /// <returns>stdout / stderr / exit code。</returns>
    public static async Task<CliProcessResult> RunAsync(
        string[] args,
        string? workingDir = null,
        int timeoutMs = 30000)
    {
        var exePath = ResolveCliExePath();
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // --noprofile 避免 profile 副作用干扰测试（等价 pwsh -noprofile）。
            // -ExecutionPolicy Bypass 避免执行策略拦截（测试环境）。
        };

        // 始终加 --noprofile（等价 PS 参考的 pwsh -noprofile），避免 profile 副作用。
        psi.ArgumentList.Add("--noprofile");
        psi.ArgumentList.Add("--execution-policy");
        psi.ArgumentList.Add("Bypass");

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        if (workingDir is not null)
            psi.WorkingDirectory = workingDir;

        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringWriter();
        var stderrBuilder = new StringWriter();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stdoutBuilder.WriteLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderrBuilder.WriteLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 等价 PS 参考的 EnsureChildHasExited：超时后 Kill。
        var exited = process.WaitForExit(timeoutMs);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new CliProcessResult(
                Stdout: stdoutBuilder.ToString(),
                Stderr: stderrBuilder.ToString() + $"\n[PROCESS TIMED OUT after {timeoutMs}ms]",
                ExitCode: -1);
        }

        // 确保异步输出读取完成（ WaitForExit 不等待异步回调完成）。
        process.WaitForExit();

        return new CliProcessResult(
            Stdout: stdoutBuilder.ToString(),
            Stderr: stderrBuilder.ToString(),
            ExitCode: process.ExitCode);
    }

    /// <summary>便捷重载：执行 -Command 命令字符串。</summary>
    public static Task<CliProcessResult> RunCommandAsync(
        string command,
        string? workingDir = null,
        int timeoutMs = 30000)
        => RunAsync(new[] { "-Command", command }, workingDir, timeoutMs);

    /// <summary>便捷重载：执行 -File 脚本文件。</summary>
    public static Task<CliProcessResult> RunFileAsync(
        string scriptPath,
        string? workingDir = null,
        int timeoutMs = 30000)
        => RunAsync(new[] { "-File", scriptPath }, workingDir, timeoutMs);

    /// <summary>
    /// 定位 openshell-cli.exe。CLI 项目输出到 artifacts/bin/OpenShell/{Configuration}/openshell-cli.exe。
    /// 从测试程序集目录向上搜索 artifacts 目录。
    /// </summary>
    private static string ResolveCliExePath()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            // Debug / Release 两种配置都尝试。
            var candidate = Path.Combine(dir, "artifacts", "bin", "OpenShell");
            if (Directory.Exists(candidate))
            {
                var debugExe = Path.Combine(candidate, "Debug", "openshell-cli.exe");
                if (File.Exists(debugExe)) return debugExe;
                var releaseExe = Path.Combine(candidate, "Release", "openshell-cli.exe");
                if (File.Exists(releaseExe)) return releaseExe;
                // 目录存在但 exe 未找到，抛出明确错误。
                throw new FileNotFoundException(
                    $"artifacts/bin/OpenShell/ exists but openshell-cli.exe not found in Debug or Release. " +
                    $"Run 'dotnet build OpenShell.slnx' first.");
            }
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }

        // 退路：基于 csproj 位置推算。
        var fallback = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "artifacts", "bin", "OpenShell", "Debug", "openshell-cli.exe"));
        if (File.Exists(fallback)) return fallback;

        throw new FileNotFoundException(
            $"openshell-cli.exe not found. Searched from {AppContext.BaseDirectory}. " +
            $"Run 'dotnet build OpenShell.slnx' first.");
    }
}

/// <summary>CLI 进程执行结果。</summary>
public sealed record CliProcessResult(
    string Stdout,
    string Stderr,
    int ExitCode)
{
    /// <summary>stdout 是否为空（无输出）。</summary>
    public bool HasStdout => !string.IsNullOrWhiteSpace(Stdout);

    /// <summary>stderr 是否为空（无错误输出）。</summary>
    public bool HasStderr => !string.IsNullOrWhiteSpace(Stderr);

    /// <summary>退出码是否为 0（成功）。</summary>
    public bool Succeeded => ExitCode == 0;
}
