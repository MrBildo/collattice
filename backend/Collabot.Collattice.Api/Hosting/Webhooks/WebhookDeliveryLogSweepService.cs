using Collabot.Collattice.Api.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Collabot.Collattice.Api.Hosting.Webhooks;

// Sweeps old webhook delivery-attempt rows. The registry's catalog × subscription fan-out
// makes the WebhookDeliveryAttempt log grow faster than v1's single endpoint, so a retention cap
// keeps it bounded. The TempCardSweepService-shaped background service: the per-tick delete is a
// deterministic static seam (SweepAsync — takes a BoardDbContext directly) the tests drive without
// racing the running loop against the shared in-memory connection. Set-based
// ExecuteDeleteAsync (predicate in the WHERE clause) so the delete is race-safe with concurrent
// attempt writes — no read-then-write gap. Attempts whose subscription was deleted (SubscriptionId
// nulled at SetNull) still age out by time, so the audit log never accumulates orphans.
//
// sealed: a DI-registered leaf hosted service — subclassing a BackgroundService's ExecuteAsync loop
// is an inheritance trap we have no reason to allow (matches TempCardSweepService / the dispatcher).
internal sealed class WebhookDeliveryLogSweepService
(
    IServiceProvider services,
    IOptions<WebhookSettings> settings,
    ILogger<WebhookDeliveryLogSweepService> logger
) : BackgroundService
{
    // Daily is plenty — the retention window is measured in days. Not operator-tunable: an internal
    // liveness cadence, not a delivery-semantics setting (mirrors the dispatcher's poll interval).
    // The retention window itself is Webhooks:DeliveryLogRetentionDays.
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromHours(24);

    private readonly IServiceProvider _services = services
        ?? throw new ArgumentNullException(nameof(services));
    private readonly WebhookSettings _settings = settings.Value;
    private readonly ILogger<WebhookDeliveryLogSweepService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 0 (or negative) keeps the log forever — the sweep stays dormant.
        if (_settings.DeliveryLogRetentionDays <= 0)
        {
            _logger.LogInformation("Webhook delivery-log retention disabled (Webhooks:DeliveryLogRetentionDays <= 0).");
            return;
        }

        _logger.LogInformation
        (
            "Webhook delivery-log sweep started — retaining {Days} day(s), interval {Interval}.",
            _settings.DeliveryLogRetentionDays,
            _sweepInterval
        );

        // No startup sweep: retention is not time-urgent, and a startup DB sweep would add a needless
        // collision surface with the WAF's shared in-memory connection in tests (a BackgroundService
        // should do no startup DB work the test thread can race). The first sweep is one interval
        // after boot, the same shape as the temp-card sweep.
        using var timer = new PeriodicTimer(_sweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSweepSafelyAsync(stoppingToken);
        }
    }

    private async Task RunSweepSafelyAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_settings.DeliveryLogRetentionDays);
            var deleted = await SweepAsync(db, cutoff, ct);

            if (deleted > 0)
            {
                _logger.LogInformation
                (
                    "Webhook delivery-log sweep removed {Count} attempt row(s) older than {Cutoff:O}.",
                    deleted,
                    cutoff
                );
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — expected, let the loop exit.
            throw;
        }
        catch (Exception ex)
        {
            // A transient failure on one tick must not tear down the background loop.
            _logger.LogError(ex, "Webhook delivery-log sweep tick failed; will retry on the next interval.");
        }
    }

    // Deletes every delivery-attempt row older than the cutoff in one set-based statement. The
    // AttemptedAtUtc predicate lives in the WHERE clause, so the delete is evaluated against
    // committed state at execution time — a row written between any prior read and this delete is
    // simply not matched, no read-then-write gap. The AttemptedAtUtc value converter stores a
    // sortable ISO-8601 string, so the relational `<` translates to SQL.
    // Returns the number of rows removed.
    public static async Task<int> SweepAsync(BoardDbContext db, DateTimeOffset cutoff, CancellationToken ct = default) =>
        await db.WebhookDeliveryAttempts
            .Where(a => a.AttemptedAtUtc < cutoff)
                .ExecuteDeleteAsync(ct);
}
