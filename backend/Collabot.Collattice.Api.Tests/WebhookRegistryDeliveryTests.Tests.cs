using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Registry-delivery fan-out tests. The dispatcher loads the enabled subscriptions per event
// and fans out to those whose selection matches. Driven through the deterministic DeliverEventAsync
// seam so each test decides when delivery happens: RunDispatcher = false, the test owns delivery,
// the real HttpWebhookSender runs against a capture stub. No Webhooks:Endpoint, so subscriptions are
// explicit, not seeded.
public class WebhookRegistryDeliveryTests
{
    private const string _urlA = "https://sub-a.test/hook";
    private const string _urlB = "https://sub-b.test/hook";

    [Fact]
    public async Task TwoSubscriptions_SelectingTheEvent_BothReceiveIt_WithDistinctSubscriptionIds()
    {
        await using var factory = await NewFactoryAsync();
        var idA = await AddSubscriptionAsync(factory, _urlA, enabled: true, WebhookEventTypes.CardMoved);
        var idB = await AddSubscriptionAsync(factory, _urlB, enabled: true, WebhookEventTypes.CardMoved);

        await DeliverAsync(factory, SampleEvent(WebhookEventTypes.CardMoved));

        // Both URLs were POSTed to.
        var dialed = factory.Handler.Requests.Select(r => r.Uri!.ToString()).ToList();
        dialed.ShouldContain(_urlA);
        dialed.ShouldContain(_urlB);

        // One attempt row per subscription, sharing the eventId, distinguished by SubscriptionId.
        var attempts = await ReadAttemptsAsync(factory);
        attempts.Count.ShouldBe(2);
        attempts.Select(a => a.SubscriptionId).ShouldBe([idA, idB], ignoreOrder: true);
        attempts.Select(a => a.EventId).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Delivery_OneFailingSubscription_DoesNotPreventDeliveryToTheOther()
    {
        await using var factory = await NewFactoryAsync();
        var idA = await AddSubscriptionAsync(factory, _urlA, enabled: true, WebhookEventTypes.CardMoved);
        var idB = await AddSubscriptionAsync(factory, _urlB, enabled: true, WebhookEventTypes.CardMoved);

        // Subscription A's endpoint fails; B's succeeds.
        factory.Handler.ResponseSelector = uri =>
            uri!.ToString() == _urlA ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;

        await DeliverAsync(factory, SampleEvent(WebhookEventTypes.CardMoved));

        var attempts = await ReadAttemptsAsync(factory);

        var aAttempts = attempts.Where(a => a.SubscriptionId == idA).ToList();
        var bAttempts = attempts.Where(a => a.SubscriptionId == idB).ToList();

        aAttempts.Count.ShouldBe(3);                                        // retried to MaxAttempts
        aAttempts.ShouldAllBe(a => a.Status == WebhookDeliveryStatus.Failed);
        bAttempts.Count.ShouldBe(1);                                        // succeeded first try
        bAttempts[0].Status.ShouldBe(WebhookDeliveryStatus.Succeeded);      // B delivered despite A failing
    }

    [Fact]
    public async Task Delivery_SubscriptionNotSelectingTheEvent_ReceivesNothing()
    {
        await using var factory = await NewFactoryAsync();
        await AddSubscriptionAsync(factory, _urlA, enabled: true, WebhookEventTypes.CardCreated);   // only created

        await DeliverAsync(factory, SampleEvent(WebhookEventTypes.CardMoved));   // a move

        factory.Handler.RequestCount.ShouldBe(0);
        (await ReadAttemptsAsync(factory)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delivery_WildcardSubscription_ReceivesEveryEvent()
    {
        await using var factory = await NewFactoryAsync();
        var id = await AddSubscriptionAsync(factory, _urlA, enabled: true, WebhookEventTypes.Wildcard);

        await DeliverAsync(factory, SampleEvent(WebhookEventTypes.CardCreated));
        await DeliverAsync(factory, SampleEvent(WebhookEventTypes.CardMoved));

        var attempts = await ReadAttemptsAsync(factory);
        attempts.Count.ShouldBe(2);
        attempts.ShouldAllBe(a => a.SubscriptionId == id);
    }

    [Fact]
    public async Task Delivery_DisabledSubscription_ReceivesNothing()
    {
        await using var factory = await NewFactoryAsync();
        await AddSubscriptionAsync(factory, _urlA, enabled: false, WebhookEventTypes.CardCreated);

        await DeliverAsync(factory, SampleEvent(WebhookEventTypes.CardCreated));

        factory.Handler.RequestCount.ShouldBe(0);
        (await ReadAttemptsAsync(factory)).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletingSubscription_LeavesItsDeliveryHistory_WithSubscriptionIdNulled()
    {
        await using var factory = await NewFactoryAsync();
        var id = await AddSubscriptionAsync(factory, _urlA, enabled: true, WebhookEventTypes.CardCreated);

        await DeliverAsync(factory, SampleEvent(WebhookEventTypes.CardCreated));
        (await ReadAttemptsAsync(factory)).ShouldNotBeEmpty();

        // Delete the subscription through the store.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
            var store = new WebhookSubscriptionStore(db, Options.Create(new WebhookSettings()));
            (await store.DeleteAsync(id, CancellationToken.None)).ShouldBeTrue();
        }

        // The audit log survives — the row is intact, its SubscriptionId nulled (SetNull).
        var attempts = await ReadAttemptsAsync(factory);
        attempts.ShouldNotBeEmpty();
        attempts.ShouldAllBe(a => a.SubscriptionId == null);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<WebhookDeliveryFactory> NewFactoryAsync()
    {
        var factory = new WebhookDeliveryFactory
        {
            // No Webhooks:Endpoint → no migration seed; subscriptions are explicit. Near-zero retry
            // backoff so the retry loop runs without real waits. RunDispatcher = false → the test
            // drives DeliverEventAsync, no hosted-loop race.
            ConfigOverrides = new Dictionary<string, string?>
            {
                ["Webhooks:Endpoint"] = null,
                ["Webhooks:MaxAttempts"] = "3",
                ["Webhooks:RetryBackoffBase"] = "00:00:00.010",
            },
            RunDispatcher = false,
        };
        await factory.InitializeAsync();
        return factory;
    }

    private static async Task<Guid> AddSubscriptionAsync(WebhookDeliveryFactory factory, string url, bool enabled, params string[] events)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Url = url,
            Enabled = enabled,
            EventTypes = [.. events],
        };
        db.WebhookSubscriptions.Add(sub);
        await db.SaveChangesAsync();
        return sub.Id;
    }

    private static async Task DeliverAsync(WebhookDeliveryFactory factory, BoardEvent boardEvent)
    {
        var settings = factory.Services.GetRequiredService<IOptions<WebhookSettings>>().Value;
        var logger = factory.Services.GetRequiredService<ILogger<WebhookDispatcherService>>();

        using var httpClient = new HttpClient(factory.Handler, disposeHandler: false)
        {
            Timeout = settings.DeliveryTimeout,
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Collattice-Webhooks");
        var sender = new HttpWebhookSender(httpClient);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        await WebhookDispatcherService.DeliverEventAsync(boardEvent, sender, db, settings, logger, CancellationToken.None);
    }

    private static async Task<List<WebhookDeliveryAttempt>> ReadAttemptsAsync(WebhookDeliveryFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.WebhookDeliveryAttempts.ToListAsync();
    }

    private static BoardEvent SampleEvent(string eventType) =>
        new
        (
            eventType,
            Ulid.NewUlid().ToString(),
            DateTimeOffset.UtcNow,
            "1",
            Guid.NewGuid(),
            "board-slug",
            new BoardEventActor(Guid.NewGuid(), "Tester", "Administrator"),
            new { card = new { id = Guid.NewGuid() } }
        );
}
