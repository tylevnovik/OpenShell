using System.IO;
using System.Text.Json;
using FluentAssertions;
using OpenShell.Paths;
using OpenShell.Sessions;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Sessions;

/// <summary>
/// ADR-0034 §1-§13: JsonSessionService 单测。
/// 用 TempDir 隔离文件系统 (构造函数注入 baseDir)，验证:
/// - LoadOrCreateAsync: 首次创建 / 二次加载
/// - SaveAsync: round-trip
/// - DetectCrashAsync: 无 lock / lock 存活 / lock 死亡
/// - SaveSnapshotAsync + LoadSnapshotAsync round-trip
/// - ClearSessionAsync: 清除后 LoadOrCreateAsync 返回新 session
/// - 损坏文件降级: 不抛异常, 返回默认 session
/// - 文件权限 (Unix-only)
/// </summary>
public class JsonSessionServiceTests
{
    private const string DefaultSessionName = "test";

    [Fact]
    public async Task LoadOrCreateAsync_FirstCall_CreatesDefaultSession()
    {
        using var dir = new TempDir();
        var svc = new JsonSessionService(dir.FullPath);

        var session = await svc.LoadOrCreateAsync(DefaultSessionName);

        session.Should().NotBeNull();
        session.Name.Should().Be(DefaultSessionName);
        session.Id.Should().NotBeEmpty();
        session.State.CurrentLocation.Provider.Should().Be("fs");
        session.State.NavigationHistory.Should().BeEmpty();
        session.State.Tabs.Should().BeEmpty();
        session.State.ActiveTabIndex.Should().Be(0);
        svc.Current.Should().BeSameAs(session);
        File.Exists(GetSessionFilePath(dir, DefaultSessionName)).Should().BeFalse();
    }

    [Fact]
    public async Task LoadOrCreateAsync_SecondCall_LoadsExistingSession()
    {
        using var dir = new TempDir();
        var svc1 = new JsonSessionService(dir.FullPath);
        var first = await svc1.LoadOrCreateAsync(DefaultSessionName);
        // 显式 SaveAsync 写入文件, 否则 LoadOrCreateAsync 不持久化首次创建的 session。
        await svc1.SaveAsync();

        var svc2 = new JsonSessionService(dir.FullPath);
        var second = await svc2.LoadOrCreateAsync(DefaultSessionName);

        second.Id.Should().Be(first.Id);
        second.Name.Should().Be(first.Name);
        second.State.CurrentLocation.Should().Be(first.State.CurrentLocation);
        File.Exists(GetSessionFilePath(dir, DefaultSessionName)).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_PersistsCurrentSession()
    {
        using var dir = new TempDir();
        var svc = new JsonSessionService(dir.FullPath);
        var session = await svc.LoadOrCreateAsync(DefaultSessionName);

        await svc.SaveAsync();

        var path = GetSessionFilePath(dir, DefaultSessionName);
        File.Exists(path).Should().BeTrue();
        var text = await File.ReadAllTextAsync(path);
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("id").GetGuid().Should().Be(session.Id);
        doc.RootElement.GetProperty("name").GetString().Should().Be(DefaultSessionName);
    }

    [Fact]
    public async Task SaveAsync_RoundTrip_PreservesLocationAndHistory()
    {
        using var dir = new TempDir();
        var svc1 = new JsonSessionService(dir.FullPath);
        var session = await svc1.LoadOrCreateAsync(DefaultSessionName);
        // LoadOrCreateAsync 后 Current 已被设置; SaveAsync 持久化 Current (含默认 location)。
        await svc1.SaveAsync();

        var svc2 = new JsonSessionService(dir.FullPath);
        var loaded = await svc2.LoadOrCreateAsync(DefaultSessionName);

        loaded.Id.Should().Be(session.Id);
        loaded.State.CurrentLocation.Should().Be(session.State.CurrentLocation);
        loaded.State.NavigationHistory.Should().BeEmpty();
        loaded.State.Tabs.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectCrashAsync_NoLock_ReturnsNoCrash()
    {
        using var dir = new TempDir();
        var svc = new JsonSessionService(dir.FullPath);

        var result = await svc.DetectCrashAsync(DefaultSessionName);

        result.LockExists.Should().BeFalse();
        result.IsProcessAlive.Should().BeFalse();
        result.Pid.Should().BeNull();
        result.MachineName.Should().BeNull();
    }

    [Fact]
    public async Task DetectCrashAsync_LockWithAliveProcess_ReturnsAlive()
    {
        using var dir = new TempDir();
        var svc = new JsonSessionService(dir.FullPath);

        await svc.AcquireLockAsync(DefaultSessionName);
        var result = await svc.DetectCrashAsync(DefaultSessionName);

        result.LockExists.Should().BeTrue();
        result.IsProcessAlive.Should().BeTrue();
        result.Pid.Should().Be(Environment.ProcessId);
        result.MachineName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DetectCrashAsync_LockWithDeadProcess_ReturnsDead()
    {
        using var dir = new TempDir();
        var svc = new JsonSessionService(dir.FullPath);

        // 写入一个不存在的 PID 到锁文件
        var lockPath = GetLockFilePath(dir, DefaultSessionName);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadPid = 999_999; // 几乎不可能存活的 PID
        var lockContent = $$"""{"pid":{{deadPid}},"started":"2026-07-08T12:00:00Z","machine":"dead-host"}""";
        await File.WriteAllTextAsync(lockPath, lockContent);

        var result = await svc.DetectCrashAsync(DefaultSessionName);

        result.LockExists.Should().BeTrue();
        result.IsProcessAlive.Should().BeFalse();
        result.Pid.Should().Be(deadPid);
        result.MachineName.Should().Be("dead-host");
    }

    [Fact]
    public async Task AcquireLockAsync_WritesLockFileWithPidAndMachine()
    {
        using var dir = new TempDir();
        var svc = new JsonSessionService(dir.FullPath);

        await svc.AcquireLockAsync(DefaultSessionName);

        var lockPath = GetLockFilePath(dir, DefaultSessionName);
        File.Exists(lockPath).Should().BeTrue();
        var text = await File.ReadAllTextAsync(lockPath);
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("pid").GetInt32().Should().Be(Environment.ProcessId);
        doc.RootElement.GetProperty("machine").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ReleaseLockAsync_DeletesLockFile()
    {
        using var dir = new TempDir();
        var svc = new JsonSessionService(dir.FullPath);

        await svc.AcquireLockAsync(DefaultSessionName);
        File.Exists(GetLockFilePath(dir, DefaultSessionName)).Should().BeTrue();

        await svc.ReleaseLockAsync(DefaultSessionName);
        File.Exists(GetLockFilePath(dir, DefaultSessionName)).Should().BeFalse();
    }

    [Fact]
    public async Task ReleaseLockAsync_WhenNoLock_DoesNotThrow()
    {
        using var dir = new TempDir();
        var svc = new JsonSessionService(dir.FullPath);

        var act = async () => await svc.ReleaseLockAsync(DefaultSessionName);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaveSnapshotAsync_WritesSnapshotFile()
    {
        using var dir = new TempDir();
        var svc = new JsonSessionService(dir.FullPath);
        await svc.LoadOrCreateAsync(DefaultSessionName);

        await svc.SaveSnapshotAsync("snap1");

        var path = GetSnapshotFilePath(dir, "snap1");
        File.Exists(path).Should().BeTrue();
        var text = await File.ReadAllTextAsync(path);
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("name").GetString().Should().Be(DefaultSessionName);
    }

    [Fact]
    public async Task LoadSnapshotAsync_RoundTrip_ReturnsSameSession()
    {
        using var dir = new TempDir();
        var svc1 = new JsonSessionService(dir.FullPath);
        var session = await svc1.LoadOrCreateAsync(DefaultSessionName);
        await svc1.SaveSnapshotAsync("snap1");

        var svc2 = new JsonSessionService(dir.FullPath);
        var loaded = await svc2.LoadSnapshotAsync("snap1");

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(session.Id);
        loaded.Name.Should().Be(session.Name);
        loaded.State.CurrentLocation.Should().Be(session.State.CurrentLocation);
    }

    [Fact]
    public async Task LoadSnapshotAsync_NonExistent_ReturnsNull()
    {
        using var dir = new TempDir();
        var svc = new JsonSessionService(dir.FullPath);

        var loaded = await svc.LoadSnapshotAsync("nonexistent");

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task ClearSessionAsync_RemovesSessionAndLockFiles()
    {
        using var dir = new TempDir();
        var svc = new JsonSessionService(dir.FullPath);
        await svc.LoadOrCreateAsync(DefaultSessionName);
        await svc.SaveAsync();
        await svc.AcquireLockAsync(DefaultSessionName);

        await svc.ClearSessionAsync(DefaultSessionName);

        File.Exists(GetSessionFilePath(dir, DefaultSessionName)).Should().BeFalse();
        File.Exists(GetLockFilePath(dir, DefaultSessionName)).Should().BeFalse();
    }

    [Fact]
    public async Task ClearSessionAsync_ClearsThenLoadOrCreateReturnsNewSession()
    {
        using var dir = new TempDir();
        var svc1 = new JsonSessionService(dir.FullPath);
        var first = await svc1.LoadOrCreateAsync(DefaultSessionName);
        await svc1.SaveAsync();

        await svc1.ClearSessionAsync(DefaultSessionName);

        var svc2 = new JsonSessionService(dir.FullPath);
        var second = await svc2.LoadOrCreateAsync(DefaultSessionName);

        second.Id.Should().NotBe(first.Id);
        second.Name.Should().Be(DefaultSessionName);
        File.Exists(GetSessionFilePath(dir, DefaultSessionName)).Should().BeFalse();
    }

    [Fact]
    public async Task LoadOrCreateAsync_CorruptedFile_ReturnsDefaultSessionWithoutThrowing()
    {
        using var dir = new TempDir();
        // 预先写入损坏 JSON
        var path = GetSessionFilePath(dir, DefaultSessionName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "this is not valid JSON {{{");

        var svc = new JsonSessionService(dir.FullPath);

        var act = async () => await svc.LoadOrCreateAsync(DefaultSessionName);
        await act.Should().NotThrowAsync();

        var session = await svc.LoadOrCreateAsync(DefaultSessionName);
        session.Should().NotBeNull();
        session.Name.Should().Be(DefaultSessionName);
        session.Id.Should().NotBeEmpty();
        session.State.NavigationHistory.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectCrashAsync_CorruptedLock_ReturnsNotAliveWithoutThrowing()
    {
        using var dir = new TempDir();
        var lockPath = GetLockFilePath(dir, DefaultSessionName);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await File.WriteAllTextAsync(lockPath, "not-json");

        var svc = new JsonSessionService(dir.FullPath);

        var result = await svc.DetectCrashAsync(DefaultSessionName);

        result.LockExists.Should().BeTrue();
        result.IsProcessAlive.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_UpdatesLastActiveTimestamp()
    {
        using var dir = new TempDir();
        var svc = new JsonSessionService(dir.FullPath);
        var session = await svc.LoadOrCreateAsync(DefaultSessionName);
        var originalLastActive = session.LastActive;

        // 确保时间推进 (DateTimeOffset 精度可能跨微秒)
        await Task.Delay(20);
        await svc.SaveAsync();

        svc.Current!.LastActive.Should().BeAfter(originalLastActive);
    }

    [Fact]
    public async Task SaveAsync_OnUnix_SetsUserOnlyFilePermissions()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            // Windows 跳过: chmod 600 不适用。
            return;
        }

        using var dir = new TempDir();
        var svc = new JsonSessionService(dir.FullPath);
        await svc.LoadOrCreateAsync(DefaultSessionName);
        await svc.SaveAsync();

        var path = GetSessionFilePath(dir, DefaultSessionName);
        var mode = File.GetUnixFileMode(path);
        mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task AcquireLockAsync_OnUnix_SetsUserOnlyFilePermissions()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            // Windows 跳过: chmod 600 不适用。
            return;
        }

        using var dir = new TempDir();
        var svc = new JsonSessionService(dir.FullPath);

        await svc.AcquireLockAsync(DefaultSessionName);

        var path = GetLockFilePath(dir, DefaultSessionName);
        var mode = File.GetUnixFileMode(path);
        mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string GetSessionFilePath(TempDir dir, string name) =>
        Path.Combine(dir.FullPath, "sessions", name + ".json");

    private static string GetLockFilePath(TempDir dir, string name) =>
        Path.Combine(dir.FullPath, "sessions", name + ".lock");

    private static string GetSnapshotFilePath(TempDir dir, string name) =>
        Path.Combine(dir.FullPath, "snapshots", name + ".json");
}
