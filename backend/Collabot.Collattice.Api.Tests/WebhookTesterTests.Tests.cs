using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// WebhookTester (ping) seam tests. The shared test-delivery path both REST /test and the
// test_webhook MCP tool delegate to. Driven directly with a constructed HttpWebhookSender
// (constructing it directly sidesteps IHttpClientFactory's opaque handler caching): a
// CapturingHttpMessageHandler for the happy-path
// bytes/headers, and the REAL SSRF-guarded SocketsHttpHandler for the no-side-channel proof — a
// private target with the flag off is connect-blocked here exactly as on a real event.
public class WebhookTesterTests
{
    [Fact]
    public async Task Test_DeliversPing_SignsWithSecret_WritesOneAttempt_ReturnsSuccess()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();

        using var handler = new CapturingHttpMessageHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Collattice-Webhooks");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var (subId, actor) = await SeedAsync(db, "https://sink.test/hook", secret: "ping-secret");

        var tester = new WebhookTester(db, new HttpWebhookSender(httpClient));
        var result = await tester.TestAsync(subId, actor, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.StatusCode.ShouldBe(200);

        // One POST to the URL carrying the ping event type + a signature (the same pipe as a real
        // event — not a side channel).
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Uri!.ToString().ShouldBe("https://sink.test/hook");
        handler.Requests[0].Headers["X-Collattice-Event"].ShouldBe(WebhookEventTypes.Ping);
        handler.Requests[0].Headers.ShouldContainKey("X-Collattice-Signature");

        // Exactly one attempt row, tagged with the subscription + the ping type.
        var attempts = await db.WebhookDeliveryAttempts.Where(a => a.SubscriptionId == subId).ToListAsync();
        attempts.Count.ShouldBe(1);
        attempts[0].EventType.ShouldBe(WebhookEventTypes.Ping);
        attempts[0].Status.ShouldBe(WebhookDeliveryStatus.Succeeded);
    }

    [Fact]
    public async Task Test_UnknownSubscription_ReturnsNull()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();

        using var handler = new CapturingHttpMessageHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) };

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var actor = await db.Users.FirstAsync();

        var tester = new WebhookTester(db, new HttpWebhookSender(httpClient));
        (await tester.TestAsync(Guid.NewGuid(), actor, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Test_PrivateTarget_FlagOff_IsConnectBlocked_RecordsFailure_NoSideChannel()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();

        // The REAL SSRF-guarded handler (allowPrivate:false) — the ping has no path around it (the
        // same IWebhookSender as production delivery). A private target is blocked at connect.
        using var guarded = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = SsrfGuard.CreateConnectCallback(allowPrivate: false),
        };
        using var httpClient = new HttpClient(guarded) { Timeout = TimeSpan.FromSeconds(5) };

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var (subId, actor) = await SeedAsync(db, "http://127.0.0.1:9/hook", secret: null);

        var tester = new WebhookTester(db, new HttpWebhookSender(httpClient));
        var result = await tester.TestAsync(subId, actor, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBeNull();   // blocked before any socket — no response

        // The block is recorded — the test shows in the delivery log like every attempt.
        var attempt = await db.WebhookDeliveryAttempts.SingleAsync(a => a.SubscriptionId == subId);
        attempt.Status.ShouldBe(WebhookDeliveryStatus.Failed);
        attempt.EventType.ShouldBe(WebhookEventTypes.Ping);
    }

    private static async Task<(Guid SubscriptionId, BoardUser Actor)> SeedAsync(BoardDbContext db, string url, string? secret)
    {
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Url = url,
            Secret = secret,
            Enabled = true,
            EventTypes = [WebhookEventTypes.CardCreated],
        };
        db.WebhookSubscriptions.Add(sub);
        await db.SaveChangesAsync();

        var actor = await db.Users.FirstAsync();
        return (sub.Id, actor);
    }
}
