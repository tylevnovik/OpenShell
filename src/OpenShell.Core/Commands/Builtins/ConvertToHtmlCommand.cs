using System.Runtime.CompilerServices;
using System.Text;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>ConvertTo-Html</c> command. Per ADR-0048 §6.7.
/// <para>
/// Returns HTML representation of objects. <c>-Fragment</c> produces only a <c>&lt;table&gt;</c> snippet.
/// </para>
/// </summary>
[Verb("ConvertTo", Noun = "Html", Aliases = ["html"])]
[Description("Converts objects to HTML format.")]
public sealed class ConvertToHtmlCommand : IPipelineTransform<ConvertToHtmlCommand.Args>
{
    /// <summary>Arguments for <c>ConvertTo-Html</c>.</summary>
    public record Args(
        string[]? Property = null,
        string[]? Head = null,
        string? PreContent = null,
        string? PostContent = null,
        string? Title = null,
        string? Body = null,
        bool Fragment = false,
        string? CssUri = null);

    /// <summary>
    /// Not supported without pipeline input: <c>ConvertTo-Html</c> is pipeline-only.
    /// </summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ConvertTo-Html is pipeline-only, use it after |");

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> Transform(
        IAsyncEnumerable<IItem> input,
        Args args,
        CommandContext ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var items = new List<IItem>();
        await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
            items.Add(item);

        var html = RenderHtml(items, args);

        yield return new Item
        {
            Path = new Paths.ItemPath { Provider = "cli", InternalPath = "ConvertTo-Html" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty.With("Value", html),
        };
    }

    private static string RenderHtml(IReadOnlyList<IItem> items, Args args)
    {
        var sb = new StringBuilder();

        if (!args.Fragment)
        {
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html>");
            sb.AppendLine("<head>");
            if (args.Title is not null)
                sb.AppendLine($"<title>{Escape(args.Title)}</title>");
            if (args.CssUri is not null)
                sb.AppendLine($"<link rel=\"stylesheet\" href=\"{Escape(args.CssUri)}\">");
            if (args.Head is not null)
                foreach (var h in args.Head) sb.AppendLine(h);
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
        }

        if (args.PreContent is not null)
            sb.AppendLine($"<p>{Escape(args.PreContent)}</p>");

        if (items.Count > 0)
        {
            var columns = args.Property ?? GetColumns(items[0]);
            sb.AppendLine("<table>");
            sb.AppendLine("<tr>");
            foreach (var col in columns)
                sb.AppendLine($"<th>{Escape(col)}</th>");
            sb.AppendLine("</tr>");

            foreach (var item in items)
            {
                sb.AppendLine("<tr>");
                foreach (var col in columns)
                {
                    var val = item.Properties[col]?.ToString() ?? "";
                    sb.AppendLine($"<td>{Escape(val)}</td>");
                }
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</table>");
        }

        if (args.PostContent is not null)
            sb.AppendLine($"<p>{Escape(args.PostContent)}</p>");

        if (!args.Fragment)
        {
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
        }

        return sb.ToString();
    }

    private static string[] GetColumns(IItem item)
    {
        return item.Properties.Values.Keys.ToArray();
    }

    private static string Escape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
              .Replace("\"", "&quot;").Replace("'", "&#39;");
}
