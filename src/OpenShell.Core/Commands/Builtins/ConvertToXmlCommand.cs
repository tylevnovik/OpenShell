using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using OpenShell.Items;
using OpenShell.Pipeline;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>ConvertTo-Xml</c> command. Per ADR-0048 §6.8.
/// <para>
/// Returns XML representation of objects. Default <c>-As Stream</c> emits CLIXML-style lines.
/// <c>-As String</c> returns a single XML string. <c>-NoTypeInformation</c> omits type attributes.
/// </para>
/// </summary>
[Verb("ConvertTo", Noun = "Xml", Aliases = ["xml"])]
[Description("Converts objects to XML format.")]
public sealed class ConvertToXmlCommand : IPipelineTransform<ConvertToXmlCommand.Args>
{
    /// <summary>Arguments for <c>ConvertTo-Xml</c>.</summary>
    public record Args(
        int Depth = 1,
        string As = "Stream",
        bool NoTypeInformation = false);

    /// <summary>
    /// Not supported without pipeline input: <c>ConvertTo-Xml</c> is pipeline-only.
    /// </summary>
    public IAsyncEnumerable<IItem> ExecuteAsync(Args args, CommandContext ctx, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ConvertTo-Xml is pipeline-only, use it after |");

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

        var xml = RenderXml(items, args);

        if (string.Equals(args.As, "Stream", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in xml.Split('\n'))
            {
                yield return new Item
                {
                    Path = new Paths.ItemPath { Provider = "cli", InternalPath = "ConvertTo-Xml" },
                    Kind = ItemKind.Property,
                    Properties = PropertyBag.Empty.With("Value", line),
                };
            }
        }
        else
        {
            yield return new Item
            {
                Path = new Paths.ItemPath { Provider = "cli", InternalPath = "ConvertTo-Xml" },
                Kind = ItemKind.Property,
                Properties = PropertyBag.Empty.With("Value", xml),
            };
        }
    }

    private static string RenderXml(IReadOnlyList<IItem> items, Args args)
    {
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
        }))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("Objects");

            if (!args.NoTypeInformation)
                writer.WriteAttributeString("Type", "System.Object[]");

            foreach (var item in items)
            {
                writer.WriteStartElement("Object");
                if (!args.NoTypeInformation)
                    writer.WriteAttributeString("Type", item.Kind.ToString());

                foreach (var prop in item.Properties.Values)
                {
                    writer.WriteStartElement("Property");
                    writer.WriteAttributeString("Name", prop.Key);
                    writer.WriteValue(prop.Value?.ToString() ?? "");
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return sb.ToString();
    }
}
