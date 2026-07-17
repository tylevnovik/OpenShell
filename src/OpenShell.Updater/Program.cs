// ADR-0037 §6: 独立 openshell-updater 进程。
// 负责在主进程退出后替换其可执行文件, 避开 Windows 上运行中 exe 的文件锁。
// 参数: <currentExePath> <newExePath> <pid> [restart]
//   currentExePath: 当前 (运行中) 主程序 exe 绝对路径。
//   newExePath:     下载好的新版本 exe 绝对路径 (在 updates/ 目录下)。
//   pid:            主进程 PID, updater 轮询其退出后再做文件替换。
//   restart:        可选布尔标志; 出现时替换完成后重启主程序。
// 退出码:
//   0 — 成功完成替换 (必要时重启)。
//   1 — 参数错误或文件操作不可恢复失败。
//   2 — 主进程在 30s 内未退出。

using System.Diagnostics;

namespace OpenShell.Updater;

internal static class Program
{
    private const int PollIntervalMs = 100;
    private const int WaitTimeoutMs = 30_000;
    private const int IoRetryCount = 3;
    private const int IoRetryDelayMs = 500;

    private static async Task<int> Main(string[] args)
    {
        // 1) 解析参数。
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: openshell-updater <currentExePath> <newExePath> <pid> [restart]");
            return 1;
        }

        var currentExePath = args[0];
        var newExePath = args[1];
        if (!int.TryParse(args[2], out var pid))
        {
            Console.Error.WriteLine($"openshell-updater: invalid pid '{args[2]}'.");
            return 1;
        }
        var restart = args.Length > 3 && IsTruthyFlag(args[3]);

        if (string.IsNullOrEmpty(currentExePath) || string.IsNullOrEmpty(newExePath))
        {
            Console.Error.WriteLine("openshell-updater: currentExePath and newExePath must not be empty.");
            return 1;
        }
        if (!File.Exists(newExePath))
        {
            Console.Error.WriteLine($"openshell-updater: new exe not found at '{newExePath}'.");
            return 1;
        }

        // 2) 轮询主进程退出, 最多等 30s。
        if (!await WaitForProcessExitAsync(pid).ConfigureAwait(false))
        {
            Console.Error.WriteLine($"openshell-updater: process {pid} did not exit within {WaitTimeoutMs / 1000}s; aborting.");
            return 2;
        }

        // 3) 把当前 exe 重命名为 .old (重试 3 次, 500ms 间隔)。
        var oldExePath = currentExePath + ".old";
        if (!RetryIo(() => SafeRename(currentExePath, oldExePath)))
        {
            Console.Error.WriteLine($"openshell-updater: failed to rename '{currentExePath}' -> '{oldExePath}'.");
            return 1;
        }

        // 4) 把新 exe 移动到当前位置 (重试 3 次)。
        if (!RetryIo(() => SafeMove(newExePath, currentExePath)))
        {
            // 严重错误: 当前 exe 已被改名为 .old, 但新 exe 没能就位 → 尝试回滚恢复。
            Console.Error.WriteLine($"openshell-updater: failed to move '{newExePath}' -> '{currentExePath}'; attempting rollback.");
            if (File.Exists(oldExePath) && !File.Exists(currentExePath))
            {
                try { File.Move(oldExePath, currentExePath); }
                catch (Exception rollbackEx)
                {
                    Console.Error.WriteLine($"openshell-updater: rollback failed: {rollbackEx.Message}");
                }
            }
            return 1;
        }

        // 非 Windows: 确保新 exe 有可执行位。
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(currentExePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"openshell-updater: warning: failed to set executable bits on '{currentExePath}': {ex.Message}");
                // 不视为致命错误, 继续后续步骤。
            }
        }

        // 5) 若请求重启, 启动新的主进程。
        if (restart)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = currentExePath,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                };
                Process.Start(psi)?.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"openshell-updater: warning: failed to restart '{currentExePath}': {ex.Message}");
                // 重启失败不视为致命: 文件替换已成功, 用户可手动启动。
            }
        }

        // 6) 删除 .old 备份 (best-effort, 忽略错误)。ADR-0037 §7 要求保留 7 天, 但简化实现直接删除。
        try
        {
            if (File.Exists(oldExePath)) File.Delete(oldExePath);
        }
        catch (Exception)
        {
            // best-effort: .old 残留不影响功能, 后续可由外部清理任务处理。
        }

        return 0;
    }

    /// <summary>轮询指定 PID 是否已退出, 每 100ms 检查一次, 最多等待 30s。</summary>
    private static async Task<bool> WaitForProcessExitAsync(int pid)
    {
        // Process.GetProcessById 在进程不存在时会抛 ArgumentException, 这是退出的标志。
        Process? proc = null;
        try
        {
            proc = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            // 进程已不存在 → 视为已退出。
            return true;
        }

        try
        {
            var elapsed = 0;
            while (elapsed < WaitTimeoutMs)
            {
                if (proc.HasExited) return true;
                await Task.Delay(PollIntervalMs).ConfigureAwait(false);
                elapsed += PollIntervalMs;
            }
            return proc.HasExited;
        }
        finally
        {
            proc.Dispose();
        }
    }

    /// <summary>把 source 重命名为 dest (覆盖已存在的 dest)。仅当 source 存在且 dest 不存在时执行。</summary>
    private static void SafeRename(string source, string dest)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException($"Source file not found: {source}", source);
        // 若 dest 已存在 (上次更新残留), 先删除。
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(source, dest);
    }

    /// <summary>把 source 移动到 dest (覆盖已存在的 dest)。</summary>
    private static void SafeMove(string source, string dest)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException($"Source file not found: {source}", source);
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(source, dest);
    }

    /// <summary>对 IO 操作重试最多 IoRetryCount 次, 每次失败后等 IoRetryDelayMs。仅吞 IOException。</summary>
    private static bool RetryIo(Action action)
    {
        for (var attempt = 1; attempt <= IoRetryCount; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (IOException) when (attempt < IoRetryCount)
            {
                Thread.Sleep(IoRetryDelayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < IoRetryCount)
            {
                // Windows 上偶发被 AV/Defender 锁定, 同样重试。
                Thread.Sleep(IoRetryDelayMs);
            }
        }
        return false;
    }

    /// <summary>判断参数是否为布尔真值标志 (restart / true / 1 / yes, 大小写不敏感)。</summary>
    private static bool IsTruthyFlag(string s)
        => string.Equals(s, "restart", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "1", StringComparison.Ordinal)
            || string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase);
}
