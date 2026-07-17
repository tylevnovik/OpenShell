using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Elevate</c> command. Per ADR-0036 §7.
/// 提权执行命令: 通过 UAC (Windows) / pkexec / sudo (Linux) / osascript (macOS) 启动新进程执行。
/// </summary>
/// <remarks>
/// 平台实现:
/// <list type="bullet">
///   <item>Windows: <c>runas</c> verb 启动 openshell-cli (UseShellExecute=true, 无法重定向输出, 输出在新的提权窗口中)。</item>
///   <item>Linux: 优先 <c>pkexec</c> (GUI 鉴权, 可重定向输出流); 不可用时回退 <c>sudo</c> (tty 鉴权)。</item>
///   <item>macOS: <c>osascript</c> 调用 <c>do shell script ... with administrator privileges</c>。</item>
/// </list>
/// </remarks>
[Verb("Invoke", Noun = "Elevate", Aliases = ["elevate", "sudo"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Medium)]
[Description("Elevates privileges to run a command (UAC on Windows, pkexec/sudo on Linux, osascript on macOS).")]
[Help(
    Synopsis = "Elevates privileges to run a command (UAC on Windows, sudo on Unix).",
    Examples = new[]
    {
        "elevate remove-item fs::C:/Windows/old",
        "sudo set-content fs::C:/Windows/System32/drivers/etc/hosts \"...\"",
    },
    RelatedLinks = new[] { "get-audit", "set-config" })]
public sealed class ElevateCommand : ICommand<ElevateCommand.Args>
{
    /// <summary>Arguments for <c>Elevate</c>.</summary>
    /// <param name="Command">要提权执行的命令行 (含参数)。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Command);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var command = args.Command?.Trim();
        if (string.IsNullOrEmpty(command))
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "elevate requires a command to run.",
                Operation = "elevate",
                Phase = ErrorPhase.Parse,
            });
            yield break;
        }

        // SupportsShouldProcess 确认 (Medium impact: 提权生成子进程)。
        if (!ctx.ShouldProcess(command, "Elevate and execute", ConfirmImpact.Medium))
            yield break;

        var launcher = Environment.ProcessPath ?? "openshell-cli";
        int exitCode;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                exitCode = await ElevateWindowsAsync(ctx, launcher, command, ct).ConfigureAwait(false);
            }
            else if (OperatingSystem.IsMacOS())
            {
                exitCode = await ElevateMacOsAsync(ctx, launcher, command, ct).ConfigureAwait(false);
            }
            else
            {
                // Linux / 其他 Unix: 优先 pkexec, 回退 sudo。
                exitCode = await ElevateLinuxAsync(ctx, launcher, command, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
        catch (Win32Exception ex)
        {
            // ERROR_CANCELLED (1223): 用户取消了 UAC / 鉴权对话框, 不视为致命错误。
            if (ex.NativeErrorCode == 1223)
            {
                await ctx.Host.WriteOutputLineAsync("Elevation cancelled by user.", ct).ConfigureAwait(false);
            }
            else
            {
                ctx.Errors?.Write(new ErrorRecord
                {
                    Category = ErrorCategory.PermissionDenied,
                    Message = $"Elevation failed: {ex.Message} (Win32 error {ex.NativeErrorCode}).",
                    Operation = "elevate",
                    Phase = ErrorPhase.Operation,
                    Exception = ex,
                });
            }
            yield break;
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(ErrorRecord.FromException(ex, operation: "elevate", phase: ErrorPhase.Operation));
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync($"Elevated process exited with code {exitCode}.", ct).ConfigureAwait(false);

        yield break;
    }

    /// <summary>
    /// Windows: <c>runas</c> verb 启动 openshell-cli。UseShellExecute=true 触发 UAC, 但无法重定向 stdout/stderr
    /// (Win32 限制); 输出在新的提权控制台窗口中。等待退出并返回退出码。
    /// </summary>
    private static async Task<int> ElevateWindowsAsync(
        CommandContext ctx, string launcher, string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = launcher,
            Arguments = command,
            Verb = "runas",
            UseShellExecute = true,
        };

        await ctx.Host.WriteOutputLineAsync(
            "Requesting elevation (UAC). Output will appear in the elevated window.", ct).ConfigureAwait(false);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for elevated process.");
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return proc.ExitCode;
    }

    /// <summary>
    /// Linux: 优先 <c>pkexec openshell-cli &lt;command&gt;</c> (polkit GUI 鉴权, 可重定向输出流);
    /// pkexec 不存在时回退 <c>sudo openshell-cli &lt;command&gt;</c> (tty 鉴权, 不重定向以便显示密码提示)。
    /// </summary>
    private static async Task<int> ElevateLinuxAsync(
        CommandContext ctx, string launcher, string command, CancellationToken ct)
    {
        if (IsExecutableInPath("pkexec"))
        {
            return await RunRedirectedAsync(ctx, "pkexec", launcher, command, ct).ConfigureAwait(false);
        }

        await ctx.Host.WriteOutputLineAsync(
            "pkexec not found; falling back to sudo (password prompt will appear on the terminal).",
            ct).ConfigureAwait(false);
        return await RunSudoAsync(ctx, launcher, command, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// macOS: <c>osascript -e 'do shell script "openshell-cli &lt;command&gt;" with administrator privileges'</c>
    /// (Security.framework GUI 鉴权, 可重定向输出流)。
    /// </summary>
    private static async Task<int> ElevateMacOsAsync(
        CommandContext ctx, string launcher, string command, CancellationToken ct)
    {
        var script = $"do shell script \"{EscapeForAppleScript(launcher)} {EscapeForAppleScript(command)}\" with administrator privileges";
        return await RunRedirectedAsync(ctx, "osascript", "-e", script, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 启动子进程 (UseShellExecute=false, 重定向 stdout/stderr) 并流式输出到宿主。
    /// <paramref name="exe"/> 为可执行文件名, 其余参数作为 argv 传入。
    /// </summary>
    private static async Task<int> RunRedirectedAsync(
        CommandContext ctx, string exe, string arg1, string arg2, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(arg1);
        psi.ArgumentList.Add(arg2);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = false };
        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start elevated process '{exe}'.");

        // 并发读取 stdout / stderr, 流式写入宿主输出。
        var stdoutTask = StreamToHostAsync(proc.StandardOutput, ctx, isStderr: false, ct);
        var stderrTask = StreamToHostAsync(proc.StandardError, ctx, isStderr: true, ct);

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return proc.ExitCode;
    }

    /// <summary>
    /// sudo 回退路径: 不重定向输出 (保留 tty 供密码提示), 等待退出并返回退出码。
    /// </summary>
    private static async Task<int> RunSudoAsync(
        CommandContext ctx, string launcher, string command, CancellationToken ct)
    {
        // 通过 sh -c 传递命令, 以支持 shell 语法 (管道 / 引号)。
        var psi = new ProcessStartInfo
        {
            FileName = "sudo",
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"{launcher} {command}");

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            throw new InvalidOperationException("Failed to start sudo process.");

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        await ctx.Host.WriteOutputLineAsync($"sudo process exited with code {proc.ExitCode}.", ct).ConfigureAwait(false);
        return proc.ExitCode;
    }

    /// <summary>逐行读取子进程输出流并写入宿主 (stdout → WriteOutputLineAsync, stderr → 错误流)。</summary>
    private static async Task StreamToHostAsync(
        StreamReader reader, CommandContext ctx, bool isStderr, CancellationToken ct)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (line is null) return;

            if (isStderr)
            {
                ctx.Errors?.Write(new ErrorRecord
                {
                    Category = ErrorCategory.OperationFailed,
                    Message = line,
                    Operation = "elevate",
                    Phase = ErrorPhase.Operation,
                });
            }
            else
            {
                await ctx.Host.WriteOutputLineAsync(line, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>检查给定可执行文件名是否在 PATH 中可解析。</summary>
    private static bool IsExecutableInPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return false;
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return true;
            }
            catch
            {
                // 非法路径片段忽略。
            }
        }
        return false;
    }

    /// <summary>转义 AppleScript 双引号字符串内的反斜杠与双引号。</summary>
    private static string EscapeForAppleScript(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
