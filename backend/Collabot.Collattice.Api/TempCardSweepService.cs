using Collabot.Collattice.Api.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Collabot.Collattice.Api;

// Sweeps orphaned temp cards. Temp cards (IsTemp = true, Number = 0) are created
// by the interactive create-temp → finalize/cancel flow; a browser closing mid-flow leaves
// the card stranded in temp state, filtered out of every read path and never cleaned up.
// This periodic sweep deletes temp cards older than the configured TTL. Children
// (attachments, comments, label assignments) are DB-resident and cascade-delete at the
// database level (BoardDbContext FK config), so no manual child cleanup is needed.
//
// sealed: a DI-registered leaf hosted service — there is no subtype, and subclassing a
// BackgroundService's ExecuteAsync loop is an inheritance trap we have no reason to allow.
internal sealed class TempCardSweepService
(
    IServiceProvider services,
    IOptions<TempCardSweepSettings> settings,
    ILogger<TempCardSweepService> logger
) : BackgroundService
{
    private readonly IServiceProvider _services = services
        ?? throw new ArgumentNullException(nameof(services));
    private readonly TempCardSweepSettings _settings = settings.Value;
    private readonly ILogger<TempCardSweepService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Temp-card sweep disabled (TempCardSweep:Enabled = false).");
            return;
        }

        _logger.LogInformation
        (
            "Temp-card sweep started — TTL {Ttl}, interval {Interval}.",
            _settings.Ttl,
            _settings.SweepInterval
        );

        // No startup sweep: the first sweep runs one interval after boot, like every other
        // database-touching hosted service here. Read paths already hide temp cards, so after a
        // restart an orphan lingers at most one interval longer, and the one place it shows is an
        // apparently empty lane or size refusing deletion until a sweep removes it. A startup
        // sweep would also be database work racing the rest of startup, which in tests is a test
        // thread sharing one connection.
        using var timer = new PeriodicTimer(_settings.SweepInterval);
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

            var cutoff = DateTimeOffset.UtcNow - _settings.Ttl;
            var deleted = await SweepAsync(db, cutoff, ct);

            if (deleted > 0)
            {
                _logger.LogInformation
                (
                    "Temp-card sweep removed {Count} orphaned temp card(s) older than {Cutoff:O}.",
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
            _logger.LogError(ex, "Temp-card sweep tick failed; will retry on the next interval.");
        }
    }

    // Deletes every temp card created before the cutoff in a single set-based statement.
    // The IsTemp predicate lives in the WHERE clause, so the delete is evaluated against
    // committed state at execution time: a card that finalized (IsTemp flipped false)
    // between any prior read and this delete simply does not match and is left untouched —
    // no read-then-write gap, no false delete, idempotent by construction. Returns the
    // number of cards removed. Children cascade at the database level.
    public static async Task<int> SweepAsync(BoardDbContext db, DateTimeOffset cutoff, CancellationToken ct = default) =>
        await db.Cards
            .Where(c => c.IsTemp && c.CreatedAtUtc < cutoff)
                .ExecuteDeleteAsync(ct);
}
