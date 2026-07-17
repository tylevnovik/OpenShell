using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenShell.Updates;

/// <summary>
/// 更新状态持久化。Per ADR-0037 §2.
/// 记录最后检查时间，用于 24h 内不重复检查。
/// 持久化到 <c>~/.openshell/updates/state.json</c>。
/// </summary>
public sealed class UpdateStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    /// <summary>构造 UpdateStateStore。默认持久化到 <see cref="OpenShellPaths.UpdateStateFile"/>。</summary>
    /// <param name="path">state.json 文件路径 (测试可注入)。</param>
    public UpdateStateStore(string? path = null)
    {
        _path = path ?? OpenShellPaths.UpdateStateFile;
    }

    /// <summary>读取最后检查时间。文件不存在或解析失败时返回 null。</summary>
    public DateTimeOffset? ReadLastCheckTime()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var text = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<UpdateStateRecord>(text, JsonOptions);
            return state?.LastCheckAt;
        }
        catch
        {
            // 损坏的状态文件降级到 null，触发重新检查。
            return null;
        }
    }

    /// <summary>持久化最后检查时间。父目录不存在时自动创建。</summary>
    public void WriteLastCheckTime(DateTimeOffset when)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var state = new UpdateStateRecord { LastCheckAt = when };
        var text = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(_path, text);
    }

    /// <summary>判断距上次检查是否已超过指定时间间隔。</summary>
    public bool ShouldCheck(TimeSpan minInterval)
    {
        var last = ReadLastCheckTime();
        if (last is null) return true;
        return DateTimeOffset.UtcNow - last.Value >= minInterval;
    }

    private sealed class UpdateStateRecord
    {
        [JsonPropertyName("lastCheckAt")]
        public DateTimeOffset? LastCheckAt { get; set; }
    }
}
