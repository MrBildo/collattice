using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// REST webhook subscription endpoint tests. CRUD round-trip; the admin-level auth matrix
// (AgentAdministrator admitted, HumanUser/AgentUser 403 on every verb); the secret-never-leaks floor
// asserted against the actual serialized bytes; the SSRF registration check un-bypassable from REST;
// the secret set/keep/clear contract; and the ping endpoint (a private target with the flag off
// reports the connect block — no validation-bypassing side-channel).
public class WebhookSubscriptionEndpointTests
{
    private const string _publicUrl = "https://8.8.8.8/hook";
    private const string _privateUrl = "http://127.0.0.1/hook";

    [Fact]
    public async Task Crud_RoundTrips_SecretFree()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);

        var create = await client.PostAsJsonAsync("/api/v1/webhooks/subscriptions", new
        {
            url = _publicUrl,
            events = new[] { WebhookEventTypes.CardCreated },
            secret = "abc",
            name = "prod",
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("signed").GetBoolean().ShouldBeTrue();
        created.TryGetProperty("secret", out _).ShouldBeFalse();   // no secret field at all

        var get = await client.GetAsync($"/api/v1/webhooks/subscriptions/{id}");
        get.EnsureSuccessStatusCode();
        var fetched = await get.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        fetched.GetProperty("url").GetString().ShouldBe(_publicUrl);
        fetched.GetProperty("events")[0].GetString().ShouldBe(WebhookEventTypes.CardCreated);

        var list = await client.GetAsync("/api/v1/webhooks/subscriptions");
        list.EnsureSuccessStatusCode();
        var listed = await list.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        listed.EnumerateArray().Any(x => x.GetProperty("id").GetGuid() == id).ShouldBeTrue();

        var patch = await client.PatchAsJsonAsync($"/api/v1/webhooks/subscriptions/{id}", new { name = "renamed", enabled = false });
        patch.EnsureSuccessStatusCode();
        var patched = await patch.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        patched.GetProperty("name").GetString().ShouldBe("renamed");
        patched.GetProperty("enabled").GetBoolean().ShouldBeFalse();

        var del = await client.DeleteAsync($"/api/v1/webhooks/subscriptions/{id}");
        del.EnsureSuccessStatusCode();
        (await client.GetAsync($"/api/v1/webhooks/subscriptions/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_Secret_NeverAppearsInAnyReadResponse()
    {
        const string secret = "super-secret-signing-key";

        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);

        var create = await client.PostAsJsonAsync("/api/v1/webhooks/subscriptions", new
        {
            url = _publicUrl,
            events = new[] { WebhookEventTypes.CardMoved },
            secret,
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var createdRaw = await create.Content.ReadAsStringAsync();
        var id = JsonDocument.Parse(createdRaw).RootElement.GetProperty("id").GetGuid();

        var getRaw = await (await client.GetAsync($"/api/v1/webhooks/subscriptions/{id}")).Content.ReadAsStringAsync();
        var listRaw = await (await client.GetAsync("/api/v1/webhooks/subscriptions")).Content.ReadAsStringAsync();

        // The actual bytes a caller receives carry `signed:true`, never the secret string.
        createdRaw.ShouldNotContain(secret);
        getRaw.ShouldNotContain(secret);
        listRaw.ShouldNotContain(secret);
        JsonDocument.Parse(createdRaw).RootElement.GetProperty("signed").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Create_EmptyEvents_Returns400()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);

        var response = await client.PostAsJsonAsync("/api/v1/webhooks/subscriptions", new
        {
            url = _publicUrl,
            events = Array.Empty<string>(),
        });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_UnknownEvent_Returns400()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);

        var response = await client.PostAsJsonAsync("/api/v1/webhooks/subscriptions", new
        {
            url = _publicUrl,
            events = new[] { "not.a.real.event" },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_PrivateUrl_FlagOff_Returns400_SsrfUnbypassable()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);

        // The SSRF registration check lives in the shared store — REST cannot bypass it.
        var response = await client.PostAsJsonAsync("/api/v1/webhooks/subscriptions", new
        {
            url = _privateUrl,
            events = new[] { WebhookEventTypes.CardCreated },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_SecretSetKeepClear()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);

        var id = (await (await client.PostAsJsonAsync("/api/v1/webhooks/subscriptions", new
        {
            url = _publicUrl,
            events = new[] { WebhookEventTypes.CardCreated },
            secret = "keep",
        })).Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();

        // keep — a URL-only patch leaves the secret untouched.
        (await PatchSignedAsync(client, id, new { url = "https://1.1.1.1/hook" })).ShouldBeTrue();
        // clear — clearSecret removes it (go unsigned).
        (await PatchSignedAsync(client, id, new { clearSecret = true })).ShouldBeFalse();
        // set — a provided secret replaces (now from unsigned → signed).
        (await PatchSignedAsync(client, id, new { secret = "now-signed" })).ShouldBeTrue();
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task Crud_NonAdmin_Returns403_OnEveryVerb(UserRole role)
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        var user = await TestAuthHelper.CreateUserAsync(client, factory, $"u-{role}", role);
        TestAuthHelper.SetAuth(client, user.AuthKey);

        var randomId = Guid.NewGuid();
        (await client.GetAsync("/api/v1/webhooks/subscriptions")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/v1/webhooks/subscriptions/{randomId}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/v1/webhooks/subscriptions", new { url = _publicUrl, events = new[] { WebhookEventTypes.CardCreated } })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PatchAsJsonAsync($"/api/v1/webhooks/subscriptions/{randomId}", new { name = "x" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.DeleteAsync($"/api/v1/webhooks/subscriptions/{randomId}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PostAsync($"/api/v1/webhooks/subscriptions/{randomId}/test", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Crud_AgentAdministrator_Succeeds()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        var agentAdmin = await TestAuthHelper.CreateUserAsync(client, factory, "AgentAdmin", UserRole.AgentAdministrator);
        TestAuthHelper.SetAuth(client, agentAdmin.AuthKey);

        var create = await client.PostAsJsonAsync("/api/v1/webhooks/subscriptions", new
        {
            url = _publicUrl,
            events = new[] { WebhookEventTypes.CardCreated },
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await client.GetAsync("/api/v1/webhooks/subscriptions")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ping_PrivateTarget_FlagOff_ReportsBlock_NoSideChannel()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);

        // Seed a private-URL subscription directly (the store rejects it at registration with the flag
        // off). The in-request IWebhookSender carries the real SSRF connect guard, so the ping reports
        // a failed delivery rather than dialing internal — no bypass.
        Guid id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
            var sub = new WebhookSubscription
            {
                Id = Guid.NewGuid(),
                Url = "http://127.0.0.1:9/hook",
                Enabled = true,
                EventTypes = [WebhookEventTypes.CardCreated],
            };
            db.WebhookSubscriptions.Add(sub);
            await db.SaveChangesAsync();
            id = sub.Id;
        }

        var response = await client.PostAsync($"/api/v1/webhooks/subscriptions/{id}/test", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        result.GetProperty("success").GetBoolean().ShouldBeFalse();   // connect-blocked, not delivered

        await using var verify = factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<BoardDbContext>();
        (await verifyDb.WebhookDeliveryAttempts.AnyAsync(a => a.SubscriptionId == id && a.Status == WebhookDeliveryStatus.Failed)).ShouldBeTrue();
    }

    [Fact]
    public async Task Ping_UnknownSubscription_Returns404()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);

        var response = await client.PostAsync($"/api/v1/webhooks/subscriptions/{Guid.NewGuid()}/test", null);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task<bool> PatchSignedAsync(HttpClient client, Guid id, object body)
    {
        var response = await client.PatchAsJsonAsync($"/api/v1/webhooks/subscriptions/{id}", body);
        response.EnsureSuccessStatusCode();
        var view = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return view.GetProperty("signed").GetBoolean();
    }
}
