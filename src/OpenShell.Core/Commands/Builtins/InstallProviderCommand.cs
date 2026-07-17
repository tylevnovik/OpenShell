using System.Runtime.CompilerServices;
using OpenShell.Items;
using OpenShell.Packaging.Installation;
using OpenShell.Packaging.Signing;
using OpenShell.Paths;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in <c>Install-Provider</c> command. Per ADR-0039 §5 / §6.
/// 从注册源安装 Provider 包。支持 <c>-DryRun</c> 预览依赖变更 / <c>-Version</c> 指定版本 /
/// <c>-Source</c> 限定注册源 / <c>-TrustKey</c> 信任未签名包。
/// </summary>
[Verb("Install", Noun = "Provider", Aliases = ["ipr"])]
[Description("Installs a provider package from a registered source.")]
public sealed class InstallProviderCommand : ICommand<InstallProviderCommand.Args>
{
    /// <summary>Arguments for <c>Install-Provider</c>.</summary>
    public record Args
    {
        /// <summary>要安装的 Provider 包名。必填。</summary>
        [Parameter(Position = 0)]
        public string? Name { get; init; }

        /// <summary>指定版本 (缺省取最新稳定版)。</summary>
        [Parameter(Aliases = ["v"])]
        public string? Version { get; init; }

        /// <summary>限定注册源名。</summary>
        [Parameter(Aliases = ["src"])]
        public string? Source { get; init; }

        /// <summary>仅预览依赖与下载清单, 不真正写盘。</summary>
        [Parameter(Aliases = ["dry-run", "whatif"])]
        public bool DryRun { get; init; }

        /// <summary>信任未签名包的公钥 (hex 编码, 与包内嵌 <c>signature.pub</c> 逐字节比对)。
        /// 当签名校验返回 Untrusted 且此值与包内嵌公钥相等时, 视为受信任。Per ADR-0039 §6 / §9.</summary>
        [Parameter(Aliases = ["trust-key"])]
        public string? TrustKey { get; init; }
    }

    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(args.Name))
        {
            await ctx.Host.WriteOutputLineAsync("usage: install-provider <name> [-Version <v>] [-Source <src>] [-DryRun]", ct);
            yield break;
        }

        var installer = ctx.Host.Services.GetService(typeof(IProviderInstaller)) as IProviderInstaller;
        if (installer is null)
        {
            await ctx.Host.WriteOutputLineAsync("[install-provider] IProviderInstaller not registered.", ct);
            yield break;
        }

        // 解析 -TrustKey hex → byte[] (非法格式提前报错, 避免下载后才失败)。
        byte[]? trustKeyBytes = null;
        if (!string.IsNullOrEmpty(args.TrustKey))
        {
            trustKeyBytes = TryParseHex(args.TrustKey);
            if (trustKeyBytes is null)
            {
                await ctx.Host.WriteOutputLineAsync(
                    $"[install-provider] -TrustKey '{args.TrustKey}' is not valid hex.", ct);
                yield break;
            }
        }

        InstallResult result;
        try
        {
            result = await installer.InstallAsync(args.Name, args.Version, args.Source, args.DryRun, trustKeyBytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ctx.Host.WriteOutputLineAsync($"[install-provider] failed: {ex.Message}", ct);
            yield break;
        }

        // 摘要输出。
        if (!string.IsNullOrEmpty(result.Summary))
            await ctx.Host.WriteOutputLineAsync(result.Summary, ct);

        // 依赖列表。
        foreach (var dep in result.Dependencies)
        {
            yield return new Item
            {
                Path = new ItemPath { Provider = "dep", InternalPath = "/" + dep.Name },
                Kind = ItemKind.Unknown,
                Properties = PropertyBag.Empty
                    .With("Name", dep.Name)
                    .With("RequestedVersion", dep.RequestedVersion)
                    .With("ResolvedVersion", dep.ResolvedVersion ?? string.Empty)
                    .With("Kind", dep.Kind)
                    .With("Satisfied", dep.Satisfied),
            };
        }

        // 顶层安装结果。
        yield return new Item
        {
            Path = new ItemPath { Provider = "install", InternalPath = "/" + result.Name },
            Kind = ItemKind.Unknown,
            Properties = PropertyBag.Empty
                .With("Name", result.Name)
                .With("Version", result.Version)
                .With("Source", result.Source ?? string.Empty)
                .With("InstallPath", result.InstallPath ?? string.Empty)
                .With("CurrentPath", result.CurrentPath ?? string.Empty)
                .With("DryRun", result.DryRun),
        };
    }

    /// <summary>把 hex 字符串解析为字节数组; 非法格式返回 null。允许 <c>0x</c> 前缀。</summary>
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
