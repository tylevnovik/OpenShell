using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShell.Interop;
using OpenShell.Paths;

namespace OpenShell.Sessions;

/// <summary>
/// 基于 System.Text.Json 的 <see cref="ISessionService"/> 实现。Per ADR-0034 §2 / §4 / §8.
/// 会话 JSON 持久化到 <c>~/.opensshell/sessions/&lt;name&gt;.json</c>，锁文件到 <c>&lt;name&gt;.lock</c>，
/// 快照到 <c>~/.opensshell/snapshots/&lt;name&gt;.json</c>。
/// 文件损坏时降级到默认状态并记录 stderr error (不抛异常)。
/// </summary>
/// <remarks>
/// ADR-0034 已实现项:
/// <list type="bullet">
///   <item>§3: 30s 定期自动保存 → <see cref="SessionAutoSaveService"/> (IHostedService)。</item>
///   <item>§6: 远程路径安全校验 → <c>RemotePathValidator</c> (OpenShell.Core.Paths)。</item>
///   <item>§9: 跨机器同步 → <see cref="SessionSyncService"/> + <see cref="WebDavSessionSyncProvider"/>。</item>
///   <item>§11: GUI tab 持久化 → <c>SessionTabsService</c> (OpenShell.Gui.Host.Services)。</item>
///   <item>§12: CLI 子进程清理 → <see cref="ChildProcessTracker"/> (Job Object / SIGTERM)。</item>
/// </list>
/// DI 注册: 调用 <c>SessionServiceCollectionExtensions.AddSessionRuntime()</c> 注册自动保存等后台服务。
/// </remarks>
public sealed class JsonSessionService : ISessionService
{
    private const string SessionFileExtension = ".json";
    private const string LockFileExtension = ".lock";

    private static readonly JsonSerializerOptions JsonOptions = BuildJsonOptions();

    private readonly string _baseDir;
    private readonly string _machineName;

    public JsonSessionService(string? baseDir = null)
    {
        _baseDir = baseDir ?? OpenShellPaths.Root;
        _machineName = Environment.MachineName;
    }

    /// <inheritdoc />
    public Session? Current { get; private set; }

    /// <inheritdoc />
    public Task<Session> LoadOrCreateAsync(string sessionName, CancellationToken ct = default)
    {
        EnsureSessionDirs();
        var path = GetSessionFilePath(sessionName);
        Session session;
        if (File.Exists(path))
        {
            session = TryReadSession(path) ?? CreateDefaultSession(sessionName);
        }
        else
        {
            session = CreateDefaultSession(sessionName);
        }
        Current = session;
        return Task.FromResult(session);
    }

    /// <inheritdoc />
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var session = Current ?? throw new InvalidOperationException(
            "No active session. Call LoadOrCreateAsync first.");
        EnsureSessionDirs();
        var path = GetSessionFilePath(session.Name);
        var updated = session with { LastActive = DateTimeOffset.UtcNow };
        Current = updated;
        await WriteSessionAsync(path, updated, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void UpdateCurrent(Session updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        if (Current is null)
            throw new InvalidOperationException("No active session. Call LoadOrCreateAsync first.");
        Current = updated;
    }

    /// <inheritdoc />
    public Task<CrashDetectionResult> DetectCrashAsync(string sessionName, CancellationToken ct = default)
    {
        var lockPath = GetLockFilePath(sessionName);
        if (!File.Exists(lockPath))
        {
            return Task.FromResult(new CrashDetectionResult(false, false, null, null));
        }

        LockFileContent? lockContent = null;
        try
        {
            var text = File.ReadAllText(lockPath);
            lockContent = JsonSerializer.Deserialize<LockFileContent>(text, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[sessions] failed to read lock file '{lockPath}': {ex.Message}");
        }

        if (lockContent is null || lockContent.Pid is null || lockContent.Pid <= 0)
        {
            return Task.FromResult(new CrashDetectionResult(true, false, lockContent?.Pid, lockContent?.Machine));
        }

        var pid = lockContent.Pid.Value;
        var alive = IsProcessAlive(pid);
        return Task.FromResult(new CrashDetectionResult(true, alive, pid, lockContent.Machine));
    }

    /// <inheritdoc />
    public Task AcquireLockAsync(string sessionName, CancellationToken ct = default)
    {
        EnsureSessionDirs();
        var lockPath = GetLockFilePath(sessionName);
        var content = new LockFileContent
        {
            Pid = Environment.ProcessId,
            Started = DateTimeOffset.UtcNow,
            Machine = _machineName,
        };
        try
        {
            var text = JsonSerializer.Serialize(content, JsonOptions);
            File.WriteAllText(lockPath, text);
            TrySetUserOnlyPermissions(lockPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[sessions] failed to acquire lock '{lockPath}': {ex.Message}");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReleaseLockAsync(string sessionName, CancellationToken ct = default)
    {
        var lockPath = GetLockFilePath(sessionName);
        try
        {
            if (File.Exists(lockPath)) File.Delete(lockPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[sessions] failed to release lock '{lockPath}': {ex.Message}");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SaveSnapshotAsync(string snapshotName, CancellationToken ct = default)
    {
        var session = Current ?? throw new InvalidOperationException(
            "No active session. Call LoadOrCreateAsync first.");
        EnsureSessionDirs();
        var path = GetSnapshotFilePath(snapshotName);
        await WriteSessionAsync(path, session, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Session?> LoadSnapshotAsync(string snapshotName, CancellationToken ct = default)
    {
        var path = GetSnapshotFilePath(snapshotName);
        if (!File.Exists(path)) return Task.FromResult<Session?>(null);
        var session = TryReadSession(path);
        return Task.FromResult(session);
    }

    /// <inheritdoc />
    public Task ClearSessionAsync(string sessionName, CancellationToken ct = default)
    {
        TryDelete(GetSessionFilePath(sessionName));
        TryDelete(GetLockFilePath(sessionName));
        if (Current is not null && string.Equals(Current.Name, sessionName, StringComparison.Ordinal))
        {
            Current = null;
        }
        return Task.CompletedTask;
    }

    private static Session CreateDefaultSession(string name)
    {
        var now = DateTimeOffset.UtcNow;
        var home = new ItemPath { Provider = "fs", InternalPath = GetHomePath() };
        return new Session(
            Id: Guid.NewGuid(),
            Name: name,
            Created: now,
            LastActive: now,
            State: new SessionState(
                CurrentLocation: home,
                NavigationHistory: Array.Empty<ItemPath>(),
                Tabs: Array.Empty<TabState>(),
                ActiveTabIndex: 0));
    }

    private static Session? TryReadSession(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var session = JsonSerializer.Deserialize<Session>(text, JsonOptions);
            if (session is null) return null;
            // 防御性：NavigationHistory / Tabs 为 null 时降级到空列表。
            var state = session.State;
            if (state.NavigationHistory is null || state.Tabs is null)
            {
                return session with
                {
                    State = state with
                    {
                        NavigationHistory = state.NavigationHistory ?? Array.Empty<ItemPath>(),
                        Tabs = state.Tabs ?? Array.Empty<TabState>(),
                    },
                };
            }
            return session;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[sessions] failed to read session file '{path}': {ex.Message}");
            return null;
        }
    }

    private static async Task WriteSessionAsync(string path, Session session, CancellationToken ct)
    {
        // D-510: 原子替换——先写唯一命名的临时文件再 Move 覆盖目标，
        // 并发读者（另一宿主/自动保存）永远不会看到撕裂的中间内容。
        var tmpPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var text = JsonSerializer.Serialize(session, JsonOptions);
            await File.WriteAllTextAsync(tmpPath, text, ct).ConfigureAwait(false);
            TrySetUserOnlyPermissions(tmpPath);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(tmpPath);
            Console.Error.WriteLine($"[sessions] failed to write session file '{path}': {ex.Message}");
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            // HasExited 在某些平台返回 false 即使进程已僵尸；这里只关心进程是否存在。
            return !proc.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void TrySetUserOnlyPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // best-effort: 文件权限设置失败不阻塞功能。
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Console.Error.WriteLine($"[sessions] failed to delete '{path}': {ex.Message}"); }
    }

    private static string GetHomePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Replace('\\', '/');
    }

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        // 复用 IPC 的 ItemPath converter (同 assembly，可直接引用)。
        opts.Converters.Add(new IpcItemPathConverter());
        return opts;
    }

    private void EnsureSessionDirs()
    {
        Directory.CreateDirectory(Path.Combine(_baseDir, "sessions"));
        Directory.CreateDirectory(Path.Combine(_baseDir, "snapshots"));
    }

    private string GetSessionFilePath(string sessionName) =>
        Path.Combine(_baseDir, "sessions", sessionName + SessionFileExtension);

    private string GetLockFilePath(string sessionName) =>
        Path.Combine(_baseDir, "sessions", sessionName + LockFileExtension);

    private string GetSnapshotFilePath(string snapshotName) =>
        Path.Combine(_baseDir, "snapshots", snapshotName + SessionFileExtension);

    /// <summary>锁文件 JSON 内容。Per ADR-0034 §4.</summary>
    private sealed class LockFileContent
    {
        [JsonPropertyName("pid")]
        public int? Pid { get; set; }

        [JsonPropertyName("started")]
        public DateTimeOffset? Started { get; set; }

        [JsonPropertyName("machine")]
        public string? Machine { get; set; }
    }
}
