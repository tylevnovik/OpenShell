using OpenShell.Paths;

namespace OpenShell.Providers;

/// <summary>
/// Per ADR-0018 §8 + ADR-0023: 写入 / 删除 / 清除 item 的单个属性。
/// Registry provider 通过此接口实现 <c>Set-ItemProperty</c> / <c>Remove-ItemProperty</c> /
/// <c>Clear-ItemProperty</c> / <c>New-ItemProperty</c>。
/// FileSystem 等其他 provider 可选实现 (用于写文件属性如 ReadOnly/Hidden)。
/// </summary>
public interface IPropertyWriterProvider
{
    /// <summary>
    /// 设置指定名称的属性值。若属性不存在则创建 (per ADR-0018 §8: <c>SetValue</c> 语义)。
    /// </summary>
    /// <param name="path">目标 item 路径。</param>
    /// <param name="name">属性名 (Registry value name; 空字符串表示 (default) value)。</param>
    /// <param name="value">属性值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask SetPropertyAsync(
        ItemPath path,
        string name,
        object? value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除指定名称的属性。若属性不存在则静默返回 (幂等, per ADR-0018 §8)。
    /// </summary>
    /// <param name="path">目标 item 路径。</param>
    /// <param name="name">属性名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask RemovePropertyAsync(
        ItemPath path,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 清除指定名称的属性值 (置为类型默认值, 不删除属性名)。
    /// Registry: value 置为 null 或空字符串; FS: 属性重置为默认。
    /// </summary>
    /// <param name="path">目标 item 路径。</param>
    /// <param name="name">属性名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask ClearPropertyAsync(
        ItemPath path,
        string name,
        CancellationToken cancellationToken = default);
}
