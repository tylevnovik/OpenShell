using System.Net.Mail;
using System.Runtime.CompilerServices;
using System.Text;
using OpenShell.Items;

namespace OpenShell.Commands.Builtins;

/// <summary>
/// <c>Send-MailMessage</c> command. Per ADR-0048 §9.7.
/// <para>
/// Sends an email via SMTP using <see cref="SmtpClient"/>. <strong>Destructive</strong> —
/// declares <c>[SupportsShouldProcess]</c> per ADR-0049.
/// </para>
/// </summary>
[Verb("Send", Noun = "MailMessage", Aliases = ["send-mail"])]
[SupportsShouldProcess]
[Description("Sends an email message via SMTP.")]
public sealed class SendMailMessageCommand : ICommand<SendMailMessageCommand.Args>
{
    /// <summary>Arguments for <c>Send-MailMessage</c>.</summary>
    public record Args(
        string[]? To = null,
        string[]? Cc = null,
        string[]? Bcc = null,
        [property: Parameter] string? From = null,
        [property: Parameter] string? Subject = null,
        string? Body = null,
        bool BodyAsHtml = false,
        [property: Parameter] string? SmtpServer = null,
        int Port = 25,
        bool UseSsl = false,
        string[]? Attachments = null,
        string Priority = "Normal");

    /// <inheritdoc />
    public async IAsyncEnumerable<IItem> ExecuteAsync(
        Args args, CommandContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(args.From))
            throw new ArgumentException("Send-MailMessage requires -From.");
        if (string.IsNullOrEmpty(args.Subject))
            throw new ArgumentException("Send-MailMessage requires -Subject.");
        if (string.IsNullOrEmpty(args.SmtpServer))
            throw new ArgumentException("Send-MailMessage requires -SmtpServer.");
        if (args.To is null || args.To.Length == 0)
            throw new ArgumentException("Send-MailMessage requires -To.");

        // ShouldProcess gate (per ADR-0049 §8)
        if (!ctx.ShouldProcess($"email to {string.Join(", ", args.To)}", "Send-MailMessage"))
            yield break;

#pragma warning disable CS0618 // SmtpClient is obsolete but acceptable per ADR-0048 §9.7
        using var client = new SmtpClient(args.SmtpServer, args.Port)
        {
            EnableSsl = args.UseSsl,
        };

        using var message = new MailMessage
        {
            From = new MailAddress(args.From!),
            Subject = args.Subject,
            Body = args.Body ?? string.Empty,
            IsBodyHtml = args.BodyAsHtml,
            Priority = args.Priority.ToLowerInvariant() switch
            {
                "high" => MailPriority.High,
                "low" => MailPriority.Low,
                _ => MailPriority.Normal,
            },
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
        };

        foreach (var to in args.To)
            message.To.Add(to);

        if (args.Cc is not null)
            foreach (var cc in args.Cc)
                message.CC.Add(cc);

        if (args.Bcc is not null)
            foreach (var bcc in args.Bcc)
                message.Bcc.Add(bcc);

        if (args.Attachments is not null)
            foreach (var path in args.Attachments)
                message.Attachments.Add(new Attachment(path));
#pragma warning restore CS0618

        try
        {
            await client.SendMailAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Send-MailMessage failed: {ex.Message}", ex);
        }

        yield return new Item
        {
            Path = new Paths.ItemPath { Provider = "cli", InternalPath = "Send-MailMessage" },
            Kind = ItemKind.Property,
            Properties = PropertyBag.Empty.With("Value", "Mail sent"),
        };
    }
}
