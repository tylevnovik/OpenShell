using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using OpenShell.Providers;

namespace OpenShell.Packaging.Signing;

/// <summary>
/// Ed25519 detached signature 校验器。Per ADR-0039 §8.
/// 使用 BouncyCastle 提供 Ed25519 支持 (.NET 8 BCL 不含)。
/// 签名载荷 = SHA256(manifest JSON + 每个 DLL 的 SHA256), 由调用方通过
/// <c>OspPackage.ComputePayloadHashAsync</c> 计算后传入 <c>payloadHash</c> 参数。
/// 公钥接受 32 字节裸 Ed25519 公钥或 DER-encoded SubjectPublicKeyInfo (SPKI)。
/// 签名为 64 字节 Ed25519 detached signature。
/// </summary>
public sealed class Ed25519SignatureVerifier : ISignatureVerifier
{
    private const int RawPublicKeyLength = 32;
    private const int SignatureLength = 64;

    /// <inheritdoc />
    public Task<SignatureResult> VerifyAsync(
        ProviderManifest manifest,
        byte[] payloadHash,
        byte[]? publicKey,
        byte[]? signature,
        bool sourceIsTrusted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payloadHash);

        // 未签名包: 根据 sourceIsTrusted 决定结果。
        if (signature is null || publicKey is null)
            return Task.FromResult(sourceIsTrusted ? SignatureResult.TrustedSource : SignatureResult.Untrusted);

        // 公钥解析: 裸 32 字节或 SPKI。
        Ed25519PublicKeyParameters pubKeyParams;
        try
        {
            pubKeyParams = ParsePublicKey(publicKey);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(SignatureResult.Invalid);
        }

        // 签名长度校验: Ed25519 签名固定 64 字节。
        if (signature.Length != SignatureLength)
            return Task.FromResult(SignatureResult.Invalid);

        // Ed25519 验签: 以 payloadHash 作为消息 (Ed25519 内部会做 SHA-512 hashing)。
        var verifier = new Ed25519Signer();
        verifier.Init(false, pubKeyParams);
        verifier.BlockUpdate(payloadHash, 0, payloadHash.Length);
        var valid = verifier.VerifySignature(signature);

        return Task.FromResult(valid ? SignatureResult.Valid : SignatureResult.Invalid);
    }

    /// <summary>
    /// 解析 Ed25519 公钥。支持两种格式:
    /// <list type="bullet">
    ///   <item>32 字节裸 Ed25519 公钥</item>
    ///   <item>DER-encoded SubjectPublicKeyInfo (SPKI, 由 BouncyCastle 解析)</item>
    /// </list>
    /// </summary>
    private static Ed25519PublicKeyParameters ParsePublicKey(byte[] publicKey)
    {
        if (publicKey.Length == RawPublicKeyLength)
            return new Ed25519PublicKeyParameters(publicKey, 0);

        // SPKI 格式: 委托 BouncyCastle 解析。
        var key = PublicKeyFactory.CreateKey(publicKey);
        if (key is Ed25519PublicKeyParameters ed25519Key)
            return ed25519Key;

        throw new ArgumentException(
            $"Public key is not an Ed25519 key (got {key.GetType().Name}).",
            nameof(publicKey));
    }
}
