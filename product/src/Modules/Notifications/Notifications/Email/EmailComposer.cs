using System.Globalization;
using System.Text;
using System.Text.Json;
using EventTicketing.Notifications.Templates;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using QRCoder;

namespace EventTicketing.Notifications.Email;

/// <summary>
/// Turns a delivery into an email: the HTML from a Razor component (rendered with .NET's
/// <see cref="HtmlRenderer"/>, no template engine), a plain-text part, and for tickets a QR image
/// per ticket, attached inline and referenced by content ID (ADR-0052).
/// </summary>
internal sealed class EmailComposer(IServiceProvider services, ILoggerFactory loggers, IOptions<EmailOptions> options)
{
    public async Task<MimeMessage> ComposeAsync(Delivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        return delivery.Kind switch
        {
            EmailKinds.Tickets => await TicketsAsync(delivery, Model<TicketsEmailModel>(delivery)),
            EmailKinds.Refund => await RefundAsync(delivery, Model<RefundEmailModel>(delivery)),
            _ => throw new InvalidOperationException($"No template for email kind '{delivery.Kind}'."),
        };
    }

    private async Task<MimeMessage> TicketsAsync(Delivery delivery, TicketsEmailModel model)
    {
        var body = new BodyBuilder();
        var images = new Dictionary<Guid, string>();
        foreach (var ticket in model.Tickets)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(ticket.Code, QRCodeGenerator.ECCLevel.M);
            var image = body.LinkedResources.Add($"ticket-{ticket.TicketId:N}.png", new PngByteQRCode(data).GetGraphic(6), new ContentType("image", "png"));
            image.ContentId = MimeKit.Utils.MimeUtils.GenerateMessageId("eventticketing.test");
            images[ticket.TicketId] = image.ContentId;
        }

        body.HtmlBody = await RenderAsync<TicketsEmail>(new Dictionary<string, object?>
        {
            [nameof(TicketsEmail.Model)] = model,
            [nameof(TicketsEmail.QrContentIds)] = images,
            [nameof(TicketsEmail.MyTicketsUrl)] = Link("tickets"),
        });

        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Your tickets for {model.EventName}, {EmailFormats.AtVenue(model.StartsAt, model.TimeZone)}:")
            .AppendLine();
        foreach (var ticket in model.Tickets)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {ticket.Label}");
        }

        text.AppendLine().AppendLine("Show the QR codes at the door (they are in the HTML version of this email, and under My tickets).");
        body.TextBody = text.ToString();

        return Message(delivery, $"Your tickets for {model.EventName}", body);
    }

    private async Task<MimeMessage> RefundAsync(Delivery delivery, RefundEmailModel model)
    {
        var body = new BodyBuilder
        {
            HtmlBody = await RenderAsync<RefundEmail>(new Dictionary<string, object?> { [nameof(RefundEmail.Model)] = model }),
            TextBody = $"{EmailFormats.RefundExplanation(model.Reason)} We refunded {EmailFormats.Money(model.AmountMinorUnits, model.Currency)} for your order for {model.EventName}.",
        };

        return Message(delivery, $"Your payment for {model.EventName} was refunded", body);
    }

    private MimeMessage Message(Delivery delivery, string subject, BodyBuilder body)
    {
        var settings = options.Value;
        var message = new MimeMessage { Subject = subject, Body = body.ToMessageBody() };
        message.From.Add(new MailboxAddress(settings.FromName, settings.From));
        message.To.Add(MailboxAddress.Parse(delivery.Recipient));

        // The delivery's ID as the Message-ID: a second copy after a crash is recognisably the same email.
        message.MessageId = $"{delivery.Id:N}@eventticketing.test";
        return message;
    }

    private async Task<string> RenderAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        await using var renderer = new HtmlRenderer(services, loggers);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private string? Link(string path) =>
        options.Value.FanSiteUrl is { Length: > 0 } site ? $"{site.TrimEnd('/')}/{path}" : null;

    private static T Model<T>(Delivery delivery) =>
        JsonSerializer.Deserialize<T>(delivery.Model, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Delivery {delivery.Id} has no model.");
}
