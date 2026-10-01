using System.Reflection;

namespace Collabot.Collattice.Api.Hosting.UpdateCheck;

// Server-side cache for the update check. The hosted service refreshes this
// out-of-band on its timer; the /version/status endpoint reads from it and never blocks on a
// live GitHub call. Single-writer (only UpdateCheckService writes) / many-reader (request
// threads), so a volatile reference swap of an immutable snapshot is sufficient — readers
// always see a consistent prior or next snapshot, never a torn one.
internal class VersionStatusCache
{
    private sealed record Snapshot(SemVer Current, LatestVersionResult? Latest, DateTimeOffset? LastChecked);

    private volatile Snapshot _snapshot;

    public VersionStatusCache()
        : this(ResolveCurrentVersion())
    {
    }

    // The current version is normally resolved from the running assembly (the parameterless
    // ctor the DI container uses). This overload lets a test pin a known current version to
    // exercise the comparison and dev-sentinel logic — the running test assembly is itself
    // unstamped (0.0.0), so without this seam every status would short-circuit on the sentinel.
    internal VersionStatusCache(SemVer current)
    {
        _snapshot = new Snapshot(current, Latest: null, LastChecked: null);
    }

    // The running assembly's stamped version (publish.yml stamps AssemblyInformationalVersion
    // from the release tag; build metadata after '+' is dropped, matching /version). An
    // unstamped dev build resolves to the 0.0.0 sentinel and is never nagged.
    public SemVer Current => _snapshot.Current;

    public void SetLatest(LatestVersionResult latest, DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(latest);

        var prior = _snapshot;
        _snapshot = prior with { Latest = latest, LastChecked = checkedAt };
    }

    public VersionStatus GetStatus()
    {
        var snapshot = _snapshot;
        var current = snapshot.Current;

        var updateAvailable = false;
        string? latestText = null;
        string? releaseUrl = null;

        if (snapshot.Latest is not null && SemVer.TryParse(snapshot.Latest.Version, out var latest))
        {
            latestText = latest.ToString();
            releaseUrl = snapshot.Latest.ReleaseUrl;

            // Never nag a dev/unstamped instance: 0.0.0 is not a released version, so
            // "newer available" is not a meaningful prompt to put in front of the operator.
            updateAvailable = !current.IsDevSentinel && latest > current;
        }

        return new VersionStatus
        (
            Current: current.ToString(),
            Latest: latestText,
            UpdateAvailable: updateAvailable,
            ReleaseUrl: releaseUrl,
            LastChecked: snapshot.LastChecked
        );
    }

    private static SemVer ResolveCurrentVersion()
    {
        var raw = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "0.0.0";

        return SemVer.TryParse(raw, out var parsed) ? parsed : SemVer.DevSentinel;
    }
}
