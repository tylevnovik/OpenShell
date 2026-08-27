using System.IO.Compression;
using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Compress-Archive</c> 命令。创建压缩包 (zip)。Per ADR-0023 M4 + ADR-0017.
/// <para>
/// 把指定文件 / 目录打包到 zip。支持 <c>-Update</c> 追加到已有 zip。
/// 声明 <c>SupportsShouldProcess</c> (per ADR-0049)。
/// </para>
/// </summary>
[Verb("Compress", Noun = "Archive", Aliases = ["compress", "cz"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Medium)]
[Description("Creates a compressed archive from files or directories.")]
public sealed class CompressArchiveCommand : ICommand<CompressArchiveCommand.Args>
{
    /// <summary>Arguments for <c>Compress-Archive</c>.</summary>
    /// <param name="Path">要压缩的文件 / 目录路径 (支持多个)。</param>
    /// <param name="DestinationPath">输出 zip 路径。</param>
    /// <param name="CompressionLevel">压缩级别: Optimal / Fastest / NoCompression。</param>
    /// <param name="Update">追加到已有 zip (而非新建)。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string[] Path,
        [property: Parameter(Position = 1, Mandatory = true)] string DestinationPath,
        [property: Parameter] string CompressionLevel = "Optimal",
        [property: Parameter] bool Update = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (args.Path is null || args.Path.Length == 0)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = "Compress-Archive requires at least one -Path.",
                Operation = "compress-archive",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        var destPath = ResolveFsPath(args.DestinationPath, ctx);
        if (!destPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            destPath += ".zip";

        if (!ctx.ShouldProcess(destPath, "Compress-Archive", ConfirmImpact.Medium))
            yield break;

        var level = ParseCompressionLevel(args.CompressionLevel);
        var mode = args.Update && File.Exists(destPath) ? ZipArchiveMode.Update : ZipArchiveMode.Create;

        // 确保目标目录存在.
        var destDir = System.IO.Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        try
        {
            using var fs = new FileStream(destPath, mode == ZipArchiveMode.Update ? FileMode.Open : FileMode.Create,
                FileAccess.ReadWrite, FileShare.None, bufferSize: 81920, useAsync: true);
            using var zip = new ZipArchive(fs, mode, leaveOpen: false);

            foreach (var inputPath in args.Path)
            {
                ct.ThrowIfCancellationRequested();
                var resolvedInput = ResolveFsPath(inputPath, ctx);

                if (Directory.Exists(resolvedInput))
                {
                    await CompressDirectoryAsync(zip, resolvedInput, level, ct).ConfigureAwait(false);
                }
                else if (File.Exists(resolvedInput))
                {
                    await CompressFileAsync(zip, resolvedInput, level, ct).ConfigureAwait(false);
                }
                else
                {
                    ctx.Errors?.Write(new ErrorRecord
                    {
                        Category = ErrorCategory.ItemNotFound,
                        Message = $"Path not found: {resolvedInput}",
                        Operation = "compress-archive",
                        Phase = ErrorPhase.Operation,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = $"Compress-Archive failed: {ex.Message}",
                Operation = "compress-archive",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Created archive: {destPath}", ct).ConfigureAwait(false);
    }

    private static async Task CompressDirectoryAsync(
        ZipArchive zip, string dirPath, CompressionLevel level, CancellationToken ct)
    {
        var dirName = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(dirPath));
        var files = Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relativePath = System.IO.Path.GetRelativePath(dirPath, file);
            // 用正斜杠保持 zip 路径跨平台一致.
            var entryName = string.IsNullOrEmpty(dirName)
                ? relativePath.Replace('\\', '/')
                : $"{dirName}/{relativePath.Replace('\\', '/')}";

            // Update 模式下删除已有同名 entry.
            var existing = zip.GetEntry(entryName);
            existing?.Delete();

            var entry = zip.CreateEntry(entryName, level);
            await using var entryStream = entry.Open();
            await using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 81920, useAsync: true);
            await fileStream.CopyToAsync(entryStream, ct).ConfigureAwait(false);
        }
    }

    private static async Task CompressFileAsync(
        ZipArchive zip, string filePath, CompressionLevel level, CancellationToken ct)
    {
        var entryName = System.IO.Path.GetFileName(filePath);

        var existing = zip.GetEntry(entryName);
        existing?.Delete();

        var entry = zip.CreateEntry(entryName, level);
        await using var entryStream = entry.Open();
        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 81920, useAsync: true);
        await fileStream.CopyToAsync(entryStream, ct).ConfigureAwait(false);
    }

    private static CompressionLevel ParseCompressionLevel(string? level)
    {
        return (level?.Trim().ToLowerInvariant()) switch
        {
            "fastest" => CompressionLevel.Fastest,
            "nocompression" or "none" => CompressionLevel.NoCompression,
            "smallest" or "smallestsize" => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };
    }

    private static string ResolveFsPath(string path, CommandContext ctx)
    {
        if (System.IO.Path.IsPathRooted(path))
            return path;
        // 相对路径: 基于 fs CurrentLocation.
        if (ctx.CurrentLocation.Provider == "fs")
            return System.IO.Path.Combine(ctx.CurrentLocation.InternalPath.Replace('/', System.IO.Path.DirectorySeparatorChar), path);
        return System.IO.Path.GetFullPath(path);
    }
}
