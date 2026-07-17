using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using OpenShell.TestUtils;
using OpenShell.Updates;
using Xunit;

namespace OpenShell.Core.Tests.Updates;

/// <summary>
/// ADR-0037 §1-§7: GitHubReleasesUpdateService 单测。
/// 用自定义 HttpMessageHandler mock GitHub API + asset 下载，文件操作隔离在 TempDir 内。
/// </summary>
public class GitHubReleasesUpdateServiceTests
{
    private static readonly string CurrentRid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
    private static readonly string CurrentRidNormalized = NormalizeRidForTest(CurrentRid);
    private static readonly Version InstalledVersion = new(0, 1, 0);

    [Fact]
    public async Task CheckForUpdatesAsync_FindsMatchingAsset()
    {
        using var dir = new TempDir();
        var assetName = $"openshell-cli-{CurrentRidNormalized}{(OperatingSystem.IsWindows() ? ".exe" : "")}";
        var json = BuildReleasesJson(new[]
        {
            new FakeRelease(
                TagName: "v0.2.0",
                Prerelease: false,
                PublishedAt: "2026-07-01T00:00:00Z",
                Body: "Bug fixes",
                Assets: new[]
                {
                    new FakeAsset(Name: assetName, Size: 1024, Url: "https://example.com/dl/openshell-0.2.0"),
                }),
        });
        var handler = new FakeHttpMessageHandler();
        handler.Respond("https://api.github.com/repos/openshell-org/openshell/releases", json);
        handler.Respond("https://example.com/dl/openshell-0.2.0", "binary-content"u8.ToArray());

        using var http = new HttpClient(handler);
        var svc = new TestableUpdateService(http, currentExePath: "/tmp/fake", updatesDir: dir.FullPath,
            currentVersion: InstalledVersion);

        var info = await svc.CheckForUpdatesAsync();

        info.Should().NotBeNull();
        info!.Version.ToString().Should().Be("0.2.0");
        info.ReleaseNotes.Should().Be("Bug fixes");
        info.SizeBytes.Should().Be(1024);
        info.IsPrerelease.Should().BeFalse();
        info.DownloadUrl.ToString().Should().Be("https://example.com/dl/openshell-0.2.0");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_SkipsPrerelease_ByDefault()
    {
        using var dir = new TempDir();
        var assetName = $"openshell-cli-{CurrentRidNormalized}";
        var json = BuildReleasesJson(new[]
        {
            new FakeRelease(
                TagName: "v0.3.0-beta1",
                Prerelease: true,
                PublishedAt: "2026-07-02T00:00:00Z",
                Body: "Beta",
                Assets: new[]
                {
                    new FakeAsset(assetName, 2048, "https://example.com/beta"),
                }),
            new FakeRelease(
                TagName: "v0.2.0",
                Prerelease: false,
                PublishedAt: "2026-07-01T00:00:00Z",
                Body: "Stable",
                Assets: new[]
                {
                    new FakeAsset(assetName, 1024, "https://example.com/stable"),
                }),
        });
        var handler = new FakeHttpMessageHandler();
        handler.Respond("https://api.github.com/repos/openshell-org/openshell/releases", json);

        using var http = new HttpClient(handler);
        var svc = new TestableUpdateService(http, currentExePath: "/tmp/fake", updatesDir: dir.FullPath,
            currentVersion: InstalledVersion);

        var info = await svc.CheckForUpdatesAsync();

        info.Should().NotBeNull();
        info!.Version.ToString().Should().Be("0.2.0"); // 跳过 beta，返回 stable
        info.ReleaseNotes.Should().Be("Stable");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_IncludesPrerelease_WhenEnabled()
    {
        using var dir = new TempDir();
        var assetName = $"openshell-cli-{CurrentRidNormalized}";
        var json = BuildReleasesJson(new[]
        {
            new FakeRelease(
                TagName: "v0.3.0-beta1",
                Prerelease: true,
                PublishedAt: "2026-07-02T00:00:00Z",
                Body: "Beta",
                Assets: new[]
                {
                    new FakeAsset(assetName, 2048, "https://example.com/beta"),
                }),
        });
        var handler = new FakeHttpMessageHandler();
        handler.Respond("https://api.github.com/repos/openshell-org/openshell/releases", json);

        using var http = new HttpClient(handler);
        var svc = new TestableUpdateService(http, currentExePath: "/tmp/fake", updatesDir: dir.FullPath,
            currentVersion: InstalledVersion)
        {
            IncludePrerelease = true,
        };

        var info = await svc.CheckForUpdatesAsync();

        info.Should().NotBeNull();
        info!.Version.ToString().Should().Be("0.3.0");
        info.IsPrerelease.Should().BeTrue();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_NoMatchingAsset_ReturnsNull()
    {
        using var dir = new TempDir();
        // asset 名只包含不存在的 RID
        var json = BuildReleasesJson(new[]
        {
            new FakeRelease(
                TagName: "v0.2.0",
                Prerelease: false,
                PublishedAt: "2026-07-01T00:00:00Z",
                Body: "Stable",
                Assets: new[]
                {
                    new FakeAsset("openshell-cli-otherplatform-x64", 1024, "https://example.com/other"),
                }),
        });
        var handler = new FakeHttpMessageHandler();
        handler.Respond("https://api.github.com/repos/openshell-org/openshell/releases", json);

        using var http = new HttpClient(handler);
        var svc = new TestableUpdateService(http, currentExePath: "/tmp/fake", updatesDir: dir.FullPath,
            currentVersion: InstalledVersion);

        var info = await svc.CheckForUpdatesAsync();

        info.Should().BeNull();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_EmptyReleases_ReturnsNull()
    {
        using var dir = new TempDir();
        var handler = new FakeHttpMessageHandler();
        handler.Respond("https://api.github.com/repos/openshell-org/openshell/releases", "[]");

        using var http = new HttpClient(handler);
        var svc = new TestableUpdateService(http, currentExePath: "/tmp/fake", updatesDir: dir.FullPath,
            currentVersion: InstalledVersion);

        var info = await svc.CheckForUpdatesAsync();

        info.Should().BeNull();
    }

    [Fact]
    public async Task DownloadAsync_StreamsToFile_AndVerifiesSha256_Success()
    {
        using var dir = new TempDir();
        // 准备 asset 内容和 SHA256
        var content = "hello openshell update!"u8.ToArray();
        var sha = ComputeSha256(content);

        var handler = new FakeHttpMessageHandler();
        handler.Respond("https://example.com/dl/v0.2.0", content);

        using var http = new HttpClient(handler);
        var svc = new GitHubReleasesUpdateService(http, updatesDir: dir.FullPath);

        var info = new UpdateInfo(
            Version: new Version(0, 2, 0),
            ReleaseNotes: "",
            DownloadUrl: new Uri("https://example.com/dl/v0.2.0"),
            Sha256: sha,
            SizeBytes: content.Length,
            PublishedAt: DateTimeOffset.UtcNow,
            IsPrerelease: false);

        var progressReports = new List<double>();
        var progress = new Progress<double>(p => progressReports.Add(p));

        await svc.DownloadAsync(info, progress);

        // 文件应已写入 0.2.0/dl (Path.GetFileName of /dl/v0.2.0 is "v0.2.0")
        var versionDir = Path.Combine(dir.FullPath, "0.2.0");
        var finalPath = Path.Combine(versionDir, "v0.2.0");
        File.Exists(finalPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(finalPath)).Should().Equal(content);

        // .partial 文件应该已被重命名移除
        File.Exists(finalPath + ".partial").Should().BeFalse();

        // 进度至少有一次报告
        progressReports.Should().NotBeEmpty();
    }

    [Fact]
    public async Task DownloadAsync_Sha256Mismatch_DeletesFileAndThrows()
    {
        using var dir = new TempDir();
        var content = "abc"u8.ToArray();

        var handler = new FakeHttpMessageHandler();
        handler.Respond("https://example.com/dl/v0.2.0", content);

        using var http = new HttpClient(handler);
        var svc = new GitHubReleasesUpdateService(http, updatesDir: dir.FullPath);

        var info = new UpdateInfo(
            Version: new Version(0, 2, 0),
            ReleaseNotes: "",
            DownloadUrl: new Uri("https://example.com/dl/v0.2.0"),
            Sha256: "deadbeef".PadRight(64, '0'), // 故意错误的 SHA256
            SizeBytes: content.Length,
            PublishedAt: DateTimeOffset.UtcNow,
            IsPrerelease: false);

        var act = async () => await svc.DownloadAsync(info, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SHA256 verification failed*");

        // .partial 文件应被删除
        var versionDir = Path.Combine(dir.FullPath, "0.2.0");
        var partialPath = Path.Combine(versionDir, "v0.2.0.partial");
        var finalPath = Path.Combine(versionDir, "v0.2.0");
        File.Exists(partialPath).Should().BeFalse();
        File.Exists(finalPath).Should().BeFalse();
    }

    [Fact]
    public async Task InstallAsync_AtomicRename_AndCreatesOldBackup()
    {
        using var dir = new TempDir();
        // 当前 exe 模拟为 TempDir 中的一个文件
        var currentExePath = Path.Combine(dir.FullPath, "openshell-cli-test.exe");
        await File.WriteAllTextAsync(currentExePath, "OLD-BINARY-CONTENT");

        // 下载好的新版本
        var version = new Version(0, 2, 0);
        var versionDir = Path.Combine(dir.FullPath, version.ToString());
        Directory.CreateDirectory(versionDir);
        var downloadedPath = Path.Combine(versionDir, "openshell-0.2.0.bin");
        await File.WriteAllTextAsync(downloadedPath, "NEW-BINARY-CONTENT");

        var handler = new FakeHttpMessageHandler();
        using var http = new HttpClient(handler);
        // 子类化以注入 currentExePath
        var svc = new TestableUpdateService(http, currentExePath, updatesDir: dir.FullPath);

        var info = new UpdateInfo(
            Version: version,
            ReleaseNotes: "",
            DownloadUrl: new Uri("https://example.com/dl/openshell-0.2.0.bin"),
            Sha256: "",
            SizeBytes: 0,
            PublishedAt: DateTimeOffset.UtcNow,
            IsPrerelease: false);

        await svc.InstallAsync(info);

        // currentExe 应已被替换为新内容
        (await File.ReadAllTextAsync(currentExePath)).Should().Be("NEW-BINARY-CONTENT");
        // .old 备份应包含旧内容
        (await File.ReadAllTextAsync(currentExePath + ".old")).Should().Be("OLD-BINARY-CONTENT");
    }

    [Fact]
    public async Task InstallAsync_DownloadedFileMissing_Throws()
    {
        using var dir = new TempDir();
        var currentExePath = Path.Combine(dir.FullPath, "openshell-cli-test.exe");
        await File.WriteAllTextAsync(currentExePath, "OLD");

        var handler = new FakeHttpMessageHandler();
        using var http = new HttpClient(handler);
        var svc = new TestableUpdateService(http, currentExePath, updatesDir: dir.FullPath);

        var info = new UpdateInfo(
            Version: new Version(0, 2, 0),
            ReleaseNotes: "",
            DownloadUrl: new Uri("https://example.com/dl/openshell-0.2.0.bin"),
            Sha256: "",
            SizeBytes: 0,
            PublishedAt: DateTimeOffset.UtcNow,
            IsPrerelease: false);

        var act = async () => await svc.InstallAsync(info);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Rollback_RestoresOldBackup()
    {
        using var dir = new TempDir();
        var currentExePath = Path.Combine(dir.FullPath, "openshell-cli-test.exe");
        await File.WriteAllTextAsync(currentExePath, "NEW-BINARY");
        // .old 文件存在
        await File.WriteAllTextAsync(currentExePath + ".old", "OLD-BINARY");

        var handler = new FakeHttpMessageHandler();
        using var http = new HttpClient(handler);
        var svc = new TestableUpdateService(http, currentExePath, updatesDir: dir.FullPath);

        var ok = svc.Rollback();

        ok.Should().BeTrue();
        (await File.ReadAllTextAsync(currentExePath)).Should().Be("OLD-BINARY");
        // .old 文件应被替换为原来的 "current" 内容（即 NEW-BINARY），可供再次回滚
        (await File.ReadAllTextAsync(currentExePath + ".old")).Should().Be("NEW-BINARY");
    }

    [Fact]
    public void Rollback_NoOldFile_ReturnsFalse()
    {
        using var dir = new TempDir();
        var currentExePath = Path.Combine(dir.FullPath, "openshell-cli-test.exe");
        File.WriteAllText(currentExePath, "CURRENT");

        var handler = new FakeHttpMessageHandler();
        using var http = new HttpClient(handler);
        var svc = new TestableUpdateService(http, currentExePath, updatesDir: dir.FullPath);

        var ok = svc.Rollback();

        ok.Should().BeFalse();
    }

    [Fact]
    public void UpdateStateStore_ReadWriteLastCheckTime_RoundTrip()
    {
        using var dir = new TempDir();
        var statePath = Path.Combine(dir.FullPath, "state.json");
        var store = new UpdateStateStore(statePath);

        // 文件不存在时返回 null
        store.ReadLastCheckTime().Should().BeNull();

        var when = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
        store.WriteLastCheckTime(when);

        // 重新读取应得到同一时间
        var read = store.ReadLastCheckTime();
        read.Should().Be(when);
    }

    [Fact]
    public void UpdateStateStore_ShouldCheck_RespectsInterval()
    {
        using var dir = new TempDir();
        var statePath = Path.Combine(dir.FullPath, "state.json");
        var store = new UpdateStateStore(statePath);

        // 从未检查：应触发
        store.ShouldCheck(TimeSpan.FromHours(24)).Should().BeTrue();

        // 写入一个 1 小时前的检查时间
        store.WriteLastCheckTime(DateTimeOffset.UtcNow.AddHours(-1));

        // 24h 内：不应触发
        store.ShouldCheck(TimeSpan.FromHours(24)).Should().BeFalse();

        // 但 30 分钟间隔：应触发
        store.ShouldCheck(TimeSpan.FromMinutes(30)).Should().BeTrue();
    }

    [Fact]
    public void UpdateStateStore_CorruptedFile_ReturnsNull()
    {
        using var dir = new TempDir();
        var statePath = Path.Combine(dir.FullPath, "state.json");
        File.WriteAllText(statePath, "not-json-at-all {{{");

        var store = new UpdateStateStore(statePath);
        store.ReadLastCheckTime().Should().BeNull();
    }

    [Fact]
    public async Task StatusChanged_StreamEmits_CheckingStatus()
    {
        using var dir = new TempDir();
        var json = "[]";
        var handler = new FakeHttpMessageHandler();
        handler.Respond("https://api.github.com/repos/openshell-org/openshell/releases", json);

        using var http = new HttpClient(handler);
        var svc = new TestableUpdateService(http, currentExePath: "/tmp/fake", updatesDir: dir.FullPath,
            currentVersion: InstalledVersion);

        var statuses = new List<UpdateStatus>();
        var sub = svc.StatusChanged.Subscribe(s => statuses.Add(s));

        await svc.CheckForUpdatesAsync();
        sub.Dispose();

        // 至少应触发 Checking → Idle
        statuses.Should().Contain(UpdateStatus.Checking);
        statuses.Should().Contain(UpdateStatus.Idle);
    }

    private sealed class TestableUpdateService : GitHubReleasesUpdateService
    {
        private readonly string _currentExePath;
        private readonly Version? _currentVersion;

        public TestableUpdateService(HttpClient http, string currentExePath, string? updatesDir = null,
            Version? currentVersion = null)
            : base(http, updatesDir: updatesDir)
        {
            _currentExePath = currentExePath;
            _currentVersion = currentVersion;
        }

        protected override string ResolveCurrentExecutablePath() => _currentExePath;

        protected override Version? ResolveCurrentVersion() => _currentVersion;
    }

    private static string ComputeSha256(byte[] content)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(content);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeRidForTest(string rid)
    {
        // 与 GitHubReleasesUpdateService.NormalizeRid 保持一致
        if (string.IsNullOrEmpty(rid)) return rid;
        var parts = rid.Split('-');
        if (parts.Length >= 2)
        {
            var os = parts[0].Split('.')[0];
            var arch = parts[^1];
            return $"{os}-{arch}";
        }
        return rid;
    }

    private static string BuildReleasesJson(FakeRelease[] releases)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartArray();
            foreach (var rel in releases)
            {
                writer.WriteStartObject();
                writer.WriteString("tag_name", rel.TagName);
                writer.WriteBoolean("prerelease", rel.Prerelease);
                writer.WriteString("published_at", rel.PublishedAt);
                writer.WriteString("body", rel.Body);
                writer.WriteStartArray("assets");
                foreach (var a in rel.Assets)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", a.Name);
                    writer.WriteNumber("size", a.Size);
                    writer.WriteString("browser_download_url", a.Url);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private sealed record FakeRelease(
        string TagName,
        bool Prerelease,
        string PublishedAt,
        string Body,
        FakeAsset[] Assets);

    private sealed record FakeAsset(string Name, long Size, string Url);

    /// <summary>
    /// 简单的 HttpMessageHandler mock：按 URL 路由到预置的响应体。
    /// 用于隔离 GitHub API + asset 下载的真实网络调用。
    /// </summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _responses = new(StringComparer.Ordinal);

        public void Respond(string url, string body)
            => Respond(url, Encoding.UTF8.GetBytes(body));

        public void Respond(string url, byte[] body)
            => _responses[url] = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri?.ToString() ?? "";
            if (!_responses.TryGetValue(uri, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request,
                });
            }
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request,
            };
            return Task.FromResult(resp);
        }
    }
}
