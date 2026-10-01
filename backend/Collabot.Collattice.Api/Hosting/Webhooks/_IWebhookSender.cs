namespace Collabot.Collattice.Api.Hosting.Webhooks;

// The HTTP send seam for webhook delivery. Mirrors the UpdateCheck ILatestVersionSource
// shape — a typed HttpClient behind an interface — so the outbound POST is stubbable in tests
// (a stub HttpMessageHandler can capture exact bytes/headers without a real socket) and the
// dispatcher's drain/retry/persist logic stays separable from the wire send.
//
// One attempt = one POST to a per-subscription target (v1 read the URL/secret from
// Webhooks:Endpoint/:Secret; v2 carries them in WebhookTarget). The sender serializes the event
// ONCE (sign the exact bytes sent), signs with the target's secret when present, attaches the
// delivery headers, and reports the outcome. The dispatcher owns the retry loop, the persisted
// attempt log, and the loud final-failure drop.
public interface IWebhookSender
{
    Task<WebhookDeliveryResult> SendAsync(BoardEvent boardEvent, WebhookTarget target, CancellationToken ct);
}

// The outcome of one delivery attempt. StatusCode is null when no response was received
// (timeout, DNS/TLS/connection error); Error carries the head-of-message actionable detail.
public sealed record WebhookDeliveryResult(bool Succeeded, int? StatusCode, string? Error);
