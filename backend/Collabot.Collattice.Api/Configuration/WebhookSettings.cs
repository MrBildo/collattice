namespace Collabot.Collattice.Api.Configuration;

// Outbound webhook delivery configuration. v2 moved delivery from a single
// configured endpoint to the subscription registry (WebhookSubscription rows); Endpoint/Secret
// now feed only the one-time config-migration seed. The remaining knobs are global delivery
// policy. Resolved through the standard config stack (env Section__Key > appsettings.json >
// hardcoded default).
public class WebhookSettings
{
    public const string SectionName = "Webhooks";

    // SEED-ONLY. The v1 single outbound endpoint. On the first v2 boot it is migrated into a
    // subscription row (gated on an empty subscription table) and is NO LONGER read for delivery —
    // the registry is the source of truth. Overridable via Webhooks__Endpoint. Unset after the
    // cutover to avoid re-seeding if all subscriptions are later deleted.
    public string? Endpoint { get; init; }

    // SEED-ONLY. The v1 shared secret, carried verbatim into the migrated subscription. No
    // longer read for delivery (each subscription carries its own secret). Overridable via
    // Webhooks__Secret. (Secret — never logged, never echoed in any response or event payload.)
    public string? Secret { get; init; }

    // Global master switch for all webhook delivery. Overridable via Webhooks__Enabled. A
    // subscription delivers only when the master switch and its own Enabled flag are both on.
    public bool Enabled { get; init; } = true;

    // Per-POST timeout (the typed HttpClient.Timeout). A slow endpoint is treated as a
    // failed attempt, not waited on. Overridable via Webhooks__DeliveryTimeout.
    public TimeSpan DeliveryTimeout { get; init; } = TimeSpan.FromSeconds(5);

    // Max delivery attempts (initial + retries). Overridable via Webhooks__MaxAttempts.
    public int MaxAttempts { get; init; } = 3;

    // The first retry's backoff (attempt 1 is immediate; the wait before attempt 2). Subsequent
    // retries grow it ~4x with jitter (attempt 2 ~= this, attempt 3 ~= 4x this). The "config-
    // tunable" knob the delivery-semantics contract names; a test can set it near-zero to exercise
    // the retry loop without real wall-clock waits. Overridable via Webhooks__RetryBackoffBase.
    public TimeSpan RetryBackoffBase { get; init; } = TimeSpan.FromSeconds(2);

    // SSRF override. When false (default), webhook deliveries to private/internal/
    // loopback/link-local targets are blocked at connect-time (the IP-pin guard) and such URLs are
    // rejected at subscription registration. Set true for a legitimately-private target (e.g. a
    // self-hosted consumer on a LAN/Tailscale address). Startup-bound (IOptions): the registration
    // validator and the connect callback read the same value, so toggling requires a restart.
    // Overridable via Webhooks__AllowPrivateNetworkTargets.
    public bool AllowPrivateNetworkTargets { get; init; }

    // Delivery-attempt log retention. The WebhookDeliveryLogSweepService deletes
    // WebhookDeliveryAttempt rows older than this many days on each sweep. 0 (or negative) keeps
    // the log forever (the sweep stays dormant). The catalog × subscription fan-out makes the log
    // grow faster than v1's single endpoint, so a default cap keeps it bounded. Overridable via
    // Webhooks__DeliveryLogRetentionDays.
    public int DeliveryLogRetentionDays { get; init; } = 30;

    // How often the delivery-log retention sweep runs; the first sweep is one interval after
    // startup. An hour, not a day: the timer restarts with the process, so an API that never stayed
    // up a full interval would never sweep, and a daily interval would starve under daily restarts.
    // 0 (or negative) turns the sweep off; past about 49.7 days it sweeps at that limit.
    // Overridable via Webhooks__DeliveryLogSweepInterval.
    public TimeSpan DeliveryLogSweepInterval { get; init; } = TimeSpan.FromHours(1);
}
