namespace Collabot.Collattice.Api.Models;

// A record of one webhook delivery attempt. One flat, append-only table — NOT a
// subscription resource (there is no subscription in v1). The observability floor we
// won't cut: a fire-and-forget webhook that silently stops triggering is
// the zero-operator nightmare, and you cannot reconstruct delivery history you did not
// record. Logs scroll away and are not queryable by board/event after the fact; the row is
// the difference between "webhooks work" and "webhooks are debuggable". The
// subscription-registry upgrade added the SubscriptionId FK below.
public class WebhookDeliveryAttempt
{
    public Guid Id { get; set; }

    // Which subscription this delivery targeted. Nullable: v1/seed rows and rows whose
    // subscription was later deleted carry null (FK OnDelete = SetNull — the audit log outlives the
    // subscription). Not backfilled.
    public Guid? SubscriptionId { get; set; }

    public string EventId { get; set; } = string.Empty;     // the envelope eventId (dedup correlation; ULID is 26 chars)

    public string EventType { get; set; } = string.Empty;   // "card.created", "card.moved"

    public Guid BoardId { get; set; }

    public int Attempt { get; set; }                        // 1-based attempt number

    public WebhookDeliveryStatus Status { get; set; }       // Succeeded | Failed

    public int? HttpStatusCode { get; set; }                // null when no response (timeout/connection error)

    public string? Error { get; set; }                      // head-preserving truncated failure detail when Failed

    public DateTimeOffset AttemptedAtUtc { get; set; }
}

public enum WebhookDeliveryStatus
{
    Succeeded,
    Failed,
}
