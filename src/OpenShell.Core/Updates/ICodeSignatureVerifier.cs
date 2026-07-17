namespace OpenShell.Updates;

/// <summary>
/// 代码签名 (Authenticode / Developer ID) 校验器抽象。Per ADR-0037 §5.
/// 在 SHA256 校验通过后, 由 <see cref="IUpdateService.DownloadAsync"/> 调用,
/// 拒绝未签名或签名无效的更新包 (除非企业策略显式放宽)。
/// </summary>
public interface ICodeSignatureVerifier
{
    /// <summary>
    /// 校验文件的平台代码签名。Per ADR-0037 §5.
    /// </summary>
    /// <param name="filePath">待校验的本地文件绝对路径 (已通过 SHA256 校验)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true 表示签名有效且受信任; false 表示未签名/签名无效/不受信任。</returns>
    Task<bool> VerifyAsync(string filePath, CancellationToken cancellationToken = default);
}
