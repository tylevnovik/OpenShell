namespace OpenShell.I18n;

/// <summary>
/// 国际化服务抽象。Per ADR-0035.
/// 负责加载翻译资源, 提供 <c>Translate(key, args)</c> 翻译接口。
/// 默认实现从 <c>~/.openshell/locales/{locale}.json</c> 加载用户翻译并与内置表合并。
/// fallback 链: 当前 locale → en-US → key 本身。
/// </summary>
public interface II18nService
{
    /// <summary>当前 locale (如 "en-US" / "zh-CN")。默认 "en-US"。通过 <see cref="SetLocale"/> 切换。</summary>
    string CurrentLocale { get; }

    /// <summary>可用 locale 列表。包含内置 locale 与用户 locale 文件中发现的 locale。</summary>
    IReadOnlyList<string> AvailableLocales { get; }

    /// <summary>
    /// 翻译 key。按 fallback 链查找: 当前 locale → en-US → key 本身。
    /// 不带参数插值, 返回原始模板 (可能含 <c>{0}</c> 占位符)。
    /// </summary>
    /// <param name="key">翻译键 (dotted path, 如 "commands.copy-item.description")。</param>
    string Translate(string key);

    /// <summary>
    /// 翻译 key 并用 <c>string.Format</c> 插值参数。
    /// 若 key 未找到则直接返回 key 本身 (不做格式化)。
    /// </summary>
    /// <param name="key">翻译键。</param>
    /// <param name="args">格式化参数。</param>
    string Translate(string key, params object[] args);

    /// <summary>
    /// 切换当前 locale。若该 locale 尚未从磁盘加载, 则尝试加载
    /// <c>~/.openshell/locales/{locale}.json</c> (用户文件覆盖内置表)。
    /// 文件缺失或非法时静默降级到内置表。切换后触发 <see cref="LocaleChanged"/> 事件。
    /// </summary>
    /// <param name="locale">目标 locale (如 "zh-CN")。</param>
    void SetLocale(string locale);

    /// <summary>
    /// 显式加载 (或重新加载) 指定 locale 的用户翻译文件。
    /// 文件格式: JSON 对象 <c>{ "key": "translated value" }</c>。
    /// 用户条目覆盖内置同名条目; 文件缺失或非法 JSON 时静默降级, 不抛异常。
    /// </summary>
    /// <param name="locale">locale 名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task LoadLocaleAsync(string locale, CancellationToken cancellationToken = default);

    /// <summary>当 <see cref="SetLocale"/> 成功切换 locale 后触发, 参数为新 locale。</summary>
    event EventHandler<string>? LocaleChanged;
}
