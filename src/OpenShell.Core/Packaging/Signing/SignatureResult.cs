namespace OpenShell.Packaging.Signing;

/// <summary>
/// .osp 包签名校验结果。Per ADR-0039 §8.
/// </summary>
public enum SignatureResult
{
    /// <summary>签名存在且校验通过, 公钥被信任。</summary>
    Valid,

    /// <summary>签名不匹配 / 公钥不正确 / 包内容被篡改。</summary>
    Invalid,

    /// <summary>包未签名或公钥未被信任 (需用户 -TrustKey 显式信任)。</summary>
    Untrusted,

    /// <summary>来自受信任注册源 (trusted=true), 签名校验被放宽。等价于 Valid 但语义区分用于审计。</summary>
    TrustedSource,
}
