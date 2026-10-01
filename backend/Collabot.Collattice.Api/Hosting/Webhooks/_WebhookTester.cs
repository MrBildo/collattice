using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Hosting.Webhooks;

// The shared test-delivery (ping) seam. Both the REST POST /webhooks/subscriptions/{id}/test
// endpoint and the test_webhook MCP tool delegate here, so the two surfaces ping identically and a
// test cannot become a validation-bypassing side-channel: the ping dials through the SAME
// IWebhookSender (the SSRF-guarded typed client + HMAC signing) as production delivery, so a private
// target with the flag off is connect-blocked here exactly as it is on a real event. Synchronous and
// one-shot — the operator clicked "send test" and wants the outcome now; no retry, one attempt row,
// returned inline. The secret is read from the entity to sign but is never returned.
internal sealed class WebhookTester
(
    BoardDbContext db,
    IWebhookSender sender
)
{
    private readonly BoardDbContext _db = db
        ?? throw new ArgumentNullException(nameof(db));
    private readonly IWebhookSender _sender = sender
        ?? throw new ArgumentNullException(nameof(sender));

    // Delivers a synchronous webhook.ping to one subscription through the guarded pipe, records a
    // single attempt row (tagged with the subscription id, like every other delivery), and returns
    // the outcome inline. Null when the subscription does not exist (the surface maps that to a
    // not-found response).
    public async Task<WebhookTestResult?> TestAsync(Guid id, BoardUser actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var subscription = await _db.WebhookSubscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (subscription is null)
        {
            return null;
        }

        var pingEvent = new BoardEvent
        (
            WebhookEventTypes.Ping,
            Ulid.NewUlid().ToString(),
            DateTimeOffset.UtcNow,
            WebhookEventFactory.EventVersion,
            Guid.Empty,                 // a ping is not board-scoped
            string.Empty,
            new BoardEventActor(actor.Id, actor.Name, actor.Role.ToString()),
            new WebhookPingData(subscription.Id, "Collattice webhook test delivery.")
        );

        var target = new WebhookTarget(subscription.Url, subscription.Secret);
        var result = await _sender.SendAsync(pingEvent, target, ct);

        _db.WebhookDeliveryAttempts.Add(new WebhookDeliveryAttempt
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscription.Id,
            EventId = pingEvent.EventId,
            EventType = pingEvent.EventType,
            BoardId = Guid.Empty,
            Attempt = 1,
            Status = result.Succeeded ? WebhookDeliveryStatus.Succeeded : WebhookDeliveryStatus.Failed,
            HttpStatusCode = result.StatusCode,
            Error = result.Succeeded ? null : TruncateHead(result.Error),
            AttemptedAtUtc = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(ct);

        return new WebhookTestResult(result.Succeeded, result.StatusCode, result.Error);
    }

    // Match the dispatcher's head-preserving 500-char cap on the persisted Error column; the inline
    // result keeps the full message for the operator.
    private static string? TruncateHead(string? error) =>
        error is { Length: > 500 } ? error[..500] : error;
}

// The inline outcome of a test delivery: success / statusCode / error — never the secret.
internal sealed record WebhookTestResult(bool Success, int? StatusCode, string? Error);
