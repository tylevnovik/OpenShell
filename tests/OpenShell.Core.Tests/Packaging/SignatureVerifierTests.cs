using System.Security.Cryptography;
using FluentAssertions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using OpenShell.Packaging.Signing;
using OpenShell.Providers;
using Xunit;

namespace OpenShell.Core.Tests.Packaging;

/// <summary>
/// ADR-0039 §8: 签名校验单测。
/// NullSignatureVerifier 默认信任行为 + Ed25519SignatureVerifier 完整往返验签。
/// </summary>
public class SignatureVerifierTests
{
    private static readonly byte[] DummyHash = SHA256.HashData(new byte[] { 1, 2, 3 });

    [Fact]
    public void SignatureResult_HasFourStates()
    {
        Enum.GetNames(typeof(SignatureResult))
            .Should().BeEquivalentTo(new[] { "Valid", "Invalid", "Untrusted", "TrustedSource" });
    }

    [Fact]
    public async Task NullSignatureVerifier_WithSignature_ReturnsValid()
    {
        var v = new NullSignatureVerifier();
        var manifest = new ProviderManifest { Name = "x", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var result = await v.VerifyAsync(manifest, DummyHash, new byte[] { 3, 4 }, new byte[] { 5, 6 }, sourceIsTrusted: false);
        result.Should().Be(SignatureResult.Valid);
    }

    [Fact]
    public async Task NullSignatureVerifier_WithoutSignature_TrustedSource_ReturnsTrustedSource()
    {
        var v = new NullSignatureVerifier();
        var manifest = new ProviderManifest { Name = "x", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var result = await v.VerifyAsync(manifest, DummyHash, publicKey: null, signature: null, sourceIsTrusted: true);
        result.Should().Be(SignatureResult.TrustedSource);
    }

    [Fact]
    public async Task NullSignatureVerifier_WithoutSignature_UntrustedSource_ReturnsUntrusted()
    {
        var v = new NullSignatureVerifier();
        var manifest = new ProviderManifest { Name = "x", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var result = await v.VerifyAsync(manifest, DummyHash, publicKey: null, signature: null, sourceIsTrusted: false);
        result.Should().Be(SignatureResult.Untrusted);
    }

    [Fact]
    public async Task NullSignatureVerifier_NullManifest_Throws()
    {
        var v = new NullSignatureVerifier();
        var act = async () => await v.VerifyAsync(null!, DummyHash, null, null, sourceIsTrusted: false);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task NullSignatureVerifier_NullPayloadHash_Throws()
    {
        var v = new NullSignatureVerifier();
        var manifest = new ProviderManifest { Name = "x", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var act = async () => await v.VerifyAsync(manifest, null!, null, null, sourceIsTrusted: false);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ===== Ed25519 完整往返验签 =====

    /// <summary>生成 Ed25519 密钥对 (32 字节 seed + SPKI 公钥), 供测试使用。</summary>
    private static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var random = new SecureRandom();
        var priv = new Ed25519PrivateKeyParameters(random);
        return (priv.GetEncoded(), priv.GeneratePublicKey().GetEncoded());
    }

    /// <summary>使用私钥对 payloadHash 进行 Ed25519 签名, 返回 64 字节签名。</summary>
    private static byte[] Sign(byte[] privateKey, byte[] payloadHash)
    {
        var privParams = new Ed25519PrivateKeyParameters(privateKey, 0);
        var signer = new Ed25519Signer();
        signer.Init(true, privParams);
        signer.BlockUpdate(payloadHash, 0, payloadHash.Length);
        return signer.GenerateSignature();
    }

    [Fact]
    public async Task Ed25519SignatureVerifier_ValidSignature_ReturnsValid()
    {
        var v = new Ed25519SignatureVerifier();
        var manifest = new ProviderManifest { Name = "x", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var (priv, pub) = GenerateKeyPair();
        var payloadHash = SHA256.HashData(new byte[] { 10, 20, 30 });
        var signature = Sign(priv, payloadHash);

        var result = await v.VerifyAsync(manifest, payloadHash, pub, signature, sourceIsTrusted: false);
        result.Should().Be(SignatureResult.Valid);
    }

    [Fact]
    public async Task Ed25519SignatureVerifier_TamperedPayload_ReturnsInvalid()
    {
        var v = new Ed25519SignatureVerifier();
        var manifest = new ProviderManifest { Name = "x", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var (priv, pub) = GenerateKeyPair();
        var signedHash = SHA256.HashData(new byte[] { 1, 2, 3 });
        var tamperedHash = SHA256.HashData(new byte[] { 9, 9, 9 });
        var signature = Sign(priv, signedHash);

        var result = await v.VerifyAsync(manifest, tamperedHash, pub, signature, sourceIsTrusted: false);
        result.Should().Be(SignatureResult.Invalid);
    }

    [Fact]
    public async Task Ed25519SignatureVerifier_WrongPublicKey_ReturnsInvalid()
    {
        var v = new Ed25519SignatureVerifier();
        var manifest = new ProviderManifest { Name = "x", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var (signingPriv, _) = GenerateKeyPair();
        var (_, verifierPub) = GenerateKeyPair();
        var payloadHash = SHA256.HashData(new byte[] { 42 });
        var signature = Sign(signingPriv, payloadHash);

        var result = await v.VerifyAsync(manifest, payloadHash, verifierPub, signature, sourceIsTrusted: false);
        result.Should().Be(SignatureResult.Invalid);
    }

    [Fact]
    public async Task Ed25519SignatureVerifier_NullSignature_TrustedSource_ReturnsTrustedSource()
    {
        var v = new Ed25519SignatureVerifier();
        var manifest = new ProviderManifest { Name = "x", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var (_, pub) = GenerateKeyPair();

        var result = await v.VerifyAsync(manifest, DummyHash, pub, signature: null, sourceIsTrusted: true);
        result.Should().Be(SignatureResult.TrustedSource);
    }

    [Fact]
    public async Task Ed25519SignatureVerifier_NullSignature_Untrusted_ReturnsUntrusted()
    {
        var v = new Ed25519SignatureVerifier();
        var manifest = new ProviderManifest { Name = "x", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var (_, pub) = GenerateKeyPair();

        var result = await v.VerifyAsync(manifest, DummyHash, pub, signature: null, sourceIsTrusted: false);
        result.Should().Be(SignatureResult.Untrusted);
    }

    [Fact]
    public async Task Ed25519SignatureVerifier_NullPublicKey_TrustedSource_ReturnsTrustedSource()
    {
        var v = new Ed25519SignatureVerifier();
        var manifest = new ProviderManifest { Name = "x", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var signature = new byte[64]; // wrong length but publicKey null short-circuits

        var result = await v.VerifyAsync(manifest, DummyHash, publicKey: null, signature: signature, sourceIsTrusted: true);
        result.Should().Be(SignatureResult.TrustedSource);
    }

    [Fact]
    public async Task Ed25519SignatureVerifier_WrongSignatureLength_ReturnsInvalid()
    {
        var v = new Ed25519SignatureVerifier();
        var manifest = new ProviderManifest { Name = "x", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var (_, pub) = GenerateKeyPair();
        var badSignature = new byte[32]; // Ed25519 签名应为 64 字节

        var result = await v.VerifyAsync(manifest, DummyHash, pub, badSignature, sourceIsTrusted: false);
        result.Should().Be(SignatureResult.Invalid);
    }

    [Fact]
    public async Task Ed25519SignatureVerifier_InvalidPublicKeyFormat_ReturnsInvalid()
    {
        var v = new Ed25519SignatureVerifier();
        var manifest = new ProviderManifest { Name = "x", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var badPubKey = new byte[] { 1, 2, 3 }; // 长度既非 32 字节裸格式也非合法 SPKI
        var signature = new byte[64];

        var result = await v.VerifyAsync(manifest, DummyHash, badPubKey, signature, sourceIsTrusted: false);
        result.Should().Be(SignatureResult.Invalid);
    }

    [Fact]
    public async Task Ed25519SignatureVerifier_RawPublicKeyFormat_ReturnsValid()
    {
        // 32 字节裸公钥格式 (非 SPKI), 应被 ParsePublicKey 接受。
        var v = new Ed25519SignatureVerifier();
        var manifest = new ProviderManifest { Name = "x", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var random = new SecureRandom();
        var privParams = new Ed25519PrivateKeyParameters(random);
        var rawPubKey = privParams.GeneratePublicKey().GetEncoded(); // SPKI 默认
        // 取出 32 字节裸公钥 (SPKI 末尾 32 字节即裸公钥)
        var rawPub = new byte[32];
        Buffer.BlockCopy(rawPubKey, rawPubKey.Length - 32, rawPub, 0, 32);

        var payloadHash = SHA256.HashData(new byte[] { 1, 2, 3, 4 });
        var signer = new Ed25519Signer();
        signer.Init(true, privParams);
        signer.BlockUpdate(payloadHash, 0, payloadHash.Length);
        var signature = signer.GenerateSignature();

        var result = await v.VerifyAsync(manifest, payloadHash, rawPub, signature, sourceIsTrusted: false);
        result.Should().Be(SignatureResult.Valid);
    }

    [Fact]
    public async Task Ed25519SignatureVerifier_NullManifest_Throws()
    {
        var v = new Ed25519SignatureVerifier();
        var act = async () => await v.VerifyAsync(null!, DummyHash, null, null, sourceIsTrusted: false);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Ed25519SignatureVerifier_NullPayloadHash_Throws()
    {
        var v = new Ed25519SignatureVerifier();
        var manifest = new ProviderManifest { Name = "x", Version = "1.0.0", RequiredApiVersion = "1.0.0" };
        var act = async () => await v.VerifyAsync(manifest, null!, null, null, sourceIsTrusted: false);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
