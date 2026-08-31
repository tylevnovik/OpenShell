using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Items;
using OpenShell.Paths;
using OpenShell.Preview;
using OpenShell.Providers;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>QuickLook</c> 命令。Per ADR-0030 §1.
/// 空格键弹出全屏预览窗口 (macOS Quick Look 风格): 选中文件 → 弹出预览 → 再按空格关闭。
/// 实现策略:
/// <list type="bullet">
///   <item>解析 ItemPath → IItem (via <see cref="IItemProvider"/>)。</item>
///   <item>调 <see cref="IPreviewService.CreatePreviewAsync"/> 生成 <see cref="PreviewViewModel"/>。</item>
///   <item>GUI host: 通过 <c>IHost.Services</c> 解析 <c>QuickLookWindow</c> 触发显示; CLI host: 文本降级输出。</item>
/// </list>
/// 约束 (per ADR-0030 §约束): 预览生成失败显示 NotSupported 原因; Esc / 再按空格关闭窗口。
/// </summary>
[Verb("QuickLook", Aliases = ["quick-look", "ql", "preview"])]
[Description("Quick Look preview window (spacebar).")]
public sealed class QuickLookCommand : ICommand<QuickLookCommand.Args>
{
    /// <summary>Arguments for <c>QuickLook</c>.</summary>
    /// <param name="Path">预览目标路径 (默认当前位置)。</param>
    public record Args(
        [property: Parameter(Position = 0)] ItemPath? Path = null);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = args.Path ?? ctx.CurrentLocation;
        if (path.Provider != "fs" || !path.IsRooted)
        {
            if (ctx.CurrentLocation.Provider != "fs")
            {
                path = new ItemPath { Provider = ctx.CurrentLocation.Provider, InternalPath = path.InternalPath };
            }
            else if (!path.IsRooted)
            {
                path = ctx.CurrentLocation.Combine(path.InternalPath);
            }
        }

        // 1. 解析 IItem (per ADR-0001 IItemProvider 契约)。
        var itemProvider = ctx.Providers.ResolveCapability<IItemProvider>(path);
        if (itemProvider is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.CapabilityNotSupported,
                Message = $"Provider '{path.Provider}' does not support item retrieval.",
                TargetPath = path,
                Operation = "quick-look",
                Phase = ErrorPhase.ProviderResolution,
            });
            yield break;
        }

        var item = await itemProvider.GetItemAsync(path, ct).ConfigureAwait(false);
        if (item is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.ItemNotFound,
                Message = $"Item not found: {path.Display}",
                TargetPath = path,
                Operation = "quick-look",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 2. 调 IPreviewService (per ADR-0030 §1)。
        var previewService = ctx.Host.Services.GetService(typeof(IPreviewService)) as IPreviewService;
        if (previewService is null)
        {
            await ctx.Host.WriteOutputLineAsync(
                $"Quick Look: preview service not registered. Item: {item.Name}", ct).ConfigureAwait(false);
            yield return item;
            yield break;
        }

        var options = new PreviewOptions(MaxWidth: 800, MaxHeight: 600, WithMetadata: true);
        var viewModel = await previewService.CreatePreviewAsync(item, options, ct).ConfigureAwait(false);

        // 3. GUI host: 通过 IQuickLookWindow 显示; CLI host: 文本降级。
        var window = ctx.Host.Services.GetService(typeof(IQuickLookWindow)) as IQuickLookWindow;
        if (window is not null)
        {
            window.Show(item, viewModel);
        }
        else
        {
            // CLI 降级: 把 PreviewViewModel 输出为文本摘要。
            var summary = RenderPreviewAsText(viewModel);
            await ctx.Host.WriteOutputLineAsync($"Quick Look: {item.Name}", ct).ConfigureAwait(false);
            await ctx.Host.WriteOutputLineAsync(summary, ct).ConfigureAwait(false);
        }

        // 透传 item 以便后续管道处理 (与 Get-Item 语义一致)。
        yield return item;
    }

    /// <summary>CLI 降级渲染: 把 <see cref="PreviewViewModel"/> 转 ASCII 摘要。</summary>
    private static string RenderPreviewAsText(PreviewViewModel? vm) => vm switch
    {
        null => "(no preview)",
        PreviewViewModel.Text t => $"Text [{t.Language ?? "?"}] ({t.TotalLines} lines{(t.Truncated ? ", truncated" : "")}):\n{t.Content}",
        PreviewViewModel.Code c => $"Code [{c.Language}] ({c.TotalLines} lines{(c.Truncated ? ", truncated" : "")}):\n{StripAnsi(c.HighlightedContent)}",
        PreviewViewModel.Image i => $"Image: {i.Width}x{i.Height} PNG ({i.PngData.Length} bytes)",
        PreviewViewModel.Archive a => $"Archive: {a.Entries.Count} entries\n  - {string.Join("\n  - ", a.Entries.Take(20).Select(e => e.Name))}",
        PreviewViewModel.Pdf p => $"PDF (~{p.EstimatedPageCount} pages):\n{p.ExtractedText}",
        PreviewViewModel.Video v => $"Video: {(v.Duration is { } d ? $"{d:hh\\:mm\\:ss}" : "duration unknown")}" +
            (v.ThumbnailPng is null ? "" : $"\nThumbnail: {v.ThumbnailWidth}x{v.ThumbnailHeight} PNG ({v.ThumbnailPng.Length} bytes)") +
            $"\n{v.Metadata ?? "(metadata unavailable)"}",
        PreviewViewModel.NotSupported ns => $"Not supported: {ns.Reason}",
        _ => vm.ToString() ?? "(unknown preview)",
    };

    /// <summary>剥离 ANSI 转义 (per ECMA-48) 用于 CLI 文本降级。</summary>
    private static string StripAnsi(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\x1b' && i + 1 < s.Length && s[i + 1] == '[')
            {
                // 跳过 CSI 序列直到 'm' 或其它终结符。
                i += 2;
                while (i < s.Length && s[i] != 'm' && !char.IsLetter(s[i])) i++;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Quick Look 窗口抽象 (per ADR-0030 §1)。
/// 由 GUI host 实现 (Avalonia Window), 由 <see cref="QuickLookCommand"/> 通过 DI 解析调用。
/// 接口位于 Core 层, 不引用 Avalonia.* 命名空间。
/// </summary>
public interface IQuickLookWindow
{
    /// <summary>显示 Quick Look 预览窗口 (模态或非模态由实现决定)。</summary>
    /// <param name="item">预览目标项。</param>
    /// <param name="viewModel">已生成的预览视图模型 (可能为 null, 表示无 previewer 支持)。</param>
    void Show(IItem item, PreviewViewModel? viewModel);

    /// <summary>关闭 Quick Look 窗口 (per ADR-0030 §1: 再按空格 / Esc 关闭)。</summary>
    void Close();
}
