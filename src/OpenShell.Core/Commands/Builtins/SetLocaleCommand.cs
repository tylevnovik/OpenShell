using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.I18n;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in Set-Locale command. Per ADR-0035.
/// Switches the active UI locale. Built-in locales: en-US, zh-CN, ja-JP.
/// User locale files (<c>~/.openshell/locales/{locale}.json</c>) extend the available set.
/// </summary>
[Verb("Set", Noun = "Locale", Aliases = ["set-locale"])]
[Description("Sets the active UI locale (e.g. en-US, zh-CN, ja-JP).")]
[Help(
    Synopsis = "Sets the active UI locale by name (case-insensitive).",
    Examples = new[]
    {
        "set-locale en-US        # use English (United States)",
        "set-locale zh-CN        # use Simplified Chinese",
        "set-locale ja-JP        # use Japanese",
    },
    RelatedLinks = new[] { "get-config" })]
public sealed class SetLocaleCommand : ICommand<SetLocaleCommand.Args>
{
    /// <summary>Arguments for Set-Locale.</summary>
    /// <param name="Locale">Locale tag (BCP 47, e.g. en-US, zh-CN, ja-JP).</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Locale);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var i18n = ctx.Host.Services.GetService(typeof(II18nService)) as II18nService;
        if (i18n is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "I18n service is not available in this context.",
                Operation = "set-locale",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        // 大小写不敏感匹配, 采用 AvailableLocales 中的规范大小写。
        var match = i18n.AvailableLocales.FirstOrDefault(
            l => string.Equals(l, args.Locale, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = $"Locale '{args.Locale}' is not available. Available: {string.Join(", ", i18n.AvailableLocales)}",
                Operation = "set-locale",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        i18n.SetLocale(match);

        await ctx.Host.WriteOutputLineAsync(
            $"Locale set to '{i18n.CurrentLocale}'.", ct).ConfigureAwait(false);

        yield break;
    }
}
