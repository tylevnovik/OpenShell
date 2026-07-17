using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Logging;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Get-DiagnosticBundle</c> command. Per ADR-0031 §7, §9.
/// 将最近日志、系统信息、环境变量 (脱敏) 打包为 zip 文件, 便于用户报告问题时附带。
/// 输出: 一个表示 zip 文件路径的 IItem (Path 指向 zip, Properties 含 "Path")。
/// </summary>
[Verb("Get", Noun = "DiagnosticBundle", Aliases = ["get-diagnostic-bundle"])]
[Description("Exports a diagnostic bundle (logs + system info + redacted env vars) as a zip archive.")]
public sealed class GetDiagnosticBundleCommand : ICommand<GetDiagnosticBundleCommand.Args>
{
    /// <summary>Arguments for <c>Get-DiagnosticBundle</c>.</summary>
    /// <param name="OutputDir">zip 输出目录; 默认当前工作目录。</param>
    public record Args(
        [property: Parameter(Position = 0)] string? OutputDir = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var logStore = ctx.Host.Services.GetService(typeof(ILogStore)) as ILogStore;
        if (logStore is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Log store is not available in this context.",
                Operation = "get-diagnosticbundle",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 输出目录: 显式参数 > 当前工作目录。
        var outputDir = !string.IsNullOrWhiteSpace(args.OutputDir)
            ? args.OutputDir!
            : Environment.CurrentDirectory;

        // 优先从 DI 解析 DiagnosticBundleExporter 单例 (与 host 注册一致); 未注册时即时构造。
        var exporter = ctx.Host.Services.GetService(typeof(DiagnosticBundleExporter)) as DiagnosticBundleExporter
            ?? new DiagnosticBundleExporter(logStore, outputDir);

        string zipPath;
        try
        {
            zipPath = await exporter.ExportAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.IOError,
                Message = $"failed to export diagnostic bundle: {ex.Message}",
                Operation = "get-diagnosticbundle",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync($"diagnostic bundle: {zipPath}", ct).ConfigureAwait(false);

        var fileInfo = new System.IO.FileInfo(zipPath);
        var itemPath = ItemPath.Parse(zipPath.Replace('\\', '/'));
        // FileInfo.LastWriteTimeUtc 是 DateTime; 显式转 DateTimeOffset 避免 Unspecified Kind 转换异常。
        var modified = fileInfo.Exists
            ? new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero)
            : (DateTimeOffset?)null;
        yield return new Item
        {
            Path = itemPath,
            Kind = ItemKind.File,
            Size = fileInfo.Exists ? fileInfo.Length : (long?)null,
            Timestamps = new ItemTimestamps(null, modified, null),
            Properties = PropertyBag.Empty
                .With("Path", zipPath)
                .With("OutputDir", outputDir),
        };
    }
}
