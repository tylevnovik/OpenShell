namespace OpenShell.Updates;

/// <summary>
/// 自动更新状态机。Per ADR-0037 §1.
/// 通过 <see cref="IUpdateService.StatusChanged"/> 推送给订阅者。
/// </summary>
public enum UpdateStatus
{
    /// <summary>无任务/初始状态。</summary>
    Idle,

    /// <summary>正在检查更新。</summary>
    Checking,

    /// <summary>已发现可用更新。</summary>
    UpdateAvailable,

    /// <summary>正在下载。</summary>
    Downloading,

    /// <summary>正在做 SHA256 / 签名校验。</summary>
    Verifying,

    /// <summary>下载校验完成，等待用户确认安装。</summary>
    ReadyToInstall,

    /// <summary>正在安装 (替换二进制)。</summary>
    Installing,

    /// <summary>安装完成。</summary>
    Installed,

    /// <summary>任意阶段失败。</summary>
    Failed,
}
