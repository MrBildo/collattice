using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// What the running dispatcher does when the database fails under it. The event is a real one (a card
// created through the REST API), delivered by the hosted dispatcher through the real queue and sender
// to a capture stub, and the faults are commands failing at the database. Each test builds its own
// host, because the delivery settings bind at startup.
public class WebhookDispatchFailureTests
{
    private const string _urlA = "https://sub-a.test/hook";
    private const string _urlB = "https://sub-b.test/hook";

    [Fact]
    public async Task SubscriptionReadFailsTwice_EventStaysQueued_AndIsDelivered()
    {
        // Arrange — three tries per event, and the registry read failing on the first two.
        await using var factory = await NewFactoryAsync();
        await AddSubscriptionAsync(factory, _urlA);
        var client = AdminClient(factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        factory.Faults.FailSubscriptionReads(2);

        // Act
        await CreateCardAsync(client, factory, laneId, "Survives two failed reads");
        var request = await WaitForRequestAsync(factory);

        // Assert — delivered on the third read, and recorded like any other delivery.
        CardName(request).ShouldBe("Survives two failed reads");
        factory.Faults.SubscriptionReads.ShouldBe(3);

        var attempts = await ReadAttemptsAsync(factory);
        attempts.Count.ShouldBe(1);
        attempts[0].Status.ShouldBe(WebhookDeliveryStatus.Succeeded);
    }

    [Fact]
    public async Task SubscriptionReadFailsEveryTry_EventIsDroppedWithAnError_AndTheNextEventIsDelivered()
    {
        // Arrange — the registry read failing on all three tries of the first event only.
        await using var factory = await NewFactoryAsync();
        await AddSubscriptionAsync(factory, _urlA);
        var client = AdminClient(factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        factory.Faults.FailSubscriptionReads(3);

        // Act — the first card's event exhausts its tries; the second card's follows it.
        await CreateCardAsync(client, factory, laneId, "Dropped");
        await CreateCardAsync(client, factory, laneId, "Delivered after the drop");
        var request = await WaitForRequestAsync(factory);

        // Assert — the drop is an error naming the event, and the queue did not stall behind it.
        CardName(request).ShouldBe("Delivered after the drop");

        var drops = factory.Log
            .Where(e => e.Level == LogLevel.Error && e.Message.Contains("was dropped", StringComparison.Ordinal))
                .ToList();

        drops.Count.ShouldBe(1);
        drops[0].Message.ShouldContain(WebhookEventTypes.CardCreated);
        drops[0].Exception.ShouldBeOfType<Microsoft.Data.Sqlite.SqliteException>();

        factory.Handler.Requests.Select(CardName).ShouldBe(["Delivered after the drop"]);
    }

    [Fact]
    public async Task AttemptRowFailsToSave_TheOtherSubscriptionStillReceivesTheEvent()
    {
        // Arrange — two subscriptions, and the first delivery-attempt row of the event failing to save.
        await using var factory = await NewFactoryAsync();
        var idA = await AddSubscriptionAsync(factory, _urlA);
        var idB = await AddSubscriptionAsync(factory, _urlB);
        var client = AdminClient(factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        factory.Faults.FailAttemptInserts(1);

        // Act
        await CreateCardAsync(client, factory, laneId, "Fans out past a failed save");
        await WaitForRequestAsync(factory);
        await WaitForRequestAsync(factory);
        var recorded = await WaitForAttemptRowsAsync(factory, 1);

        // Assert — both subscribers got the event once; the one row that could not be saved is
        // reported, and the other subscription's row is in the delivery log.
        factory.Handler.Requests.Select(r => r.Uri!.ToString()).ShouldBe([_urlA, _urlB], ignoreOrder: true);

        recorded.Count.ShouldBe(1);
        recorded[0].SubscriptionId.ShouldNotBeNull();
        new[] { idA, idB }.ShouldContain(recorded[0].SubscriptionId!.Value);

        var unrecorded = factory.Log
            .Where(e => e.Level == LogLevel.Error && e.Message.Contains("failed before its outcome was recorded", StringComparison.Ordinal))
                .ToList();

        unrecorded.Count.ShouldBe(1);

        var missing = recorded[0].SubscriptionId == idA ? idB : idA;
        unrecorded[0].Message.ShouldContain(missing.ToString());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<WebhookDispatchFaultFactory> NewFactoryAsync()
    {
        var factory = new WebhookDispatchFaultFactory
        {
            // No Webhooks:Endpoint, so no seeded subscription; a near-zero backoff so the retries
            // run without real waits.
            ConfigOverrides = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Webhooks:Endpoint"] = null,
                ["Webhooks:MaxAttempts"] = "3",
                ["Webhooks:RetryBackoffBase"] = "00:00:00.010",
            },
        };

        await factory.InitializeAsync();
        return factory;
    }

    private static HttpClient AdminClient(WebhookDispatchFaultFactory factory)
    {
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        return client;
    }

    private static async Task<Guid> AddSubscriptionAsync(WebhookDispatchFaultFactory factory, string url)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var subscription = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Url = url,
            Enabled = true,
            EventTypes = [WebhookEventTypes.CardCreated],
        };

        db.WebhookSubscriptions.Add(subscription);
        await db.SaveChangesAsync();

        return subscription.Id;
    }

    private static async Task CreateCardAsync(HttpClient client, WebhookDispatchFaultFactory factory, Guid laneId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name, laneId });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<CapturedRequest> WaitForRequestAsync(WebhookDispatchFaultFactory factory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            return await factory.Handler.WaitForNextRequestAsync(timeout.Token);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException("Expected a webhook delivery within 30 s, but none arrived.", ex);
        }
    }

    // The dispatcher writes an attempt row just after its POST lands, so the row can trail the
    // captured request by a moment.
    private static async Task<List<WebhookDeliveryAttempt>> WaitForAttemptRowsAsync(WebhookDispatchFaultFactory factory, int count)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        var attempts = await ReadAttemptsAsync(factory);

        while (attempts.Count < count && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            attempts = await ReadAttemptsAsync(factory);
        }

        return attempts;
    }

    private static async Task<List<WebhookDeliveryAttempt>> ReadAttemptsAsync(WebhookDispatchFaultFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        return await db.WebhookDeliveryAttempts.ToListAsync();
    }

    private static string? CardName(CapturedRequest request) =>
        JsonDocument.Parse(request.Body).RootElement
            .GetProperty("data")
                .GetProperty("card")
                    .GetProperty("name")
                        .GetString();
}
