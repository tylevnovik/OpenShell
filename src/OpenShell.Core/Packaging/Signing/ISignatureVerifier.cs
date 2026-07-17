using OpenShell.Providers;

namespace OpenShell.Packaging.Signing;

/// <summary>
/// .osp 包 detached 签名校验器抽象。Per ADR-0039 §8.
/// 签名算法目标为 Ed25519 (实现见 <see cref="Ed25519SignatureVerifier"/>)。
/// 默认开发实现见 <see cref="NullSignatureVerifier"/>。
/// </summary>
public interface ISignatureVerifier
{
    /// <summary>
    /// 校验一个 <c>.osp</c> 包的 detached 签名。
    /// </summary>
    /// <param name="manifest">包内清单 (调用方已读取, 用于上下文/日志)。</param>
    /// <param name="payloadHash">签名载荷的 SHA256 哈希 (由 <c>OspPackage.ComputePayloadHashAsync</c> 计算, 覆盖 manifest JSON + 所有 DLL 的 SHA256)。</param>
    /// <param name="publicKey">从包内 <c>signature.pub</c> 读出的公钥字节 (可能为 null: 未签名包)。</param>
    /// <param name="signature">从包内 <c>signature.sig</c> 读出的签名字节 (可能为 null: 未签名包)。</param>
    /// <param name="sourceIsTrusted">包来源注册源是否标记为 <c>trusted</c>。Trusted 源可放宽校验。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>校验结果。Trusted 源且未签名返回 <see cref="SignatureResult.TrustedSource"/>。</returns>
    Task<SignatureResult> VerifyAsync(
        ProviderManifest manifest,
        byte[] payloadHash,
        byte[]? publicKey,
        byte[]? signature,
        bool sourceIsTrusted,
        CancellationToken cancellationToken = default);
}
