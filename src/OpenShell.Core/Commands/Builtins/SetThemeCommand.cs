using System.Runtime.CompilerServices;
using OpenShell.Errors;
using OpenShell.Help;
using OpenShell.Items;
using OpenShell.Themes;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// Built-in Set-Theme command. Per ADR-0027 section 1.
/// Sets the active GUI theme by name. Built-in themes: light, dark, highcontrast.
/// User themes loaded from the themes directory are also available.
/// </summary>
[Verb("Set", Noun = "Theme", Aliases = ["set-theme"])]
[Description("Sets the active GUI theme.")]
[Help(
    Synopsis = "Sets the active GUI theme by name (case-insensitive).",
    Examples = new[]
    {
        "set-theme dark            # use the dark theme",
        "set-theme light           # use the light theme",
        "set-theme highcontrast    # use the high contrast theme",
    },
    RelatedLinks = new[] { "get-config" })]
public sealed class SetThemeCommand : ICommand<SetThemeCommand.Args>
{
    /// <summary>Arguments for Set-Theme.</summary>
    /// <param name="Name">Theme name (e.g. dark, light, highcontrast).</param>
    public record Args(
        [property: Parameter(Position = 0, Mandatory = true)] string Name);

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var themeService = ctx.Host.Services.GetService(typeof(IThemeService)) as IThemeService;
        if (themeService is null)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.NotImplemented,
                Message = "Theme service is not available in this context.",
                Operation = "set-theme",
                Phase = ErrorPhase.Operation,
            });
            yield break;
        }

        try
        {
            themeService.Apply(args.Name);
        }
        catch (ArgumentException ex)
        {
            ctx.Errors?.Write(new ErrorRecord
            {
                Category = ErrorCategory.InvalidArgument,
                Message = ex.Message,
                Operation = "set-theme",
                Phase = ErrorPhase.ArgumentBinding,
            });
            yield break;
        }

        await ctx.Host.WriteOutputLineAsync(
            $"Theme set to '{themeService.Current.Name}'.", ct).ConfigureAwait(false);

        yield break;
    }
}
