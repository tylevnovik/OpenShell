using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Preview;

/// <summary>
/// 视频预览器。Per ADR-0030 §2.
/// 实现限制 (per 任务约束: 不添加重依赖):
/// <list type="bullet">
///   <item>不嵌入 ffmpeg/ffprobe 二进制, 仅在 PATH 上探测可用性。</item>
///   <item>ffprobe 不可用时返回 <see cref="PreviewViewModel.Video"/> (Metadata=null, Duration=null),
///     GUI 显示 "metadata unavailable"。</item>
///   <item>IH-009: 若 PATH 上有 ffmpeg, 额外提取首帧生成缩略图 (PNG); 失败/缺失时降级为纯元数据。</item>
/// </list>
/// </summary>
public sealed class VideoPreviewer : IPreviewer
{
    /// <summary>缩略图最长边像素上限。</summary>
    public const int ThumbnailMaxEdge = 640;
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv", ".wmv", ".m4v", ".mpg", ".mpeg",
    };

    private static readonly HashSet<string> VideoContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp4", "video/x-matroska", "video/quicktime", "video/x-msvideo",
        "video/webm", "video/x-flv", "video/x-ms-wmv",
    };

    private readonly Func<ItemPath, string> _resolveLocalPath;

    /// <summary>
    /// 构造 VideoPreviewer。
    /// </summary>
    /// <param name="resolveLocalPath">
    /// 将 <see cref="ItemPath"/> 解析为本地绝对路径 (用于启动 ffprobe); 远程路径返回 null/空字符串。
    /// </param>
    public VideoPreviewer(Func<ItemPath, string> resolveLocalPath)
    {
        _resolveLocalPath = resolveLocalPath ?? throw new ArgumentNullException(nameof(resolveLocalPath));
    }

    /// <inheritdoc />
    public bool CanPreview(IItem item)
    {
        if (item.Kind != ItemKind.File) return false;
        if (item.ContentType is { } ct && VideoContentTypes.Contains(ct))
            return true;
        return VideoExtensions.Contains(GetExtension(item.Path));
    }

    /// <inheritdoc />
    public async ValueTask<PreviewViewModel?> CreatePreviewAsync(IItem item, PreviewOptions options, CancellationToken ct)
    {
        if (!CanPreview(item)) return null;

        var localPath = _resolveLocalPath(item.Path);
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
        {
            return new PreviewViewModel.Video(null, null);
        }

        var ffprobePath = FindOnPath("ffprobe");
        if (ffprobePath is null)
        {
            // ffprobe 不在 PATH 上 → "metadata unavailable" (per 任务约束)。
            return new PreviewViewModel.Video(null, null);
        }

        try
        {
            var (exitCode, stdout, stderr) = await RunFfprobeAsync(ffprobePath, localPath, ct).ConfigureAwait(false);
            if (exitCode != 0)
            {
                return new PreviewViewModel.Video(null, $"ffprobe error: {stderr.Trim()}");
            }

            // 解析 JSON 输出 (per ffprobe -show_format -show_streams -of json)。
            var (duration, metadata) = ParseFfprobeJson(stdout);

            // IH-009: ffmpeg 可用时提取首帧缩略图; 任何失败都降级为纯元数据, 不影响主预览。
            var (thumbnail, thumbWidth, thumbHeight) = await TryCreateThumbnailAsync(localPath, ct).ConfigureAwait(false);
            return new PreviewViewModel.Video(duration, metadata, thumbnail, thumbWidth, thumbHeight);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PreviewViewModel.Video(null, $"ffprobe failed: {ex.Message}");
        }
    }

    /// <summary>
    /// IH-009: 用 ffmpeg 提取首帧并转 PNG (最长边 <see cref="ThumbnailMaxEdge"/>)。
    /// ffmpeg 缺失、运行失败或解码失败时返回 (null, 0, 0) —— 缩略图是增强项, 不允许拖垮元数据预览。
    /// </summary>
    private static async Task<(byte[]? Png, int Width, int Height)> TryCreateThumbnailAsync(
        string mediaPath, CancellationToken ct)
    {
        var ffmpegPath = FindOnPath("ffmpeg");
        if (ffmpegPath is null) return (null, 0, 0);

        var tempPng = Path.Combine(
            Path.GetTempPath(),
            $"openshell-thumb-{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // -frames:v 1: 只取首帧; scale 滤镜限制最长边, 避免大图占用内存。
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(mediaPath);
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add($"scale='min({ThumbnailMaxEdge},iw)':-2");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("image2");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add(tempPng);

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return (null, 0, 0);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0 || !File.Exists(tempPng)) return (null, 0, 0);

            var bytes = await File.ReadAllBytesAsync(tempPng, ct).ConfigureAwait(false);
            if (bytes.Length < 8) return (null, 0, 0);

            // 用 ImageSharp 读取真实尺寸 (PNG IHDR), 与 Image 预览一致。
            using var image = SixLabors.ImageSharp.Image.Load(bytes);
            return (bytes, image.Width, image.Height);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (null, 0, 0);
        }
        finally
        {
            try { if (File.Exists(tempPng)) File.Delete(tempPng); } catch { /* 临时文件清理失败可忽略 */ }
        }
    }

    /// <summary>查找可执行文件在 PATH 上的完整路径 (per <c>which</c>/<c>where</c>)。</summary>
    private static string? FindOnPath(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;
        var exts = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { ".exe", ".bat", ".cmd", "" }
            : new[] { "" };
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>运行 ffprobe -v error -show_format -show_streams -of json &lt;path&gt;。</summary>
    private static async Task<(int exitCode, string stdout, string stderr)> RunFfprobeAsync(
        string ffprobePath, string mediaPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(mediaPath);

        using var proc = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };

        if (!proc.Start()) return (-1, "", "Process.Start returned false");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return (proc.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }

    /// <summary>从 ffprobe JSON 输出解析 duration + 简短 metadata 文本。轻量正则, 不依赖 System.Text.Json。</summary>
    private static (TimeSpan? Duration, string? Metadata) ParseFfprobeJson(string json)
    {
        TimeSpan? duration = null;
        var sb = new StringBuilder();

        // format.duration (秒, 浮点)。
        var durMatch = Regex.Match(json, @"""duration""\s*:\s*""?([0-9.]+)""?", RegexOptions.Compiled);
        if (durMatch.Success && double.TryParse(durMatch.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var durSeconds))
        {
            duration = TimeSpan.FromSeconds(durSeconds);
            sb.Append($"Duration: {duration:hh\\:mm\\:ss\\.fff}\n");
        }

        // codec_name / codec_type / width / height。
        var codecMatches = Regex.Matches(json,
            @"""codec_type""\s*:\s*""(\w+)""[^}]*""codec_name""\s*:\s*""(\w+)""",
            RegexOptions.Compiled);
        foreach (Match m in codecMatches)
        {
            sb.Append($"{m.Groups[1].Value}: {m.Groups[2].Value}\n");
        }

        var dimMatch = Regex.Match(json, @"""width""\s*:\s*(\d+)[^}]*""height""\s*:\s*(\d+)", RegexOptions.Compiled);
        if (dimMatch.Success)
        {
            sb.Append($"Resolution: {dimMatch.Groups[1].Value}x{dimMatch.Groups[2].Value}\n");
        }

        return (duration, sb.Length > 0 ? sb.ToString().TrimEnd() : null);
    }

    private static string GetExtension(ItemPath path)
    {
        var name = path.GetName();
        var idx = name.LastIndexOf('.');
        return idx >= 0 ? name[idx..] : "";
    }
}
