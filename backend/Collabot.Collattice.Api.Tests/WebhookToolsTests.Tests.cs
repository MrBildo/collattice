using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// MCP WebhookTools tests. The tools are invoked directly against a DI scope (the project's
// MCP-tools test convention — never through /mcp). Load-bearing properties: the MCP surface shares the one store
// with REST (anti-drift — create via MCP, read via REST → identical), the SSRF check is
// un-bypassable from MCP, list_webhooks never leaks the secret, the secret set/keep/clear contract
// matches REST, and the admin-level gate rejects a non-admin authKey.
public sealed class WebhookToolsTests
{
    private const string _publicUrl = "https://8.8.8.8/hook";
    private const string _privateUrl = "http://127.0.0.1/hook";

    [Fact]
    public async Task Create_Then_List_SecretFree()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var tools = NewTools(scope, allowPrivate: false);

        var createJson = await tools.CreateWebhookAsync(factory.AdminAuthKey, _publicUrl, "card.created", secret: "mcp-secret", name: "via-mcp");
        JsonDocument.Parse(createJson).RootElement.GetProperty("signed").GetBoolean().ShouldBeTrue();
        createJson.ShouldNotContain("mcp-secret");

        var listJson = await tools.ListWebhooksAsync(factory.AdminAuthKey);
        listJson.ShouldNotContain("mcp-secret");
        listJson.ShouldContain(_publicUrl);
    }

    [Fact]
    public async Task Create_PrivateUrl_FlagOff_IsRejected_SsrfUnbypassableFromMcp()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var tools = NewTools(scope, allowPrivate: false);

        var result = await tools.CreateWebhookAsync(factory.AdminAuthKey, _privateUrl, "card.created");
        result.ShouldStartWith("Error:");   // the shared store's SSRF check fires on the MCP path too
    }

    [Fact]
    public async Task Create_Via_Mcp_Read_Via_Rest_AreIdentical_AntiDrift()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();

        Guid id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var tools = NewTools(scope, allowPrivate: false);
            var createJson = await tools.CreateWebhookAsync(factory.AdminAuthKey, _publicUrl, "card.created,card.moved", name: "shared");
            id = JsonDocument.Parse(createJson).RootElement.GetProperty("id").GetGuid();
        }

        // The REST surface reads the SAME store/row — identical view by construction (anti-drift).
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        var rest = await (await client.GetAsync($"/api/v1/webhooks/subscriptions/{id}")).Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);

        rest.GetProperty("url").GetString().ShouldBe(_publicUrl);
        rest.GetProperty("name").GetString().ShouldBe("shared");
        rest.GetProperty("events").EnumerateArray().Select(e => e.GetString()).ShouldBe([WebhookEventTypes.CardCreated, WebhookEventTypes.CardMoved]);
    }

    [Fact]
    public async Task Update_SecretSetKeepClear_MatchesRest()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var tools = NewTools(scope, allowPrivate: false);

        var id = JsonDocument
            .Parse(await tools.CreateWebhookAsync(factory.AdminAuthKey, _publicUrl, "card.created", secret: "keep"))
            .RootElement.GetProperty("id").GetGuid();

        // keep — url-only update leaves it signed.
        Signed(await tools.UpdateWebhookAsync(factory.AdminAuthKey, id, url: "https://1.1.1.1/hook")).ShouldBeTrue();
        // clear.
        Signed(await tools.UpdateWebhookAsync(factory.AdminAuthKey, id, clearSecret: true)).ShouldBeFalse();
        // set (from unsigned).
        Signed(await tools.UpdateWebhookAsync(factory.AdminAuthKey, id, secret: "now-signed")).ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_RemovesViaStore_ThenReportsNotFound()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var tools = NewTools(scope, allowPrivate: false);

        var id = JsonDocument
            .Parse(await tools.CreateWebhookAsync(factory.AdminAuthKey, _publicUrl, "card.created"))
            .RootElement.GetProperty("id").GetGuid();

        (await tools.DeleteWebhookAsync(factory.AdminAuthKey, id)).ShouldBe("Webhook subscription deleted.");
        (await tools.DeleteWebhookAsync(factory.AdminAuthKey, id)).ShouldStartWith("Error:");   // already gone
    }

    [Fact]
    public async Task NonAdmin_IsRejected()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        var human = await TestAuthHelper.CreateUserAsync(client, factory, "Human", UserRole.HumanUser);

        await using var scope = factory.Services.CreateAsyncScope();
        var tools = NewTools(scope, allowPrivate: false);

        var result = await tools.CreateWebhookAsync(human.AuthKey, _publicUrl, "card.created");
        result.ShouldContain("administrator");   // the admin-level gate
    }

    private static bool Signed(string viewJson) =>
        JsonDocument.Parse(viewJson).RootElement.GetProperty("signed").GetBoolean();

    private static WebhookTools NewTools(AsyncServiceScope scope, bool allowPrivate)
    {
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var settings = Options.Create(new WebhookSettings { AllowPrivateNetworkTargets = allowPrivate });
        var store = new WebhookSubscriptionStore(db, settings);
        var tester = new WebhookTester(db, new StubWebhookSender());   // these tests do not ping
        var auth = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        return new WebhookTools(store, tester, auth);
    }
}

// A no-op sender for the MCP tests, which never exercise the ping path — keeps the tester
// constructible without a real HttpClient.
file sealed class StubWebhookSender : IWebhookSender
{
    public Task<WebhookDeliveryResult> SendAsync(BoardEvent boardEvent, WebhookTarget target, CancellationToken ct) =>
        Task.FromResult(new WebhookDeliveryResult(true, 200, null));
}
