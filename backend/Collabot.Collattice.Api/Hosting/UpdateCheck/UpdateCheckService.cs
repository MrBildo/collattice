using Microsoft.Extensions.Options;

namespace Collabot.Collattice.Api.Hosting.UpdateCheck;

// Polls the latest-version source on a timer and refreshes the server-side cache.
// One egress per instance per cadence regardless of how many browsers/tabs are connected —
// the concurrency profile of the board makes per-client polling wrong, so the fact (identical
// for every client of one instance) is fetched once here. When UpdateCheck:Enabled is false
// the kill switch gates this off entirely — the service returns immediately and no outbound
// call is ever made.
//
// sealed: a DI-registered leaf hosted service; subclassing a BackgroundService's ExecuteAsync
// loop is an inheritance trap we have no reason to allow (matches TempCardSweepService).
internal sealed class UpdateCheckService
(
    ILatestVersionSource source,
    VersionStatusCache cache,
    IOptions<UpdateCheckSettings> settings,
    ILogger<UpdateCheckService> logger
) : BackgroundService
{
    private readonly ILatestVersionSource _source = source
        ?? throw new ArgumentNullException(nameof(source));
    private readonly VersionStatusCache _cache = cache
        ?? throw new ArgumentNullException(nameof(cache));
    private readonly UpdateCheckSettings _settings = settings.Value;
    private readonly ILogger<UpdateCheckService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Update check disabled (UpdateCheck:Enabled = false); no outbound version checks will be made.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _settings.IntervalHours));

        _logger.LogInformation
        (
            "Update check started — source repository {Repository}, interval {Interval}.",
            _settings.Repository,
            interval
        );

        // Poll once on startup, then on each interval tick.
        await PollSafelyAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PollSafelyAsync(stoppingToken);
        }
    }

    private async Task PollSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var latest = await _source.GetLatestAsync(cancellationToken);

            // Fail-quiet: a failed poll returns null and leaves the last good cached
            // value in place. The absence of a fresh result is the correct degraded state, not
            // an error to surface.
            if (latest is not null)
            {
                _cache.SetLatest(latest, DateTimeOffset.UtcNow);
                _logger.LogInformation("Update check: latest release is {Version}.", latest.Version);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown — expected, let the loop exit.
            throw;
        }
        catch (Exception ex)
        {
            // A transient failure on one tick must not tear down the background loop.
            _logger.LogInformation(ex, "Update check tick failed; will retry on the next interval.");
        }
    }
}
