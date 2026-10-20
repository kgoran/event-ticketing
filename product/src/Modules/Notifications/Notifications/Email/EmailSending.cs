using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace EventTicketing.Notifications.Email;

/// <summary>Configuration section "Email" (ADR-0052).</summary>
public sealed class EmailOptions
{
    /// <summary>The SMTP server: Mailpit locally (it catches every email and shows it in a web inbox).</summary>
    public string SmtpHost { get; set; } = "localhost";

    public int SmtpPort { get; set; } = 1025;

    /// <summary>STARTTLS for a real server; Mailpit on localhost needs none.</summary>
    public bool UseTls { get; set; }

    public string From { get; set; } = "tickets@eventticketing.test";

    public string FromName { get; set; } = "EventTicketing";

    /// <summary>The fan site's address, for links in emails; no links when unset.</summary>
    public string? FanSiteUrl { get; set; }
}

/// <summary>Sends one email. Not transactional: the caller decides what happens if it fails or runs twice.</summary>
public interface IEmailSender
{
    Task SendAsync(MimeMessage message, CancellationToken cancellationToken);
}

/// <summary>SMTP through MailKit (ADR-0052): a connection per email, which is plenty at this volume.</summary>
internal sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(settings.SmtpHost, settings.SmtpPort, settings.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, cancellationToken);
        await smtp.SendAsync(message, cancellationToken);
        await smtp.DisconnectAsync(quit: true, cancellationToken);
    }
}
