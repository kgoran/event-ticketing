using System.Buffers.Text;
using System.Text;

namespace EventTicketing.Scanner.Tests;

/// <summary>D62, ADR-0013: the door decides offline from the door list and its own scans.</summary>
public sealed class DoorDecisionTests
{
    private static readonly Guid EventId = Guid.CreateVersion7();
    private static readonly Guid TicketId = Guid.CreateVersion7();

    [Fact]
    public void A_valid_ticket_on_the_list_is_let_in()
    {
        var decision = DoorDecision.Decide(Code(TicketId), signatureValid: true, Door());

        decision.Should().Be(new Decision(true, "ok", "Welcome."));
    }

    [Fact]
    public void A_valid_ticket_not_on_the_list_yet_is_let_in_rather_than_stopping_the_queue()
    {
        DoorDecision.Decide(Code(Guid.CreateVersion7()), signatureValid: true, Door())
            .Should().Match<Decision>(d => d.Admit && d.Reason == "not-on-list-yet");
    }

    [Theory]
    [InlineData(false, "bad-code")]
    public void A_code_not_signed_by_the_events_key_is_refused(bool signatureValid, string reason)
    {
        DoorDecision.Decide(Code(TicketId), signatureValid, Door()).Should().Match<Decision>(d => !d.Admit && d.Reason == reason);
    }

    [Fact]
    public void Revoked_already_scanned_and_other_events_tickets_are_refused()
    {
        var revoked = new DoorList { EventId = EventId, Issued = [TicketId], Revoked = [TicketId] };
        DoorDecision.Decide(Code(TicketId), true, new DoorView(revoked, new DoorQueue())).Reason.Should().Be("revoked");

        var here = new DoorQueue { AdmittedHere = { [TicketId] = DateTimeOffset.UtcNow } };
        DoorDecision.Decide(Code(TicketId), true, new DoorView(List(), here)).Reason.Should().Be("already-scanned");

        var elsewhere = List();
        elsewhere.Admitted[TicketId] = DateTimeOffset.UtcNow;
        DoorDecision.Decide(Code(TicketId), true, new DoorView(elsewhere, new DoorQueue())).Reason.Should().Be("already-scanned", "another door let it in and this one has synced since");

        DoorDecision.Decide(Code(TicketId, Guid.CreateVersion7()), true, Door()).Reason.Should().Be("other-event");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a code")]
    [InlineData("a.b.c")]
    [InlineData("eyJhbGciOiJFUzI1NiJ9.e30.c2ln")]
    public void Anything_that_isnt_a_ticket_code_is_refused(string text)
    {
        DoorDecision.Decide(TicketCode.Parse(text), signatureValid: false, Door()).Reason.Should().Be("bad-code");
    }

    [Fact]
    public void A_codes_claims_are_read_from_its_payload()
    {
        var text = Jws(TicketId, EventId);

        TicketCode.Parse(text).Should().Be(new TicketCode(TicketId, EventId, 1));
        TicketCode.Parse("  " + text + "\n").Should().NotBeNull("a pasted code may come with spaces around it");
    }

    [Fact]
    public void A_device_is_set_up_from_the_registrations_answer_as_pasted()
    {
        Pages.Door.ParseSetup("""{ "deviceId": "x", "clientId": "device-abc", "clientSecret": "s3cret", "tokenEndpoint": "http://k" }""")
            .Should().Be(new DeviceSetup("device-abc", "s3cret"));
        Pages.Door.ParseSetup("""{ "clientId": "device-abc" }""").Should().BeNull();
        Pages.Door.ParseSetup("not json").Should().BeNull();
    }

    private static DoorList List() => new() { EventId = EventId, Issued = [TicketId] };

    private static DoorView Door() => new(List(), new DoorQueue());

    private static TicketCode Code(Guid ticketId, Guid? eventId = null) => new(ticketId, eventId ?? EventId, 1);

    /// <summary>A code as Ticketing makes it (ADR-0051), with a dummy signature: only its claims are read here.</summary>
    private static string Jws(Guid ticketId, Guid eventId) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"alg":"ES256","typ":"JWT","kid":"k1"}""")) + "." +
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($$"""{"tid":"{{ticketId}}","eid":"{{eventId}}","ver":1}""")) + ".c2ln";
}
