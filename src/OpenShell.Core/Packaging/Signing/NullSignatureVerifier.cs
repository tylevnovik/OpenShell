using OpenShell.Providers;

namespace OpenShell.Packaging.Signing;

/// <summary>
/// 默认 <see cref="ISignatureVerifier"/> 实现: 不做实际签名校验, 信任所有包。Per ADR-0039 §8.
/// <b>仅用于开发/测试</b>: 生产环境必须替换为真正的 Ed25519 校验器。
/// 签名存在时返回 <see cref="SignatureResult.Valid"/>; 缺失时根据 <c>sourceIsTrusted</c> 决定 TrustedSource / Untrusted。
/// </summary>
public sealed class NullSignatureVerifier : ISignatureVerifier
{
    /// <inheritdoc />
    public Task<SignatureResult> VerifyAsync(
        ProviderManifest manifest,
        byte[] payloadHash,
        byte[]? publicKey,
        byte[]? signature,
        bool sourceIsTrusted,
        CancellationToken cancellationToken = default)
    {
        // 空清单视为编程错误 (调用方必须先验证 manifest 存在)。
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payloadHash);
        // TODO(ADR-0039 §8): 生产环境必须替换为真正的 Ed25519 detached signature 校验。
        // 当前实现是占位: 始终信任, 不校验签名内容。
        if (signature is not null && publicKey is not null)
            return Task.FromResult(SignatureResult.Valid);
        return Task.FromResult(sourceIsTrusted ? SignatureResult.TrustedSource : SignatureResult.Untrusted);
    }
}
