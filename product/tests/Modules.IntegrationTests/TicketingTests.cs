using System.Net.Http.Json;
using System.Security.Cryptography;
using EventTicketing.Testing;
using EventTicketing.Ticketing.Infrastructure.Signing;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// M1b TK1 (ADR-0051): a confirmed order gets one ticket per place, each with a code signed by the
/// event's key that any JOSE library can verify; nothing is issued twice; private keys are stored
/// only encrypted.
/// </summary>
public sealed class TicketingTests(TestSystem system)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_paid_order_gets_a_ticket_per_seat_that_anyone_can_verify_with_the_events_public_keys()
    {
        var (sale, fanId, orderId) = await PaidOrderAsync();

        var tickets = await TicketsAsync(orderId);
        tickets.Select(t => t.SeatId).Should().BeEquivalentTo(sale.Seats.Take(2));
        tickets.Select(t => t.Label).Should().BeEquivalentTo("A · Row 1 · Seat 1", "A · Row 1 · Seat 2");

        // A standard library, given only the published JSON Web Key Set, accepts every code.
        var keys = await PublicKeysAsync(sale.EventId);
        foreach (var ticket in tickets)
        {
            var result = await VerifyAsync(ticket.Code, keys);
            result.IsValid.Should().BeTrue(result.Exception?.Message);
            result.Claims["tid"].Should().Be(ticket.Id.ToString());
            result.Claims["eid"].Should().Be(sale.EventId.ToString());
            Convert.ToInt32(result.Claims["ver"], System.Globalization.CultureInfo.InvariantCulture).Should().Be(1);
        }

        // TicketsIssued carries what Notifications needs to email them, codes included.
        (await system.ScalarAsync<long>(
            "SELECT count(*) FROM ticketing.outbox_messages WHERE type = 'ticketing.tickets-issued' AND payload->>'orderId' = @p0", orderId.ToString()))
            .Should().Be(1);
        (await system.ScalarAsync<string>(
            "SELECT payload->>'customerEmail' FROM ticketing.outbox_messages WHERE type = 'ticketing.tickets-issued' AND payload->>'orderId' = @p0", orderId.ToString()))
            .Should().Be(TestTokens.EmailOf(fanId));
        (await system.ScalarAsync<long>(
            "SELECT jsonb_array_length(payload->'tickets') FROM ticketing.outbox_messages WHERE type = 'ticketing.tickets-issued' AND payload->>'orderId' = @p0", orderId.ToString()))
            .Should().Be(2);
    }

    [Fact]
    public async Task A_code_with_a_changed_payload_fails_verification()
    {
        var (sale, _, orderId) = await PaidOrderAsync();
        var code = (await TicketsAsync(orderId))[0].Code;

        // Point the ticket at another event: the signature no longer matches.
        var parts = code.Split('.');
        var payload = System.Text.Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1]))
            .Replace(sale.EventId.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal);
        var forged = $"{parts[0]}.{Base64UrlEncoder.Encode(payload)}.{parts[2]}";

        (await VerifyAsync(forged, await PublicKeysAsync(sale.EventId))).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task The_same_confirmation_published_again_issues_no_more_tickets()
    {
        var (_, _, orderId) = await PaidOrderAsync();

        // A second OrderConfirmed for the order under a new message ID: the inbox can't recognise
        // it, so Ticketing's own check has to.
        await system.QueryAsync<int>(
            """
            INSERT INTO orders.outbox_messages (id, type, version, payload, headers, occurred_at, attempts)
            SELECT gen_random_uuid(), type, version, payload, headers, occurred_at, 0
            FROM orders.outbox_messages WHERE type = 'orders.order-confirmed' AND payload->>'orderId' = @p0
            RETURNING 1
            """,
            orderId.ToString());
        await system.DeliverMessagesAsync();

        (await TicketsAsync(orderId)).Should().HaveCount(2);
        (await system.ScalarAsync<long>(
            "SELECT count(*) FROM ticketing.outbox_messages WHERE type = 'ticketing.tickets-issued' AND payload->>'orderId' = @p0", orderId.ToString()))
            .Should().Be(1);
    }

    [Fact]
    public async Task Private_keys_are_stored_only_encrypted_under_the_master_key()
    {
        var (sale, _, _) = await PaidOrderAsync();
        var keyId = await system.ScalarAsync<string>("SELECT key_id FROM ticketing.event_keys WHERE event_id = @p0", sale.EventId);
        var stored = await system.ScalarAsync<byte[]>("SELECT encrypted_private_key FROM ticketing.event_keys WHERE event_id = @p0", sale.EventId);

        // Not a private key as stored...
        using var ecdsa = ECDsa.Create();
        FluentActions.Invoking(() => ecdsa.ImportPkcs8PrivateKey(stored, out _)).Should().Throw<CryptographicException>();

        // ...readable with the right master key and key ID, and with nothing else.
        var right = new MasterKey(Options.Create(new TicketingOptions { MasterKey = TestSystem.MasterKey }));
        var plaintext = right.Decrypt(stored, keyId);
        ecdsa.ImportPkcs8PrivateKey(plaintext, out _);

        var wrong = new MasterKey(Options.Create(new TicketingOptions { MasterKey = new string('x', 40) }));
        FluentActions.Invoking(() => wrong.Decrypt(stored, keyId)).Should().Throw<CryptographicException>();
        FluentActions.Invoking(() => right.Decrypt(stored, "another-key")).Should().Throw<CryptographicException>();
    }

    private async Task<(Sale Sale, string FanId, Guid OrderId)> PaidOrderAsync()
    {
        var sale = await system.OnSaleAsync();
        var fanId = "fan-" + Guid.NewGuid().ToString("N");
        using var fan = system.PublicApi(Callers.Fan(fanId));
        var hold = await (await fan.HoldAsync(sale.EventId, sale.Seats[0], sale.Seats[1])).PlacedAsync();
        var checkout = await fan.CheckoutAsync(hold.HoldId);

        var payment = system.LocalPay.PaymentOf(checkout.OrderId);
        system.LocalPay.Settle(payment, "succeeded");
        await system.PostWebhookAsync(FakeLocalPay.Webhook("payment.succeeded", payment));
        await system.DeliverMessagesAsync();
        return (sale, fanId, checkout.OrderId);
    }

    private async Task<IReadOnlyList<(Guid Id, Guid SeatId, string Label, string Code)>> TicketsAsync(Guid orderId)
    {
        var ids = await system.QueryAsync<Guid>("SELECT id FROM ticketing.tickets WHERE order_id = @p0 ORDER BY label", orderId);
        var seats = await system.QueryAsync<Guid>("SELECT seat_id FROM ticketing.tickets WHERE order_id = @p0 ORDER BY label", orderId);
        var labels = await system.QueryAsync<string>("SELECT label FROM ticketing.tickets WHERE order_id = @p0 ORDER BY label", orderId);
        var codes = await system.QueryAsync<string>("SELECT code FROM ticketing.tickets WHERE order_id = @p0 ORDER BY label", orderId);
        return [.. ids.Select((id, i) => (id, seats[i], labels[i], codes[i]))];
    }

    private async Task<JsonWebKeySet> PublicKeysAsync(Guid eventId)
    {
        using var anonymous = system.PublicApi();
        return new JsonWebKeySet(await anonymous.GetStringAsync(new Uri($"/events/{eventId}/ticket-keys", UriKind.Relative), Ct));
    }

    private static Task<TokenValidationResult> VerifyAsync(string code, JsonWebKeySet keys) =>
        new JsonWebTokenHandler().ValidateTokenAsync(code, new TokenValidationParameters
        {
            IssuerSigningKeys = keys.GetSigningKeys(),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            RequireExpirationTime = false,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        });
}
