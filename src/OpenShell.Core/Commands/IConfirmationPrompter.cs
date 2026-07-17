using OpenShell.I18n;

namespace OpenShell.Commands;

/// <summary>
/// 用户在 [Y] Yes / [A] Yes to All / [N] No / [L] No to All / [S] Suspend / [?] Help 提示中的选择。
/// Per ADR-0049 §3.2 / §9 / §10.
/// </summary>
public enum ConfirmationChoice
{
    /// <summary>本次 Yes。</summary>
    Yes,

    /// <summary>本次及本会话后续全部 Yes (设 YesToAll)。</summary>
    YesToAll,

    /// <summary>本次 No。</summary>
    No,

    /// <summary>本次及本会话后续全部 No (设 NoToAll)。</summary>
    NoToAll,

    /// <summary>挂起到嵌套 REPL (Per ADR-0049 §10: CLI host 进入嵌套循环, GUI host 降级为 No)。</summary>
    Suspend,

    /// <summary>请求帮助 (循环提示, 不退出)。</summary>
    Help,
}

/// <summary>
/// Per-ADR-0049 §3.2: abstracts the Y/A/N/L/S/? interactive prompt so the
/// <see cref="IShouldProcessService"/> can be unit-tested without the console.
/// CLI host uses <see cref="ConsoleConfirmationPrompter"/>; GUI host can later
/// swap in a dialog-based prompter.
/// </summary>
public interface IConfirmationPrompter
{
    /// <summary>
    /// Prompt the user with [Y] Yes / [A] Yes to All / [N] No / [L] No to All / [S] Suspend / [?] Help.
    /// </summary>
    /// <param name="target">Human-readable description of the operation target.</param>
    /// <param name="action">Human-readable description of the operation action.</param>
    /// <param name="yesToAll">Set to <c>true</c> when user picks "Yes to All".</param>
    /// <param name="noToAll">Set to <c>true</c> when user picks "No to All".</param>
    /// <returns><c>true</c> for Yes / Yes to All; <c>false</c> for No / No to All / Suspend (降级为 No, Per ADR-0049 §10 GUI 路径)。</returns>
    bool PromptYesNoAll(string target, string action, out bool yesToAll, out bool noToAll);

    /// <summary>
    /// Prompt the user and返回完整选择 (含 Suspend)。Per ADR-0049 §3.2 / §10.
    /// 调用方应根据返回值处理 Suspend: 若 <see cref="SuspendCallback"/> 已设置则调用它进入嵌套 REPL, 然后重新提示;
    /// 否则降级为 <see cref="ConfirmationChoice.No"/> (Per ADR-0049 §10: GUI host / 无嵌套 REPL 能力时)。
    /// </summary>
    ConfirmationChoice Prompt(string target, string action);

    /// <summary>
    /// 可选的 Suspend 回调。CLI host 设置为进入嵌套 REPL 的委托; null 时 Suspend 降级为 No。
    /// Per ADR-0049 §10. 回调返回时控制流回到提示器, 重新显示 [Y/A/N/L/S/?]。
    /// </summary>
    Action<string, string>? SuspendCallback { get; set; }
}

/// <summary>
/// Default CLI prompter. Reads from <see cref="Console.In"/> and writes to
/// <see cref="Console.Error"/>. Per ADR-0049 §9. Mirrors PowerShell's
/// "Confirm" prompt format. Per i18n 改造 T-303: 支持通过 <see cref="II18nService"/> 翻译提示文本。
/// </summary>
public sealed class ConsoleConfirmationPrompter : IConfirmationPrompter
{
    private readonly II18nService? _i18n;

    /// <summary>
    /// 构造 ConsoleConfirmationPrompter。
    /// </summary>
    /// <param name="i18n">可选的 i18n 服务。未提供时使用硬编码英文 (向后兼容)。</param>
    public ConsoleConfirmationPrompter(II18nService? i18n = null)
    {
        _i18n = i18n;
    }

    /// <inheritdoc />
    public Action<string, string>? SuspendCallback { get; set; }

    /// <summary>翻译 key; i18n 未注入时回退到 fallback 英文。</summary>
    private string T(string key) => _i18n?.Translate(key) ?? key;

    /// <summary>翻译带参数的 key。</summary>
    private string T(string key, params object[] args) => _i18n?.Translate(key, args) ?? key;

    /// <inheritdoc />
    public bool PromptYesNoAll(string target, string action, out bool yesToAll, out bool noToAll)
    {
        yesToAll = false;
        noToAll = false;

        // 循环: Suspend 回调返回后重新提示, 直到用户给出 Y/A/N/L。
        while (true)
        {
            var choice = Prompt(target, action);
            switch (choice)
            {
                case ConfirmationChoice.Yes:
                    return true;
                case ConfirmationChoice.YesToAll:
                    yesToAll = true;
                    return true;
                case ConfirmationChoice.No:
                    return false;
                case ConfirmationChoice.NoToAll:
                    noToAll = true;
                    return false;
                case ConfirmationChoice.Suspend:
                    // SuspendCallback 应已在 Prompt 内被调用; 这里若仍收到 Suspend, 降级为 No。
                    return false;
            }
        }
    }

    /// <inheritdoc />
    public ConfirmationChoice Prompt(string target, string action)
    {
        Console.Error.WriteLine(T("confirm.title"));
        Console.Error.WriteLine(T("confirm.areYouSure"));
        Console.Error.WriteLine(T("confirm.performing", action, target));
        Console.Error.WriteLine(T("confirm.choices"));

        while (true)
        {
            var line = Console.ReadLine();
            // D-311: stdin 到达 EOF（重定向/非交互模式）时 ReadLine 返回 null。
            // 此时无法获取用户输入，降级为 No（拒绝操作，最安全），避免无限循环。
            if (line is null)
            {
                Console.Error.WriteLine(T("confirm.noInput"));
                return ConfirmationChoice.No;
            }
            var input = line.Trim().ToUpperInvariant();
            switch (input)
            {
                case "" or "Y":
                    return ConfirmationChoice.Yes;
                case "A":
                    return ConfirmationChoice.YesToAll;
                case "N":
                    return ConfirmationChoice.No;
                case "L":
                    return ConfirmationChoice.NoToAll;
                case "S":
                    // Per ADR-0049 §10: Suspend 进入嵌套 REPL。若 SuspendCallback 已设置则调用它,
                    // 回调返回后重新显示提示; 否则降级为 No (GUI host / 无 REPL 能力时)。
                    if (SuspendCallback is not null)
                    {
                        try
                        {
                            SuspendCallback.Invoke(target, action);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine(T("confirm.suspendFailed", ex.Message));
                            return ConfirmationChoice.No;
                        }
                        // 嵌套 REPL 退出后, 重新显示提示让用户决定 Y/N。
                        Console.Error.WriteLine(T("confirm.resuming"));
                        Console.Error.WriteLine(T("confirm.choices"));
                        continue;
                    }
                    // 无 SuspendCallback: 降级为 No (Per ADR-0049 §10 GUI 路径)。
                    Console.Error.WriteLine(T("confirm.suspendUnavailable"));
                    return ConfirmationChoice.No;
                case "?":
                    Console.Error.WriteLine(T("confirm.help"));
                    continue;
                default:
                    Console.Error.WriteLine(T("confirm.invalidInput"));
                    continue;
            }
        }
    }
}
