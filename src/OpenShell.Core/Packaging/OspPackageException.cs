namespace OpenShell.Packaging;

/// <summary>
/// .osp 包格式异常。Per ADR-0039 §1 / §10.
/// 在打包、解包、清单读写、签名校验过程中检测到包格式不合法时抛出。
/// </summary>
public sealed class OspPackageException : Exception
{
    public OspPackageException(string message) : base(message) { }
    public OspPackageException(string message, Exception inner) : base(message, inner) { }
}
