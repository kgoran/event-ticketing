using EventTicketing.Notifications.Sending;
using EventTicketing.Testing;
using MimeKit;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// M1b N1 (ADR-0052): the fan gets their tickets by email, with a QR image per ticket; an event
/// delivered again sends nothing twice; a mail server that is down only delays the email.
/// </summary>
public sealed class NotificationsTests(TestSystem system)
{
    [Fact]
    public async Task The_buyer_gets_their_tickets_by_email_with_a_qr_image_for_each()
    {
        var (fanId, orderId) = await PaidOrderAsync();

        var email = system.Mail.SentTo(TestTokens.EmailOf(fanId)).Should().ContainSingle().Subject;
        email.Subject.Should().Be("Your tickets for Test concert");
        System.Net.WebUtility.HtmlDecode(email.HtmlBody).Should()
            .Contain("A · Row 1 · Seat 1").And.Contain("A · Row 1 · Seat 2").And.Contain("http://fansite.test/tickets");
        email.TextBody.Should().Contain("A · Row 1 · Seat 1");

        // One inline PNG per ticket, each referenced from the HTML by its content ID.
        var images = email.BodyParts.OfType<MimePart>().Where(part => part.ContentType.MimeType == "image/png").ToList();
        images.Should().HaveCount(2);
        images.Should().OnlyContain(image => email.HtmlBody!.Contains("cid:" + image.ContentId, StringComparison.Ordinal));

        (await system.ScalarAsync<string>("SELECT status FROM notifications.deliveries WHERE order_id = @p0 AND kind = 'tickets'", orderId))
            .Should().Be("Sent");
    }

    [Fact]
    public async Task Tickets_issued_again_send_no_second_email()
    {
        var (fanId, orderId) = await PaidOrderAsync();

        // The same TicketsIssued under a new message ID: past the inbox, so the delivery log has to catch it.
        await system.QueryAsync<int>(
            """
            INSERT INTO ticketing.outbox_messages (id, type, version, payload, headers, occurred_at, attempts)
            SELECT gen_random_uuid(), type, version, payload, headers, occurred_at, 0
            FROM ticketing.outbox_messages WHERE type = 'ticketing.tickets-issued' AND payload->>'orderId' = @p0
            RETURNING 1
            """,
            orderId.ToString());
        await system.DeliverMessagesAsync();

        system.Mail.SentTo(TestTokens.EmailOf(fanId)).Should().ContainSingle();
    }

    [Fact]
    public async Task An_email_the_mail_server_refused_goes_out_once_it_is_back()
    {
        var fanId = "fan-" + Guid.NewGuid().ToString("N");
        system.Mail.MakeUnreachableFor(TestTokens.EmailOf(fanId));
        var (_, orderId) = await PaidOrderAsync(fanId);

        system.Mail.SentTo(TestTokens.EmailOf(fanId)).Should().BeEmpty();
        (await system.ScalarAsync<string>("SELECT status FROM notifications.deliveries WHERE order_id = @p0", orderId)).Should().Be("Pending");

        system.Mail.MakeReachableFor(TestTokens.EmailOf(fanId));
        (await system.MakeDueAsync("notifications", typeof(SendEmail), orderId)).Should().Be(1);
        await system.DeliverMessagesAsync();

        system.Mail.SentTo(TestTokens.EmailOf(fanId)).Should().ContainSingle();
        (await system.ScalarAsync<string>("SELECT status FROM notifications.deliveries WHERE order_id = @p0", orderId)).Should().Be("Sent");
    }

    [Fact]
    public async Task A_refunded_fan_is_told_why_and_how_much()
    {
        var sale = await system.OnSaleAsync();
        var fanId = "fan-" + Guid.NewGuid().ToString("N");
        using var fan = system.PublicApi(Callers.Fan(fanId));
        var hold = await (await fan.HoldAsync(sale.EventId, sale.Seats[0])).PlacedAsync();
        var checkout = await fan.CheckoutAsync(hold.HoldId);

        // The hold ran out before the payment was confirmed, and someone else took the seat.
        var payment = system.LocalPay.PaymentOf(checkout.OrderId);
        system.LocalPay.Settle(payment, "succeeded");
        await system.ExpireHoldAsync(hold.HoldId);
        using var other = system.PublicApi(Callers.Fan());
        await (await other.HoldAsync(sale.EventId, sale.Seats[0])).PlacedAsync();
        await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();

        var email = system.Mail.SentTo(TestTokens.EmailOf(fanId)).Should().ContainSingle().Subject;
        email.Subject.Should().Be("Your payment for Test concert was refunded");
        System.Net.WebUtility.HtmlDecode(email.HtmlBody).Should().Contain("25.00 EUR").And.Contain("someone else took a seat");
    }

    private async Task<(string FanId, Guid OrderId)> PaidOrderAsync(string? fanId = null)
    {
        var sale = await system.OnSaleAsync();
        fanId ??= "fan-" + Guid.NewGuid().ToString("N");
        using var fan = system.PublicApi(Callers.Fan(fanId));
        var hold = await (await fan.HoldAsync(sale.EventId, sale.Seats[0], sale.Seats[1])).PlacedAsync();
        var checkout = await fan.CheckoutAsync(hold.HoldId);

        var payment = system.LocalPay.PaymentOf(checkout.OrderId);
        system.LocalPay.Settle(payment, "succeeded");
        await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();
        return (fanId, checkout.OrderId);
    }
}
