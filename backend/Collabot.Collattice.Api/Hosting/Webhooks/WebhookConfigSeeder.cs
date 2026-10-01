using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Hosting.Webhooks;

// Migrates the v1 single configured endpoint (Webhooks:Endpoint / :Secret) into a subscription row
// on the first v2 boot. Extracted from Program.cs as a static seam so the gate-catch and
// idempotency are testable directly (the BoardSeeder pattern).
//
// The gate is the load-bearing detail: Webhooks:Endpoint set AND an EMPTY subscription table — NOT
// the !Users.AnyAsync() fresh-install gate, which never fires on an upgrade (prod already has
// users) and would silently drop the working prod webhook (the exact failure mode this gate exists
// to prevent).
//
// The row is written DIRECTLY (no registration SSRF validation): a private prod endpoint must not
// be dropped at seed. That is NOT a guard exemption (no grandfathering): the seeded row's
// DELIVERIES still pass the uniform connect-time SSRF guard, blocked until the operator sets
// Webhooks:AllowPrivateNetworkTargets. The selection is the v1 parity pair [card.created,
// card.moved], NOT "*" — seeding the wildcard would fire new event types at the prod consumer on
// upgrade (a silent behavior change; the corollary of "must not silently drop").
internal static class WebhookConfigSeeder
{
    public static async Task<WebhookSubscription?> SeedAsync
    (
        BoardDbContext db,
        string? endpoint,
        string? secret,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(db);

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        // Idempotent: any existing subscription means the migration already ran (or the operator
        // built a registry) — do not re-seed.
        if (await db.WebhookSubscriptions.AnyAsync(ct))
        {
            return null;
        }

        var subscription = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Name = "Migrated from configuration",
            Url = endpoint.Trim(),
            Secret = string.IsNullOrWhiteSpace(secret) ? null : secret,
            Enabled = true,
            EventTypes = [WebhookEventTypes.CardCreated, WebhookEventTypes.CardMoved],
        };

        db.WebhookSubscriptions.Add(subscription);
        await db.SaveChangesAsync(ct);

        return subscription;
    }
}
