using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Collabot.Collattice.Api.Configuration;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Hosting.Webhooks;
using Collabot.Collattice.Api.Models;
using Collabot.Collattice.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Webhook DELIVERY tests (delivery flow + status endpoint + edge cases).
// These exercise the REAL dispatcher draining the REAL queue and POSTing real bytes through the
// real HttpWebhookSender (serialize-once + sign + headers); only the HttpClient's primary handler
// is swapped for a capture stub. Each scenario needs its own Webhooks config (Endpoint/Secret/
// MaxAttempts/RetryBackoffBase bind at startup), so each test builds its own factory rather than
// sharing a fixture.
public sealed class WebhookDeliveryTests
{
    private const string _testEndpoint = "https://sink.test/webhooks";

    // Config that makes the dispatcher fast and deterministic: a near-zero retry backoff so the
    // retry loop runs without real wall-clock waits. The poll interval (500ms) is internal.
    private static Dictionary<string, string?> BaseConfig(string? endpoint, string? secret = null, int maxAttempts = 3) => new()
    {
        ["Webhooks:Endpoint"] = endpoint,
        ["Webhooks:Secret"] = secret,
        ["Webhooks:MaxAttempts"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Webhooks:RetryBackoffBase"] = "00:00:00.010",   // 10ms — exercise the retry loop, no wall-clock wait
    };

    // Each test new()s its own factory (config binds at startup, so config-per-scenario needs a
    // fresh host). The factory is not managed by an IClassFixture, so InitializeAsync — which seeds
    // and captures the admin auth key + default board id — is driven explicitly here.
    //
    // runDispatcher = true: the hosted dispatcher drains the queue (end-to-end "delivery happens"
    // tests asserting on the capturing handler). false: the test owns the queue and drives the
    // deterministic DeliverEventAsync seam (persistence tests — the test decides when delivery
    // happens and when its rows are read back).
    private static async Task<WebhookDeliveryFactory> CreateFactoryAsync(IReadOnlyDictionary<string, string?> config, bool runDispatcher = true)
    {
        var factory = new WebhookDeliveryFactory { ConfigOverrides = config, RunDispatcher = runDispatcher };
        await factory.InitializeAsync();
        return factory;
    }

    // Drives the production delivery seam deterministically over every event currently queued: one
    // DbContext scope per event, the REAL HttpWebhookSender (serialize-once + sign + headers)
    // constructed directly over an HttpClient wrapping the capture stub, real retry + persist.
    // Returns the number of events delivered. Used by the persistence tests (RunDispatcher = false)
    // — the TempCardSweepService.SweepAsync direct-seam pattern, not racing the hosted loop.
    //
    // The sender is constructed directly (not DI-resolved) so the capture handler is unambiguously
    // the transport — IHttpClientFactory's named-client handler caching is sidestepped entirely.
    // The HttpClient mirrors the Program.cs typed-client config (UA + DeliveryTimeout).
    private static async Task<int> DrainQueueViaSeamAsync(WebhookDeliveryFactory factory)
    {
        var queue = factory.Services.GetRequiredService<WebhookQueue>();
        var settings = factory.Services.GetRequiredService<IOptions<WebhookSettings>>();
        var logger = factory.Services.GetRequiredService<ILogger<WebhookDispatcherService>>();

        using var httpClient = new HttpClient(factory.Handler, disposeHandler: false)
        {
            Timeout = settings.Value.DeliveryTimeout,
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Collattice-Webhooks");
        var sender = new HttpWebhookSender(httpClient);

        var delivered = 0;
        while (queue.TryDequeue(out var boardEvent) && boardEvent is not null)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

            await WebhookDispatcherService.DeliverEventAsync(boardEvent, sender, db, settings.Value, logger, CancellationToken.None);
            delivered++;
        }

        return delivered;
    }

    // ── Scenario 1 (delivery side): card.created is POSTed async after the 201, fat + actor ──

    [Fact]
    public async Task RestCreate_PostsCardCreated_AsyncAfterResponse_WithHeaders()
    {
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint));
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        var response = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name = "Delivered Card", laneId });
        response.EnsureSuccessStatusCode();   // 201 returns immediately, independent of delivery

        var request = await WaitForOneRequestAsync(factory.Handler);

        request.Method.ShouldBe("POST");
        request.Uri!.ToString().ShouldBe(_testEndpoint);
        request.Headers["Content-Type"].ShouldStartWith("application/json");
        request.Headers["User-Agent"].ShouldContain("Collattice-Webhooks");
        request.Headers["X-Collattice-Event"].ShouldBe("card.created");
        request.Headers.ContainsKey("X-Collattice-Signature").ShouldBeFalse();   // no secret → unsigned

        var wire = JsonDocument.Parse(request.Body).RootElement;
        wire.GetProperty("event").GetString().ShouldBe("card.created");
        wire.GetProperty("boardSlug").GetString().ShouldNotBeNullOrEmpty();
        wire.GetProperty("actor").GetProperty("role").GetString().ShouldBe("Administrator");
        wire.GetProperty("data").GetProperty("card").GetProperty("name").GetString().ShouldBe("Delivered Card");

        // X-Collattice-Delivery-Id == the envelope eventId (dedup correlation).
        request.Headers["X-Collattice-Delivery-Id"].ShouldBe(wire.GetProperty("eventId").GetString());
    }

    // ── Scenario 6: dark by default — no Endpoint → no outbound calls, no rows ────

    [Fact]
    public async Task DarkByDefault_MakesNoOutboundCalls_AndPersistsNoRows()
    {
        // No Webhooks:Endpoint → dark. The API must behave identically to a webhooks-off run.
        await using var factory = await CreateFactoryAsync(BaseConfig(endpoint: null));
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        var response = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name = "Dark Create", laneId });
        response.EnsureSuccessStatusCode();

        // Give the dispatcher time to have drained-and-no-op'd had it been configured.
        await Task.Delay(800);

        factory.Handler.RequestCount.ShouldBe(0);
        (await CountDeliveryAttemptsAsync(factory)).ShouldBe(0);
    }

    [Fact]
    public async Task DisabledMasterSwitch_MakesNoOutboundCalls()
    {
        // Endpoint set but Enabled=false → still dark (the pause-but-keep-config affordance).
        var config = BaseConfig(_testEndpoint);
        config["Webhooks:Enabled"] = "false";
        await using var factory = await CreateFactoryAsync(config);
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        var response = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name = "Disabled Create", laneId });
        response.EnsureSuccessStatusCode();

        await Task.Delay(800);

        factory.Handler.RequestCount.ShouldBe(0);
        (await CountDeliveryAttemptsAsync(factory)).ShouldBe(0);
    }

    // ── Scenario 7: HMAC signing round-trip — over byte-identical bytes ───────────

    [Fact]
    public async Task WithSecret_SignsRawBody_AndConsumerRecomputationMatches()
    {
        const string secret = "test-secret";
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint, secret));
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        var response = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name = "Signed Card", laneId });
        response.EnsureSuccessStatusCode();

        var request = await WaitForOneRequestAsync(factory.Handler);

        var signature = request.Headers["X-Collattice-Signature"];
        signature.ShouldStartWith("sha256=");

        // The footgun guard: recompute HMAC over the SAME captured raw bytes the handler received
        // (request.Body) — NOT a re-serialization of the parsed body. They must be byte-identical.
        var expected = "sha256=" + Convert.ToHexStringLower
        (
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), request.Body)
        );
        signature.ShouldBe(expected);
    }

    [Fact]
    public async Task WithoutSecret_SendsNoSignatureHeader()
    {
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint, secret: null));
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        var response = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name = "Unsigned Card", laneId });
        response.EnsureSuccessStatusCode();

        var request = await WaitForOneRequestAsync(factory.Handler);
        request.Headers.ContainsKey("X-Collattice-Signature").ShouldBeFalse();
    }

    // ── The dispatcher's database work runs beside other contexts' open statements ──
    // A create enqueues its event and then keeps reading to build its response, so the dispatcher
    // can open its context while that read is mid-result. Holding a statement open on another
    // context for the whole create-and-deliver reproduces that state on demand.

    [Fact]
    public async Task OpenStatementOnAnotherContext_DoesNotStopTheCreateOrItsDelivery()
    {
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint));
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        // Two rows, one read: the statement stays active until the reader is disposed.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 UNION ALL SELECT 2";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();

        var response = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name = "Beside An Open Read", laneId });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var request = await WaitForOneRequestAsync(factory.Handler);
        var wire = JsonDocument.Parse(request.Body).RootElement;
        wire.GetProperty("data").GetProperty("card").GetProperty("name").GetString().ShouldBe("Beside An Open Read");
    }

    // ── Scenario 8: retry + persisted attempts + loud drop; the mutation still succeeded ──

    [Fact]
    public async Task FailingEndpoint_RetriesToMaxAttempts_PersistsEach_AndMutationUnaffected()
    {
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint, maxAttempts: 3), runDispatcher: false);
        factory.Handler.ResponseStatusCode = HttpStatusCode.InternalServerError;   // 500 always

        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        var response = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name = "Retry Card", laneId });
        // The user's mutation succeeded and was never blocked by the (failing) delivery — the
        // event was enqueued after a successful SaveChanges and delivery is entirely out-of-band.
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Drive the production delivery seam over the queued event: 3 attempts against the 500 stub.
        await DrainQueueViaSeamAsync(factory);

        // 3 Failed attempts persisted, ascending Attempt, HttpStatusCode 500.
        var attempts = await ReadDeliveryAttemptsAsync(factory);
        attempts.Count.ShouldBe(3);
        attempts.Select(a => a.Attempt).ShouldBe([1, 2, 3]);
        attempts.ShouldAllBe(a => a.Status == WebhookDeliveryStatus.Failed);
        attempts.ShouldAllBe(a => a.HttpStatusCode == 500);

        // Then flip to 200 on a fresh create → exactly one Succeeded row for the new event.
        factory.Handler.ResponseStatusCode = HttpStatusCode.OK;
        var second = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name = "Recovers", laneId });
        second.EnsureSuccessStatusCode();

        await DrainQueueViaSeamAsync(factory);

        var all = await ReadDeliveryAttemptsAsync(factory);
        all.Count(r => r.Status == WebhookDeliveryStatus.Succeeded).ShouldBe(1);
    }

    // ── Scenario 9: deliveries endpoint admin/403 ────────────────────────────────

    [Fact]
    public async Task DeliveriesEndpoint_ReturnsPagedAttempts_FilteredByBoard_ForAdmin()
    {
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint, maxAttempts: 2), runDispatcher: false);
        factory.Handler.ResponseStatusCode = HttpStatusCode.InternalServerError;
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        var create = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name = "Logged", laneId });
        create.EnsureSuccessStatusCode();
        await DrainQueueViaSeamAsync(factory);   // 2 Failed attempts persisted

        // Admin: PagedResult of attempts, filtered to the board.
        var adminResponse = await client.GetAsync($"/api/v1/webhooks/deliveries?boardId={factory.DefaultBoardId}");
        adminResponse.EnsureSuccessStatusCode();
        var paged = await adminResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        paged.GetProperty("totalCount").GetInt32().ShouldBe(2);
        var items = paged.GetProperty("items");
        items.GetArrayLength().ShouldBe(2);
        items[0].GetProperty("status").GetString().ShouldBe("Failed");          // enum NAME, not ordinal
        items[0].GetProperty("eventType").GetString().ShouldBe("card.created");
        items[0].GetProperty("boardId").GetGuid().ShouldBe(factory.DefaultBoardId);

        // Newest-first ordering (the (BoardId, AttemptedAtUtc DESC) read).
        var firstAt = items[0].GetProperty("attemptedAtUtc").GetDateTimeOffset();
        var lastAt = items[items.GetArrayLength() - 1].GetProperty("attemptedAtUtc").GetDateTimeOffset();
        firstAt.ShouldBeGreaterThanOrEqualTo(lastAt);
    }

    [Fact]
    public async Task DeliveriesEndpoint_NonAdmin_Returns403()
    {
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint));
        var client = factory.CreateClient();
        var agent = await TestAuthHelper.CreateUserAsync(client, factory, "Agent", UserRole.AgentUser);

        TestAuthHelper.SetAuth(client, agent.AuthKey);
        var response = await client.GetAsync("/api/v1/webhooks/deliveries");
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ── Status endpoint (posture + counts) — admin-level/403, never the secret/URL ──

    [Fact]
    public async Task StatusEndpoint_ReturnsPostureAndCounts_ForAdmin_NeverSecretOrUrl()
    {
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint, secret: "shh"));
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);

        var response = await client.GetAsync("/api/v1/webhooks/status");
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync();
        var status = JsonDocument.Parse(raw).RootElement;

        // The evolved posture+counts shape. The v1 endpointConfigured/signed booleans read
        // the retiring config keys and would lie in the registry world, so they are gone.
        status.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        status.GetProperty("allowPrivateNetworkTargets").GetBoolean().ShouldBeFalse();
        status.GetProperty("subscriptionCount").GetInt32().ShouldBe(1);            // the config-migrated seed
        status.GetProperty("enabledSubscriptionCount").GetInt32().ShouldBe(1);

        // Counts + booleans ONLY — never the secret, never the URL.
        raw.ShouldNotContain("shh");
        raw.ShouldNotContain(_testEndpoint);
        raw.ShouldNotContain("sink.test");
    }

    [Fact]
    public async Task StatusEndpoint_ReportsEmptyRegistry_WhenUnconfigured()
    {
        await using var factory = await CreateFactoryAsync(BaseConfig(endpoint: null));
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);

        var response = await client.GetAsync("/api/v1/webhooks/status");
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);

        // No Webhooks:Endpoint → no migration seed → an empty registry.
        status.GetProperty("subscriptionCount").GetInt32().ShouldBe(0);
        status.GetProperty("enabledSubscriptionCount").GetInt32().ShouldBe(0);
        status.GetProperty("enabled").GetBoolean().ShouldBeTrue();   // the master switch defaults on
    }

    [Fact]
    public async Task StatusEndpoint_NonAdmin_Returns403()
    {
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint));
        var client = factory.CreateClient();
        var agent = await TestAuthHelper.CreateUserAsync(client, factory, "Agent2", UserRole.AgentUser);

        TestAuthHelper.SetAuth(client, agent.AuthKey);
        var response = await client.GetAsync("/api/v1/webhooks/status");
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ── The diagnostics are now admin-level (AgentAdministrator admitted) ──

    [Fact]
    public async Task StatusEndpoint_AgentAdministrator_Succeeds()
    {
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint));
        var client = factory.CreateClient();
        var agentAdmin = await TestAuthHelper.CreateUserAsync(client, factory, "AgentAdmin", UserRole.AgentAdministrator);

        TestAuthHelper.SetAuth(client, agentAdmin.AuthKey);
        var response = await client.GetAsync("/api/v1/webhooks/status");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeliveriesEndpoint_AgentAdministrator_Succeeds()
    {
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint));
        var client = factory.CreateClient();
        var agentAdmin = await TestAuthHelper.CreateUserAsync(client, factory, "AgentAdmin2", UserRole.AgentAdministrator);

        TestAuthHelper.SetAuth(client, agentAdmin.AuthKey);
        var response = await client.GetAsync("/api/v1/webhooks/deliveries");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeliveriesEndpoint_FilteredBySubscriptionId_ReturnsOnlyThatSubscription()
    {
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint, maxAttempts: 1), runDispatcher: false);
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);

        // The config-migrated seed is the one subscription; a card.created delivers to it.
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);
        var create = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name = "Filtered", laneId });
        create.EnsureSuccessStatusCode();
        await DrainQueueViaSeamAsync(factory);

        Guid seededId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
            seededId = (await db.WebhookSubscriptions.SingleAsync()).Id;
        }

        // Filter to the seeded subscription → its attempt(s); filter to a random id → none.
        var matched = await client.GetAsync($"/api/v1/webhooks/deliveries?subscriptionId={seededId}");
        matched.EnsureSuccessStatusCode();
        var matchedPaged = await matched.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        matchedPaged.GetProperty("totalCount").GetInt32().ShouldBeGreaterThan(0);
        matchedPaged.GetProperty("items")[0].GetProperty("subscriptionId").GetGuid().ShouldBe(seededId);

        var unmatched = await client.GetAsync($"/api/v1/webhooks/deliveries?subscriptionId={Guid.NewGuid()}");
        unmatched.EnsureSuccessStatusCode();
        var unmatchedPaged = await unmatched.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        unmatchedPaged.GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    // ── Edge cases ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SlowEndpoint_DoesNotBlockOrFailTheMutation()
    {
        // The sink hangs ~3s; the create must still return promptly (delivery is never inline).
        var config = BaseConfig(_testEndpoint);
        config["Webhooks:DeliveryTimeout"] = "00:00:01";   // 1s per-POST timeout
        await using var factory = await CreateFactoryAsync(config);
        factory.Handler.ResponseDelay = TimeSpan.FromSeconds(3);

        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name = "Slow Sink", laneId });
        sw.Stop();

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));   // independent of the 3s sink latency
    }

    [Fact]
    public async Task FailedMutation_EmitsNoEvent()
    {
        // A create with an invalid label → 400; enqueue is after a successful SaveChanges, so no
        // event is ever delivered.
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint));
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        var response = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new
        {
            name = "Bad Label",
            laneId,
            labelIds = new[] { Guid.NewGuid() },   // a label that does not exist on this board
        });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await Task.Delay(800);
        factory.Handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task Secret_IsNeverEchoed_InDeliveriesOrStatusOrBody()
    {
        const string secret = "top-secret-value";
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint, secret), runDispatcher: false);
        factory.Handler.ResponseStatusCode = HttpStatusCode.InternalServerError;
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        var create = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name = "Secret Check", laneId });
        create.EnsureSuccessStatusCode();
        await DrainQueueViaSeamAsync(factory);

        // The secret appears in no response body, no event payload, no delivery row.
        var status = await (await client.GetAsync("/api/v1/webhooks/status")).Content.ReadAsStringAsync();
        var deliveries = await (await client.GetAsync("/api/v1/webhooks/deliveries")).Content.ReadAsStringAsync();
        var requestBody = factory.Handler.Requests.Count > 0
            ? Encoding.UTF8.GetString(factory.Handler.Requests[0].Body)
            : string.Empty;

        status.ShouldNotContain(secret);
        deliveries.ShouldNotContain(secret);
        requestBody.ShouldNotContain(secret);
    }

    [Fact]
    public async Task AttemptedAtUtc_RoundTripsThroughSortableUtcConverter_NewestFirst()
    {
        await using var factory = await CreateFactoryAsync(BaseConfig(_testEndpoint, maxAttempts: 3), runDispatcher: false);
        factory.Handler.ResponseStatusCode = HttpStatusCode.InternalServerError;
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        var create = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name = "Time Order", laneId });
        create.EnsureSuccessStatusCode();
        await DrainQueueViaSeamAsync(factory);

        // Read back ordered newest-first via the converter-backed column — the timestamps must be
        // genuine DateTimeOffsets in descending chronological order (the (BoardId, AttemptedAtUtc)
        // index read), proving the sortable-UTC converter round-trips and orders correctly.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var ordered = await db.WebhookDeliveryAttempts
            .OrderByDescending(x => x.AttemptedAtUtc)
                .ToListAsync();

        ordered.Count.ShouldBeGreaterThanOrEqualTo(3);
        for (var i = 1; i < ordered.Count; i++)
        {
            ordered[i - 1].AttemptedAtUtc.ShouldBeGreaterThanOrEqualTo(ordered[i].AttemptedAtUtc);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<CapturedRequest> WaitForOneRequestAsync(CapturingHttpMessageHandler handler)
    {
        // Await the dispatcher's actual delivery rather than polling a wall clock — the capture
        // signals the instant the POST lands, so a loaded runner can no longer beat a fixed deadline
        // (the flake this replaces). The timeout is a backstop against a genuine non-delivery hang,
        // not a race deadline: on success the wait ends in milliseconds.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            return await handler.WaitForNextRequestAsync(cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException("Expected a webhook delivery POST to be captured within 30s, but none arrived.", ex);
        }
    }

    private static async Task<int> CountDeliveryAttemptsAsync(WebhookDeliveryFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.WebhookDeliveryAttempts.CountAsync();
    }

    private static async Task<List<WebhookDeliveryAttempt>> ReadDeliveryAttemptsAsync(WebhookDeliveryFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.WebhookDeliveryAttempts
            .OrderBy(x => x.Attempt)
                .ToListAsync();
    }

}
