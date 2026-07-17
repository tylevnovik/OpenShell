using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Packaging;
using OpenShell.Packaging.Registry;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Publish-Provider</c> 命令。Per ADR-0039 §5 / §10.
/// 把一个本地 <c>.osp</c> 包发布到注册源 (PUT <c>/v1/packages/{name}/{version}.osp</c>)。
/// ADR-0039 §9: 支持 <c>-SignKeyEd25519</c> (hex 编码 32 字节私钥) 优先使用 Ed25519 签名;
/// 缺省时回退到 legacy <c>-SignKey</c> RSA-SHA256 路径。
/// </summary>
[Verb("Publish", Noun = "Provider", Aliases = ["pbpr"])]
[Description("Publishes a built .osp package to a registry source.")]
public sealed class PublishProviderCommand : ICommand<PublishProviderCommand.Args>
{
    /// <summary>Arguments for <c>Publish-Provider</c>.</summary>
    public record Args
    {
        /// <summary>本地 <c>.osp</c> 包绝对路径。必填。</summary>
        [Parameter(Position = 0)]
        public string? PackagePath { get; init; }

        /// <summary>目标注册源名。必填。</summary>
        [Parameter(Position = 1)]
        public string? Source { get; init; }

        /// <summary>API key (Bearer token)。缺省时使用源配置的 auth。</summary>
        [Parameter(Aliases = ["k", "apikey"])]
        public string? ApiKey { get; init; }

        /// <summary>RSA 私钥 (XML 字符串) 用于在发布前为包追加 RSA-SHA256 签名。已废弃, 推荐用 <see cref="SignKeyEd25519"/>。</summary>
        [Parameter(Aliases = ["sign-key"])]
        public string? SignKey { get; init; }

        /// <summary>
        /// Ed25519 私钥 (hex 编码 32 字节 seed)。Per ADR-0039 §9.
        /// 提供时优先调用 <see cref="OspPackager.SignAsync(string, byte[], CancellationToken)"/> (Ed25519 路径)。
        /// </summary>
        [Parameter(Aliases = ["sign-key-ed25519", "sign-ed25519"])]
        public string? SignKeyEd25519 { get; init; }
    }

    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(args.PackagePath) || string.IsNullOrWhiteSpace(args.Source))
        {
            await ctx.Host.WriteOutputLineAsync(
                "usage: publish-provider <package-path> <source> [-ApiKey <token>] [-SignKeyEd25519 <hex>] [-SignKey <xml>]",
                ct).ConfigureAwait(false);
            yield break;
        }
        if (!File.Exists(args.PackagePath))
        {
            await ctx.Host.WriteOutputLineAsync($"[publish-provider] package not found: {args.PackagePath}", ct).ConfigureAwait(false);
            yield break;
        }

        var registry = ctx.Host.Services.GetService(typeof(ProviderSourceRegistry)) as ProviderSourceRegistry;
        var client = ctx.Host.Services.GetService(typeof(RegistryClient)) as RegistryClient;
        if (registry is null || client is null)
        {
            await ctx.Host.WriteOutputLineAsync("[publish-provider] packaging services not registered.", ct).ConfigureAwait(false);
            yield break;
        }

        var source = registry.Sources.FirstOrDefault(s => string.Equals(s.Name, args.Source, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            await ctx.Host.WriteOutputLineAsync($"[publish-provider] source '{args.Source}' not registered.", ct).ConfigureAwait(false);
            yield break;
        }

        // 可选签名: ADR-0039 §9 — Ed25519 优先, RSA 回退。
        var packager = new OspPackager();
        if (!string.IsNullOrEmpty(args.SignKeyEd25519))
        {
            var keyBytes = TryParseHex(args.SignKeyEd25519);
            if (keyBytes is null || keyBytes.Length != 32)
            {
                await ctx.Host.WriteOutputLineAsync(
                    "[publish-provider] -SignKeyEd25519 must be a hex-encoded 32-byte Ed25519 private key.",
                    ct).ConfigureAwait(false);
                yield break;
            }
            try
            {
                await packager.SignAsync(args.PackagePath, keyBytes, ct).ConfigureAwait(false);
                await ctx.Host.WriteOutputLineAsync("[publish-provider] package signed (Ed25519).", ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ctx.Host.WriteOutputLineAsync($"[publish-provider] signing failed: {ex.Message}", ct).ConfigureAwait(false);
                yield break;
            }
        }
        else if (!string.IsNullOrEmpty(args.SignKey))
        {
#pragma warning disable CS0618 // 故意调用 legacy RSA 重载, 上方条件已表明用户显式选择 RSA 路径。
            try
            {
                await packager.SignAsync(args.PackagePath, args.SignKey, ct).ConfigureAwait(false);
                await ctx.Host.WriteOutputLineAsync("[publish-provider] package signed (RSA-SHA256, legacy).", ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ctx.Host.WriteOutputLineAsync($"[publish-provider] signing failed: {ex.Message}", ct).ConfigureAwait(false);
                yield break;
            }
#pragma warning restore CS0618
        }

        try
        {
            await client.PublishAsync(source, args.PackagePath, args.ApiKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ctx.Host.WriteOutputLineAsync($"[publish-provider] upload failed: {ex.Message}", ct).ConfigureAwait(false);
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync($"published '{args.PackagePath}' to '{args.Source}'.", ct).ConfigureAwait(false);
        yield return new Item
        {
            Path = new ItemPath { Provider = "publish", InternalPath = "/" + Path.GetFileName(args.PackagePath) },
            Kind = ItemKind.Unknown,
            Properties = PropertyBag.Empty
                .With("PackagePath", args.PackagePath!)
                .With("Source", source.Name)
                .With("Published", true),
        };
    }

    /// <summary>把 hex 字符串解析为字节数组; 非法格式返回 null。</summary>
    private static byte[]? TryParseHex(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var cleaned = hex.Trim();
        if ((cleaned.Length % 2) != 0) return null;
        // 允许 0x 前缀。
        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || cleaned.StartsWith("0X", StringComparison.Ordinal))
            cleaned = cleaned[2..];
        if ((cleaned.Length % 2) != 0) return null;
        var bytes = new byte[cleaned.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(cleaned.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                return null;
        }
        return bytes;
    }
}
