using System.IO.Compression;
using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Expand-Archive</c> 命令。解压 zip 压缩包。Per ADR-0023 M4 + ADR-0017.
/// <para>
/// 把 zip 内容解压到指定目录。防 zip-slip 攻击 (entry 路径不能逃逸目标目录)。
/// 声明 <c>SupportsShouldProcess</c> (per ADR-0049)。
/// </para>
/// </summary>
[Verb("Expand", Noun = "Archive", Aliases = ["expand", "ez", "unzip"])]
[SupportsShouldProcess(ConfirmImpact = ConfirmImpact.Medium)]
[Description("Extracts a compressed archive to a directory.")]
public sealed class ExpandArchiveCommand : ICommand<ExpandArchiveCommand.Args>
{
    /// <summary>Arguments for <c>Expand-Archive</c>.</summary>
    /// <param name="Path">zip 文件路径。</param>
    /// <param name="DestinationPath">解压目标目录 (默认当前目录)。</param>
    /// <param name="Force">覆盖已有文件。</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Path,
        [property: Parameter(Position = 1)] string? DestinationPath = null,
        [property: Parameter] bool Force = false);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var zipPath = ResolveFsPath(args.Path, ctx);
        if (!File.Exists(zipPath))
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemNotFound,
                Message = $"Archive not found: {zipPath}",
                TargetPath = new ItemPath { Provider = "fs", InternalPath = zipPath.Replace('\\', '/') },
                Operation = "expand-archive",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        var destPath = string.IsNullOrEmpty(args.DestinationPath)
            ? Directory.GetCurrentDirectory()
            : ResolveFsPath(args.DestinationPath, ctx);

        if (!ctx.ShouldProcess($"{zipPath} -> {destPath}", "Expand-Archive", ConfirmImpact.Medium))
            yield break;

        Directory.CreateDirectory(destPath);
        var destFull = System.IO.Path.GetFullPath(destPath);

        try
        {
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 81920, useAsync: true);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();

                // 防 zip-slip: entry 全路径必须在目标目录内.
                var entryFull = System.IO.Path.GetFullPath(System.IO.Path.Combine(destFull, entry.FullName));

                if (!entryFull.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Errors?.Write(new ErrorRecord
                    {
                        Category = ErrorCategory.PermissionDenied,
                        Message = $"Entry '{entry.FullName}' escapes destination directory (zip-slip blocked).",
                        Operation = "expand-archive",
                        Phase = ErrorPhase.Operation,
                    });
                    continue;
                }

                // 目录 entry (以 / 结尾或无内容).
                if (entry.FullName.EndsWith('/') || entry.Length == 0 && string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(entryFull);
                    continue;
                }

                // 文件 entry.
                var dir = System.IO.Path.GetDirectoryName(entryFull);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                if (File.Exists(entryFull) && !args.Force)
                {
                    ctx.Errors?.Write(new ErrorRecord
                    {
                        Category = ErrorCategory.ItemAlreadyExists,
                        Message = $"File already exists: {entryFull} (use -Force to overwrite).",
                        Operation = "expand-archive",
                        Phase = ErrorPhase.Operation,
                    });
                    continue;
                }

                await using var entryStream = entry.Open();
                await using var outStream = new FileStream(entryFull, FileMode.Create, FileAccess.Write,
                    FileShare.None, bufferSize: 81920, useAsync: true);
                await entryStream.CopyToAsync(outStream, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.OperationFailed,
                Message = $"Expand-Archive failed: {ex.Message}",
                Operation = "expand-archive",
                Phase = ErrorPhase.Operation,
                Exception = ex,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Extracted to: {destPath}", ct).ConfigureAwait(false);
    }

    private static string ResolveFsPath(string path, CommandContext ctx)
    {
        if (System.IO.Path.IsPathRooted(path))
            return path;
        if (ctx.CurrentLocation.Provider == "fs")
            return System.IO.Path.Combine(ctx.CurrentLocation.InternalPath.Replace('/', System.IO.Path.DirectorySeparatorChar), path);
        return System.IO.Path.GetFullPath(path);
    }
}
