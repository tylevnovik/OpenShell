namespace OpenShell.I18n;

/// <summary>
/// i18n 服务静态访问器。Per i18n 改造 T-311.
/// 供无法通过 DI 注入的组件 (如 Avalonia IValueConverter) 访问 <see cref="II18nService"/>。
/// App 启动时由 host 设置 <see cref="Instance"/>; 转换器读取 <see cref="Instance"/> 翻译。
/// </summary>
public static class I18nAccessor
{
    /// <summary>
    /// 当前 i18n 服务实例。App 启动时设置; 未设置时 Translate 回退到 key 本身。
    /// </summary>
    public static II18nService? Instance { get; set; }

    /// <summary>
    /// 翻译指定 key。Instance 未设置时返回 key 本身 (graceful degradation)。
    /// </summary>
    /// <param name="key">翻译 key。</param>
    /// <returns>翻译后的字符串; Instance 未设置时返回 key。</returns>
    public static string Translate(string key)
        => Instance?.Translate(key) ?? key;

    /// <summary>
    /// 翻译指定 key 并格式化。Instance 未设置时返回 key 本身。
    /// </summary>
    /// <param name="key">翻译 key。</param>
    /// <param name="args">格式化参数。</param>
    /// <returns>翻译并格式化后的字符串; Instance 未设置时返回 key。</returns>
    public static string Translate(string key, params object[] args)
        => Instance?.Translate(key, args) ?? key;
}
