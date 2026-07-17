using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShell;
using OpenShell.Paths;

namespace OpenShell.Security;

/// <summary>
/// 单条审计记录。Per ADR-0036 §5.
/// </summary>
public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    string User,
    string Command,
    string Args,
    OperationRisk Risk,
    bool Approved,
    string ApprovedBy); // "prompt" / "config" / "auto" / "force"

/// <summary>
/// 操作审计服务。Per ADR-0036 §5.
/// 记录敏感操作 (High 及以上) 的执行情况, 供 <c>get-audit</c> 查询。
/// </summary>
/// <remarks>
/// ADR-0036 §5: 30 天保留期清理由 <see cref="AuditRetentionService"/> (IHostedService) 实现。
/// ADR-0036 §10: 凭据脱敏由 <see cref="CredentialRedactor"/> 实现 (写入前应用于 Args)。
/// </remarks>
public interface IAuditService
{
    /// <summary>追加一条审计日志。</summary>
    Task LogAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>查询审计日志, 可选按时间过滤。</summary>
    Task<IReadOnlyList<AuditEntry>> QueryAsync(DateTimeOffset? since = null, CancellationToken ct = default);

    /// <summary>清空全部审计日志 (由 clear-audit 命令调用, 需 -Force)。</summary>
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// 基于 JSONL 文件的 <see cref="IAuditService"/> 实现。Per ADR-0036 §5.
/// 每条记录追加一行到 <c>~/.openshell/audit.jsonl</c>; 文件权限 0600 (Unix)。
/// 写入前通过 <see cref="CredentialRedactor.Redact"/> 对 <see cref="AuditEntry.Args"/> 脱敏 (ADR-0036 §10)。
/// </summary>
public sealed class JsonAuditService : IAuditService
{
    private static readonly JsonSerializerOptions JsonOptions = BuildJsonOptions();

    private readonly string _filePath;
    private readonly string _user;
    private readonly object _lock = new();

    /// <summary>
    /// 构造 JsonAuditService。
    /// </summary>
    /// <param name="filePath">JSONL 文件路径; 默认 <see cref="OpenShellPaths.AuditLog"/>; 测试可注入。</param>
    /// <param name="user">执行用户标识; 默认 <see cref="Environment.UserName"/>。</param>
    public JsonAuditService(string? filePath = null, string? user = null)
    {
        _filePath = filePath ?? OpenShellPaths.AuditLog;
        _user = user ?? Environment.UserName;
    }

    /// <summary>当前用户标识 (用于构造 <see cref="AuditEntry"/>)。</summary>
    public string CurrentUser => _user;

    /// <inheritdoc />
    public async Task LogAsync(AuditEntry entry, CancellationToken ct = default)
    {
        // 确保 entry.User 已填; 若调用方未填则用 CurrentUser。
        var toWrite = string.IsNullOrEmpty(entry.User)
            ? entry with { User = _user }
            : entry;

        // ADR-0036 §10: 凭据脱敏, 防止密码/令牌泄漏到审计日志。
        toWrite = toWrite with { Args = CredentialRedactor.Redact(toWrite.Args) };

        var line = JsonSerializer.Serialize(toWrite, JsonOptions);

        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        await using var stream = new FileStream(
            _filePath, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        await using var writer = new StreamWriter(stream) { NewLine = "\n" };
        await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);

        TrySetUserOnlyPermissions(_filePath);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditEntry>> QueryAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var results = new List<AuditEntry>();

        // 加锁读取避免并发写入冲突 (FileShare.Read 允许并发读)。
        string[] lines;
        lock (_lock)
        {
            if (!File.Exists(_filePath))
                return Task.FromResult<IReadOnlyList<AuditEntry>>(results);
            try
            {
                lines = File.ReadAllLines(_filePath);
            }
            catch (IOException)
            {
                return Task.FromResult<IReadOnlyList<AuditEntry>>(results);
            }
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            AuditEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<AuditEntry>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // 损坏行跳过 (ADR-0034 §5 类似降级策略)。
                continue;
            }
            if (entry is null) continue;
            if (since is { } s && entry.Timestamp < s) continue;
            results.Add(entry);
        }

        return Task.FromResult<IReadOnlyList<AuditEntry>>(results);
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath)) File.Delete(_filePath);
            }
            catch (IOException)
            {
                // best-effort: 文件被锁定时静默忽略。
            }
        }
        return Task.CompletedTask;
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
            // best-effort: 权限失败不阻塞功能。
        }
    }

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        opts.Converters.Add(new AuditEntryItemPathPassThroughConverter());
        return opts;
    }

    /// <summary>
    /// 占位 converter (AuditEntry 本身不直接持有 ItemPath, 但保留扩展位)。
    /// JsonAuditService 字段均为 string/enum, 默认 camelCase 序列化即可。
    /// </summary>
    private sealed class AuditEntryItemPathPassThroughConverter : JsonConverter<ItemPath>
    {
        public override ItemPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => ItemPath.Parse(reader.GetString() ?? "");

        public override void Write(Utf8JsonWriter writer, ItemPath value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Display);
    }
}
