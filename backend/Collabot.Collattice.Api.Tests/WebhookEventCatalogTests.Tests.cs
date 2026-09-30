using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Models;
using Collabot.Collattice.Api.Tests.Infrastructure;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// WebhookEventCatalog + GET /api/v1/webhooks/event-types — the single server-side source of
// truth for the subscription picker. Two layers: the pure drift guard (the catalog presents exactly
// the deliver/select SoT, so the in-backend catalog can never desync from what actually fires) and
// the endpoint (full catalog grouped by family, admin-gated like the rest of the webhook surface).
public sealed class WebhookEventCatalogTests
{
    // The crux: the catalog must present EXACTLY WebhookEventTypes.All. Adding an event to the
    // deliver/select SoT without display metadata here — or vice versa — fails this test, which is the
    // anti-drift binding the frontend's hand-maintained copy never had.
    [Fact]
    public void Catalog_PresentsExactly_WebhookEventTypesAll() =>
        WebhookEventCatalog.Types.ShouldBe(WebhookEventTypes.All, ignoreOrder: true);

    [Fact]
    public void Catalog_ContainsAll27Events()
    {
        var count = WebhookEventCatalog.Groups.Sum(group => group.Events.Count);

        count.ShouldBe(27);
    }

    [Fact]
    public void Catalog_Descriptors_AreWellFormed()
    {
        var descriptors = WebhookEventCatalog.Groups
            .SelectMany(group => group.Events)
            .ToList();

        foreach (var descriptor in descriptors)
        {
            descriptor.Type.ShouldNotBeNullOrWhiteSpace();
            descriptor.Label.ShouldNotBeNullOrWhiteSpace();
            descriptor.Description.ShouldNotBeNullOrWhiteSpace();
        }

        descriptors
            .Select(descriptor => descriptor.Type)
            .Distinct(StringComparer.Ordinal)
                .Count()
                .ShouldBe(descriptors.Count, "an event type appears in more than one group");
    }

    // card.updated fires only on name/description/size changes; label changes fire card.labeled /
    // card.unlabeled instead. This pins the description so a future edit that reinstates "or labels"
    // fails the build.
    [Fact]
    public void Catalog_CardUpdated_DescriptionMatchesEmitScope()
    {
        var descriptor = WebhookEventCatalog.Groups
            .SelectMany(group => group.Events)
                .Single(e => e.Type == WebhookEventTypes.CardUpdated);

        descriptor.Description.ShouldBe("A card's name, description, or size changes.");
    }

    [Fact]
    public void Catalog_Groups_AreNonEmpty_AndEachFamilyIsItsEventsNamespace()
    {
        foreach (var group in WebhookEventCatalog.Groups)
        {
            group.Family.ShouldNotBeNullOrWhiteSpace();
            group.Label.ShouldNotBeNullOrWhiteSpace();
            group.Events.ShouldNotBeEmpty();
            group.Events.ShouldAllBe(descriptor => descriptor.Type.StartsWith(group.Family + ".", StringComparison.Ordinal));
        }

        // Exactly one group per event namespace in the deliver/select source of truth. Derived rather
        // than listed, so a new family is expected the moment its first event type is declared — whether
        // a write surface declares any event at all is the coverage test's question, not this one's.
        var namespaces = WebhookEventTypes.All
            .Select(eventType => eventType[..eventType.IndexOf('.', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal);

        WebhookEventCatalog.Groups
            .Select(group => group.Family)
            .ShouldBe(namespaces, ignoreOrder: true);
    }

    [Fact]
    public async Task EventTypes_Endpoint_ReturnsFullCatalog_GroupedByFamily()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);

        var response = await client.GetAsync("/api/v1/webhooks/event-types");
        response.EnsureSuccessStatusCode();

        var groups = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);

        // The endpoint serves the catalog's groups, in the catalog's order.
        var families = groups.EnumerateArray()
            .Select(group => group.GetProperty("family").GetString())
                .ToList();
        families.ShouldBe(WebhookEventCatalog.Groups.Select(group => group.Family));

        var types = groups.EnumerateArray()
            .SelectMany(group => group.GetProperty("events").EnumerateArray())
            .Select(descriptor => descriptor.GetProperty("type").GetString()!)
            .ToList();
        types.Count.ShouldBe(27);
        types.ShouldBe(WebhookEventTypes.All, ignoreOrder: true);

        // Each descriptor carries the display metadata the picker renders.
        var first = groups.EnumerateArray().First().GetProperty("events").EnumerateArray().First();
        first.GetProperty("type").GetString().ShouldBe(WebhookEventTypes.CardCreated);
        first.GetProperty("label").GetString().ShouldNotBeNullOrWhiteSpace();
        first.GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task EventTypes_Endpoint_NonAdmin_Returns403(UserRole role)
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        var user = await TestAuthHelper.CreateUserAsync(client, factory, $"u-{role}", role);
        TestAuthHelper.SetAuth(client, user.AuthKey);

        (await client.GetAsync("/api/v1/webhooks/event-types")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EventTypes_Endpoint_AgentAdministrator_Succeeds()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        var agentAdmin = await TestAuthHelper.CreateUserAsync(client, factory, "AgentAdmin", UserRole.AgentAdministrator);
        TestAuthHelper.SetAuth(client, agentAdmin.AuthKey);

        (await client.GetAsync("/api/v1/webhooks/event-types")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
