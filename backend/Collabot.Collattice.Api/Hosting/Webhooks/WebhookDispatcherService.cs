using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Collabot.Collattice.Api.Hosting.Webhooks;

// Drains the in-memory webhook queue and delivers each enriched event over HTTP. The third
// TempCardSweepService-shaped BackgroundService.
//
// Delivery is async-after-save and NEVER inline: the mutation handler enqueued and returned
// long ago; a slow or dead endpoint can never degrade the board (a heavily-concurrent project).
// The dispatcher owns the bounded-retry loop, the persisted WebhookDeliveryAttempt log, and the
// loud drop on final failure (the difference between "acceptable v1 loss" and "silent black hole"
// is whether the drop is observable). The actual POST — serialize-once + sign + headers — lives
// behind IWebhookSender (a typed HttpClient seam, stubbable in tests). The dispatcher is DB-free
// for the EVENT — the enriched BoardEvent is a self-contained POJO, fully resolved at emit-time.
// The DB is touched only to persist attempt rows.
//
// Like TempCardSweepService, the per-event delivery logic is a deterministic static seam
// (DeliverEventAsync — takes a BoardDbContext directly) and the PeriodicTimer drain loop is thin
// framework plumbing. Tests drive DeliverEventAsync directly against a scope's DbContext rather
// than racing the running hosted service against a shared in-memory connection.
//
// sealed: a DI-registered leaf hosted service; subclassing a BackgroundService's ExecuteAsync
// loop is an inheritance trap we have no reason to allow (matches TempCardSweepService /
// UpdateCheckService).
internal sealed class WebhookDispatcherService
(
    WebhookQueue queue,
    IWebhookSender sender,
    IServiceProvider services,
    IOptions<WebhookSettings> settings,
    ILogger<WebhookDispatcherService> logger
) : BackgroundService
{
    // How often the in-memory queue is polled. The queue is a ConcurrentQueue with no blocking
    // dequeue, so the dispatcher polls; a short cadence keeps delivery responsive at trivial cost
    // (a no-op TryDequeue when empty). Not operator-tunable — an internal liveness knob, not a
    // delivery-semantics setting.
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(500);

    private readonly WebhookQueue _queue = queue
        ?? throw new ArgumentNullException(nameof(queue));
    private readonly IWebhookSender _sender = sender
        ?? throw new ArgumentNullException(nameof(sender));
    private readonly IServiceProvider _services = services
        ?? throw new ArgumentNullException(nameof(services));
    private readonly WebhookSettings _settings = settings.Value;
    private readonly ILogger<WebhookDispatcherService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    // Delivery now routes to the subscription registry, not a single configured endpoint, so
    // "configured" collapses to the global master kill-switch. Endpoint is no longer read for
    // delivery (it survives only as the one-time config-migration seed input). Each per-subscription
    // Enabled is ANDed with this at fan-out.
    private bool IsConfigured => _settings.Enabled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStartupState();

        // Dark deployments still drain the queue (so it does not grow unbounded) but make no
        // outbound calls and persist no attempt rows — the IsConfigured guard below no-ops.
        using var timer = new PeriodicTimer(_pollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await DrainSafelyAsync(stoppingToken);
        }
    }

    // The startup-confirmation log line (operator on-ramp). The registry replaced the single
    // configured endpoint, so this reports the master-switch posture only — NEVER a URL or secret,
    // and deliberately NO subscription-count DB read at startup (a startup-time query on the WAF's
    // shared in-memory connection races the test thread; the registry's contents surface through the
    // management endpoints, not this log line).
    private void LogStartupState()
    {
        if (_settings.Enabled)
        {
            _logger.LogInformation("Webhooks enabled — delivery routes to the subscription registry.");
        }
        else
        {
            _logger.LogInformation("Webhooks disabled (Webhooks:Enabled = false).");
        }
    }

    private async Task DrainSafelyAsync(CancellationToken ct)
    {
        try
        {
            while (_queue.TryDequeue(out var boardEvent) && boardEvent is not null)
            {
                // Dark: the event was dequeued (so the queue stays bounded) but no delivery is
                // attempted and no attempt row is persisted.
                if (!IsConfigured)
                {
                    continue;
                }

                await using var scope = _services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

                await DeliverEventAsync(boardEvent, _sender, db, _settings, _logger, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — expected, let the loop exit.
            throw;
        }
        catch (Exception ex)
        {
            // A transient failure draining one tick must not tear down the loop.
            _logger.LogError(ex, "Webhook dispatch tick failed; will retry on the next poll.");
        }
    }

    // Delivers ONE event to EVERY enabled subscription whose selection matches its type (v1
    // dialed a single configured endpoint, v2 fans out to the registry). A deterministic static
    // seam the tests drive directly, the same shape as the temp-card sweep: it loads the matching
    // subscriptions and runs the full per-subscription attempt loop. The caller supplies one
    // DbContext scope per event, and the master switch runs ahead of it.
    public static async Task DeliverEventAsync
    (
        BoardEvent boardEvent,
        IWebhookSender sender,
        BoardDbContext db,
        WebhookSettings settings,
        ILogger logger,
        CancellationToken ct
    )
    {
        // Load the enabled rows with a translatable predicate, then match the selection in CLR
        // memory. A relational predicate over the value-converted EventTypes column does not
        // translate to SQL, so the selection match must happen in memory after loading.
        var enabled = await db.WebhookSubscriptions
            .Where(s => s.Enabled)
                .ToListAsync(ct);

        var matches = enabled
            .Where(s => WebhookEventTypes.Matches(s.EventTypes, boardEvent.EventType))
                .ToList();

        foreach (var subscription in matches)
        {
            // One subscription's failure must not block the next — each fans out independently.
            var target = new WebhookTarget(subscription.Url, subscription.Secret);
            await DeliverToSubscriptionAsync(boardEvent, target, subscription.Id, sender, db, settings, logger, ct);
        }
    }

    // Delivers one event to ONE subscription with bounded retry, persisting every attempt (tagged
    // with the SubscriptionId) and logging a loud drop on final failure. This is v1's per-event
    // attempt loop, now per-subscription.
    private static async Task DeliverToSubscriptionAsync
    (
        BoardEvent boardEvent,
        WebhookTarget target,
        Guid subscriptionId,
        IWebhookSender sender,
        BoardDbContext db,
        WebhookSettings settings,
        ILogger logger,
        CancellationToken ct
    )
    {
        var maxAttempts = Math.Max(1, settings.MaxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Backoff BEFORE the 2nd+ attempt (attempt 1 is immediate). Short exponential +
            // jitter. A slow endpoint is bounded by the per-POST HttpClient.Timeout in the sender.
            if (attempt > 1)
            {
                await DelayBeforeRetryAsync(settings, attempt, ct);
            }

            var result = await sender.SendAsync(boardEvent, target, ct);

            db.WebhookDeliveryAttempts.Add(new WebhookDeliveryAttempt
            {
                Id = Guid.NewGuid(),
                SubscriptionId = subscriptionId,
                EventId = boardEvent.EventId,
                EventType = boardEvent.EventType,
                BoardId = boardEvent.BoardId,
                Attempt = attempt,
                Status = result.Succeeded ? WebhookDeliveryStatus.Succeeded : WebhookDeliveryStatus.Failed,
                HttpStatusCode = result.StatusCode,
                Error = result.Succeeded ? null : TruncateHead(result.Error),
                AttemptedAtUtc = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(ct);

            if (result.Succeeded)
            {
                return;
            }

            if (attempt == maxAttempts)
            {
                // Loud, persisted drop on final failure (the observability floor). The attempt
                // rows are already written above; this is the operator-visible log entry.
                logger.LogWarning
                (
                    "Webhook delivery failed after {Attempts} attempt(s) for event {EventId} ({EventType}) on board {BoardId} to subscription {SubscriptionId}: {Error}",
                    maxAttempts,
                    boardEvent.EventId,
                    boardEvent.EventType,
                    boardEvent.BoardId,
                    subscriptionId,
                    result.Error
                );
            }
        }
    }

    private static async Task DelayBeforeRetryAsync(WebhookSettings settings, int attempt, CancellationToken ct)
    {
        // attempt 2 => ~base, attempt 3 => ~4x base (base * 4^(attempt-2)), plus up to 250ms jitter
        // so a burst of failures does not retry in lockstep. base is Webhooks:RetryBackoffBase.
        var multiplier = Math.Pow(4, attempt - 2);
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
        var delay = (settings.RetryBackoffBase * multiplier) + jitter;

        await Task.Delay(delay, ct);
    }

    // First N chars, not a mid-cut: the actionable signal (HTTP status + reason, TLS/DNS/
    // connection-refused error class) lives at the head of the message. MaxLength(500) on the
    // column.
    private static string? TruncateHead(string? error) =>
        error is { Length: > 500 } ? error[..500] : error;
}
