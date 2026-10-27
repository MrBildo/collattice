using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Collabot.Collattice.Api.Hosting.Webhooks;

// The shared CRUD + validation core for webhook subscriptions. The REST endpoints and the
// MCP tools both delegate here — so the SSRF/URL validation, the events-non-empty
// rule, the secret set/keep/clear contract, and the secret-free read projection are defined ONCE
// and are un-bypassable by construction. This is deliberately NOT the LabelTools pattern (which
// re-implements CRUD inline per surface — the codebase's top bug class); the precedent is
// CardSummaryBuilder / SearchHelper / LaneReorderHelper. On a security-sensitive surface (SSRF),
// a per-surface re-implementation would be a security bug, not mere inconsistency.
//
// Validation reads AllowPrivateNetworkTargets via IOptions (startup-bound), so registration and
// the connect-time guard read the SAME value and agree. The secret is write-only at the
// API surface: ToView projects `signed: bool` and NEVER the secret string.
internal class WebhookSubscriptionStore
(
    BoardDbContext db,
    IOptions<WebhookSettings> settings
)
{
    private readonly BoardDbContext _db = db
        ?? throw new ArgumentNullException(nameof(db));
    private readonly WebhookSettings _settings = settings.Value;

    public async Task<WebhookSubscriptionView> CreateAsync(WebhookSubscriptionInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var events = NormalizeAndValidateEvents(input.Events);
        var url = RequireUrl(input.Url);
        await SsrfGuard.ValidateForRegistrationAsync(url, _settings.AllowPrivateNetworkTargets, ct);

        var subscription = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Name = NormalizeName(input.Name),
            Url = url,
            Secret = NormalizeSecret(input.Secret),
            Enabled = input.Enabled ?? true,
            EventTypes = events,
        };

        _db.WebhookSubscriptions.Add(subscription);
        await _db.SaveChangesAsync(ct);

        // A fresh row has no delivery history — zero metrics, no extra query.
        return ToView(subscription, SubscriptionMetrics.Empty);
    }

    public async Task<IReadOnlyList<WebhookSubscriptionView>> ListAsync(CancellationToken ct)
    {
        var subscriptions = await _db.WebhookSubscriptions.ToListAsync(ct);
        if (subscriptions.Count == 0)
        {
            return [];
        }

        var ids = subscriptions
            .Select(s => s.Id)
                .ToList();

        // Metrics computed on-read (never persisted — no denormalized counters that would couple
        // every delivery write to a subscription update on a heavily-concurrent board). "Reject
        // N+1" means one endpoint, not one SQL statement: one projected read of the
        // relevant attempt rows, grouped in CLR. At registry scale (a handful of subscriptions,
        // bounded by the retention sweep) this is trivial; the (SubscriptionId, AttemptedAtUtc)
        // index serves it.
        var attempts = await _db.WebhookDeliveryAttempts
            .Where(a => a.SubscriptionId != null && ids.Contains(a.SubscriptionId.Value))
                .Select(a => new AttemptMetric(a.SubscriptionId!.Value, a.Status, a.AttemptedAtUtc))
                    .ToListAsync(ct);

        var metricsBySubscription = attempts
            .GroupBy(a => a.SubscriptionId)
                .ToDictionary(g => g.Key, BuildMetrics);

        return
        [
            .. subscriptions.Select(s => ToView(s, metricsBySubscription.GetValueOrDefault(s.Id, SubscriptionMetrics.Empty)))
        ];
    }

    public async Task<WebhookSubscriptionView?> GetAsync(Guid id, CancellationToken ct)
    {
        var subscription = await _db.WebhookSubscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);
        return subscription is null ? null : await WithMetricsAsync(subscription, ct);
    }

    public async Task<WebhookSubscriptionView?> UpdateAsync(Guid id, WebhookSubscriptionPatch patch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var subscription = await _db.WebhookSubscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (subscription is null)
        {
            return null;
        }

        // URL: re-run the SSRF registration check ONLY when the URL is changing. A name/
        // enabled/secret-only PATCH must NOT re-validate an unchanged URL — otherwise the migrated
        // private-URL subscription could not be disabled (PATCH { enabled:false }) with the flag
        // off, a surprising asymmetry on the exact row the deliberate break targets.
        if (patch.Url is not null)
        {
            var url = RequireUrl(patch.Url);
            await SsrfGuard.ValidateForRegistrationAsync(url, _settings.AllowPrivateNetworkTargets, ct);
            subscription.Url = url;
        }

        if (patch.Events is not null)
        {
            // Replace-only: assign a fresh list on update rather than mutating in place. A value
            // comparer is configured (BoardDbContext) so an in-place edit would also be detected, but
            // replace-only is the store's set-the-selection semantics.
            subscription.EventTypes = NormalizeAndValidateEvents(patch.Events);
        }

        if (patch.Enabled is not null)
        {
            subscription.Enabled = patch.Enabled.Value;
        }

        if (patch.Name is not null)
        {
            subscription.Name = NormalizeName(patch.Name);
        }

        ApplySecretChange(subscription, patch);

        await _db.SaveChangesAsync(ct);
        return await WithMetricsAsync(subscription, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var subscription = await _db.WebhookSubscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (subscription is null)
        {
            return false;
        }

        // The delivery-attempt history survives (SubscriptionId is SetNull at the FK) — the audit
        // log outlives the subscription.
        _db.WebhookSubscriptions.Remove(subscription);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // Secret set/keep/clear (identical on REST PATCH and MCP update). clearSecret wins; else
    // a provided non-blank secret replaces; else (omitted/blank) the secret is unchanged. Blank is
    // treated as "not provided" because an empty string is indistinguishable from absent in many
    // client serializers — clearing is the explicit clearSecret flag.
    private static void ApplySecretChange(WebhookSubscription subscription, WebhookSubscriptionPatch patch)
    {
        if (patch.ClearSecret)
        {
            subscription.Secret = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(patch.Secret))
        {
            subscription.Secret = patch.Secret;
        }
    }

    private async Task<WebhookSubscriptionView> WithMetricsAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        var attempts = await _db.WebhookDeliveryAttempts
            .Where(a => a.SubscriptionId == subscription.Id)
                .Select(a => new AttemptMetric(subscription.Id, a.Status, a.AttemptedAtUtc))
                    .ToListAsync(ct);

        return ToView(subscription, BuildMetrics(attempts));
    }

    private static SubscriptionMetrics BuildMetrics(IEnumerable<AttemptMetric> attempts)
    {
        var list = attempts.ToList();
        if (list.Count == 0)
        {
            return SubscriptionMetrics.Empty;
        }

        var success = list.Count(a => a.Status == WebhookDeliveryStatus.Succeeded);
        var failure = list.Count(a => a.Status == WebhookDeliveryStatus.Failed);

        var latest = list
            .OrderByDescending(a => a.AttemptedAtUtc)
                .First();

        return new SubscriptionMetrics(success, failure, latest.Status.ToString(), latest.AttemptedAtUtc);
    }

    // The secret-free read projection — the primary secret-leak guard. NEVER returns the entity; the
    // secret becomes the `signed` boolean and nothing else. Every read path (REST list/get, MCP
    // list, metrics enrichment) funnels through here, so the write-only-secret rule has teeth.
    private static WebhookSubscriptionView ToView(WebhookSubscription subscription, SubscriptionMetrics metrics) =>
        new
        (
            subscription.Id,
            subscription.Name,
            subscription.Url,
            subscription.Enabled,
            [.. subscription.EventTypes],
            !string.IsNullOrWhiteSpace(subscription.Secret),
            metrics.SuccessCount,
            metrics.FailureCount,
            metrics.LastDeliveryStatus,
            metrics.LastDeliveryAtUtc
        );

    private static string RequireUrl(string url)
    {
        var trimmed = url?.Trim() ?? string.Empty;
        return trimmed.Length == 0
            ? throw new WebhookValidationException("A webhook subscription requires a URL.")
            : trimmed;
    }

    private static string? NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static string? NormalizeSecret(string? secret) =>
        string.IsNullOrWhiteSpace(secret) ? null : secret;

    private static List<string> NormalizeAndValidateEvents(IReadOnlyList<string>? events)
    {
        var normalized = (events ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim())
                .Distinct(StringComparer.Ordinal)
                    .ToList();

        if (normalized.Count == 0)
        {
            // Empty is never "all" — a subscription must explicitly select at least one event.
            throw new WebhookValidationException("A webhook subscription must select at least one event type.");
        }

        // The wildcard stands alone: if present, it IS the selection (co-listed explicit types
        // are redundant under "all current and future").
        if (normalized.Contains(WebhookEventTypes.Wildcard, StringComparer.Ordinal))
        {
            return [WebhookEventTypes.Wildcard];
        }

        var unknown = normalized
            .Where(e => !WebhookEventTypes.All.Contains(e))
                .ToList();

        return unknown.Count > 0
            ? throw new WebhookValidationException
            (
                $"Unknown event type(s): {string.Join(", ", unknown)}. "
                + $"Valid event types: {string.Join(", ", WebhookEventTypes.All)} (or \"{WebhookEventTypes.Wildcard}\")."
            )
            : normalized;
    }

    // On-read delivery metrics for one subscription. Never persisted. Private to the store — it is
    // an implementation detail of the read projection.
    private sealed record SubscriptionMetrics
    (
        int SuccessCount,
        int FailureCount,
        string? LastDeliveryStatus,
        DateTimeOffset? LastDeliveryAtUtc
    )
    {
        public static readonly SubscriptionMetrics Empty = new(0, 0, null, null);
    }

    // A lightweight projection of the columns the metrics computation needs — avoids loading full
    // WebhookDeliveryAttempt entities for the grouped counts + latest-per-subscription read.
    private sealed record AttemptMetric
    (
        Guid SubscriptionId,
        WebhookDeliveryStatus Status,
        DateTimeOffset AttemptedAtUtc
    );
}

// Create input for a subscription. Events is the selection — validated as non-empty, of known
// types or the wildcard. Secret is write-only: set on create, never read back. Enabled defaults to
// true when omitted. (internal — consumed by the in-assembly REST/MCP surfaces, not a public API.)
internal sealed record WebhookSubscriptionInput
(
    string Url,
    IReadOnlyList<string> Events,
    string? Secret,
    bool? Enabled,
    string? Name
);

// Partial-update patch. A null field means unchanged. The secret follows set-keep-clear: a non-
// blank Secret replaces, ClearSecret clears, both absent leaves it unchanged. Url is re-validated
// for SSRF only when present.
internal sealed record WebhookSubscriptionPatch
(
    string? Url,
    IReadOnlyList<string>? Events,
    string? Secret,
    bool ClearSecret,
    bool? Enabled,
    string? Name
);

// The secret-free read projection. Signed is the only secret-derived field — true when a secret is
// set — and the secret string itself appears nowhere. Metrics are computed on-read.
internal sealed record WebhookSubscriptionView
(
    Guid Id,
    string? Name,
    string Url,
    bool Enabled,
    IReadOnlyList<string> Events,
    bool Signed,
    int SuccessCount,
    int FailureCount,
    string? LastDeliveryStatus,
    DateTimeOffset? LastDeliveryAtUtc
);
