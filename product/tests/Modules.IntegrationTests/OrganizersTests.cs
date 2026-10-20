using System.Net;
using System.Net.Http.Json;
using EventTicketing.Organizers;
using EventTicketing.Shared.Application.Authorization;
using EventTicketing.Testing;

namespace EventTicketing.Modules.IntegrationTests;

/// <summary>
/// M1a G1: organizers and their members. Isolation between organizers' events is covered by
/// <see cref="CatalogTests.Another_organizer_can_neither_see_nor_change_the_event"/>.
/// </summary>
public sealed class OrganizersTests(TestSystem system)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static string Admin { get; } = TestTokens.Create("admin-" + Guid.NewGuid().ToString("N"), [Roles.Admin]);

    [Fact]
    public async Task An_admin_registers_an_organizer_and_its_members_manage_the_member_list()
    {
        using var admin = system.BackOfficeApi(Admin);
        var name = "Organizer " + Guid.NewGuid().ToString("N")[..8];
        var created = await admin.PostAsJsonAsync(new Uri("/organizers", UriKind.Relative), new CreateOrganizer(name), CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var organizerId = (await created.Content.ReadFromJsonAsync<OrganizerCreatedResponse>(CancellationToken))!.OrganizerId;

        (await system.ScalarAsync<long>(
            "SELECT count(*) FROM organizers.outbox_messages WHERE type = 'organizers.organizer-created' AND payload->>'organizerId' = @p0",
            organizerId.ToString())).Should().Be(1);

        // A member (by token) adds a colleague; the list shows both.
        using var member = system.BackOfficeApi(Callers.Organizer(organizerId));
        (await AddMemberAsync(member, organizerId, "user-olga", "Olga")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AddMemberAsync(member, organizerId, "user-oscar", "Oscar")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AddMemberAsync(member, organizerId, "user-oscar", "Oscar")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var details = await member.GetFromJsonAsync<OrganizerDetails>(new Uri($"/organizers/{organizerId}", UriKind.Relative), CancellationToken);
        details!.Name.Should().Be(name);
        details.Members.Select(m => m.DisplayName).Should().Equal("Olga", "Oscar");

        (await member.DeleteAsync(new Uri($"/organizers/{organizerId}/members/user-oscar", UriKind.Relative), CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var mine = await member.GetFromJsonAsync<List<OrganizerSummary>>(new Uri("/organizers", UriKind.Relative), CancellationToken);
        mine.Should().ContainSingle().Which.OrganizerId.Should().Be(organizerId);
    }

    [Fact]
    public async Task Only_admins_register_organizers()
    {
        using var organizer = system.BackOfficeApi(Callers.Organizer(Guid.NewGuid()));

        var response = await organizer.PostAsJsonAsync(new Uri("/organizers", UriKind.Relative), new CreateOrganizer("Self-made"), CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_organizer_that_exists_in_Keycloak_is_registered_under_its_ID_once()
    {
        using var admin = system.BackOfficeApi(Admin);
        var keycloakGroupId = Guid.NewGuid();

        var first = await admin.PostAsJsonAsync(new Uri("/organizers", UriKind.Relative), new CreateOrganizer("From the realm", keycloakGroupId), CancellationToken);
        var again = await admin.PostAsJsonAsync(new Uri("/organizers", UriKind.Relative), new CreateOrganizer("From the realm", keycloakGroupId), CancellationToken);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        (await first.Content.ReadFromJsonAsync<OrganizerCreatedResponse>(CancellationToken))!.OrganizerId.Should().Be(keycloakGroupId);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Members_of_another_organizer_can_neither_see_nor_change_it()
    {
        using var admin = system.BackOfficeApi(Admin);
        var created = await admin.PostAsJsonAsync(new Uri("/organizers", UriKind.Relative), new CreateOrganizer("Private"), CancellationToken);
        var organizerId = (await created.Content.ReadFromJsonAsync<OrganizerCreatedResponse>(CancellationToken))!.OrganizerId;

        using var outsider = system.BackOfficeApi(Callers.Organizer(Guid.NewGuid()));

        (await outsider.GetAsync(new Uri($"/organizers/{organizerId}", UriKind.Relative), CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AddMemberAsync(outsider, organizerId, "user-mallory", "Mallory")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.GetFromJsonAsync<List<OrganizerSummary>>(new Uri("/organizers", UriKind.Relative), CancellationToken))
            .Should().NotContain(o => o.OrganizerId == organizerId);
    }

    private static Task<HttpResponseMessage> AddMemberAsync(HttpClient client, Guid organizerId, string userId, string displayName) =>
        client.PostAsJsonAsync(new Uri($"/organizers/{organizerId}/members", UriKind.Relative), new AddMemberRequest(userId, displayName), CancellationToken);

    [Fact]
    public async Task Keycloak_follows_the_member_list_group_and_members_alike()
    {
        using var admin = system.BackOfficeApi(Admin);
        var organizerId = await CreateOrganizerAsync(admin);
        await system.DeliverMessagesAsync();
        system.Directory.HasGroup(organizerId).Should().BeTrue("registering an organizer creates its group");

        (await AddMemberAsync(admin, organizerId, "user-ana", "Ana")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AddMemberAsync(admin, organizerId, "user-ben", "Ben")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await system.DeliverMessagesAsync();
        system.Directory.MembersOf(organizerId).Should().Equal("user-ana", "user-ben");

        (await admin.DeleteAsync(new Uri($"/organizers/{organizerId}/members/user-ana", UriKind.Relative), CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        await system.DeliverMessagesAsync();
        system.Directory.MembersOf(organizerId).Should().Equal("user-ben");
    }

    [Fact]
    public async Task While_Keycloak_is_down_the_change_waits_and_lands_where_the_member_list_is()
    {
        using var admin = system.BackOfficeApi(Admin);
        var organizerId = await CreateOrganizerAsync(admin);
        await system.DeliverMessagesAsync();

        // Added and removed again while Keycloak can't be reached: both syncs fail and wait.
        system.Directory.MakeUnreachableFor(organizerId);
        await AddMemberAsync(admin, organizerId, "user-cid", "Cid");
        await AddMemberAsync(admin, organizerId, "user-dan", "Dan");
        await admin.DeleteAsync(new Uri($"/organizers/{organizerId}/members/user-cid", UriKind.Relative), CancellationToken);
        await system.DeliverMessagesAsync();
        system.Directory.MembersOf(organizerId).Should().BeEmpty();

        // Back again: every sync reads the list as it is now, in whatever order they run.
        system.Directory.MakeReachableFor(organizerId);
        await system.QueryAsync<int>(
            "UPDATE organizers.scheduled_messages SET due_at = now() - interval '1 second' WHERE payload->>'organizerId' = @p0 RETURNING 1",
            organizerId.ToString());
        await system.DeliverMessagesAsync();
        system.Directory.MembersOf(organizerId).Should().Equal("user-dan");
    }

    private static async Task<Guid> CreateOrganizerAsync(HttpClient admin)
    {
        var created = await admin.PostAsJsonAsync(new Uri("/organizers", UriKind.Relative), new CreateOrganizer("Organizer " + Guid.NewGuid().ToString("N")[..8]), CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<OrganizerCreatedResponse>(CancellationToken))!.OrganizerId;
    }
}
