using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Collabot.Collattice.Api.Hosting.Webhooks;

// The typed-HttpClient realization of IWebhookSender. Serializes the enriched event
// ONCE with the project's REST JSON config (camelCase / Web defaults — a consumer that already
// reads Collattice's REST shapes sees identical field names), signs THOSE bytes when the target
// carries a secret (never re-serialize for the signature, or the signed bytes would differ from the
// sent bytes), and POSTs with the delivery headers. The per-POST timeout is the typed client's
// Timeout (configured in Program.cs from Webhooks:DeliveryTimeout) so a slow endpoint is a failed
// attempt, not a wait. The outbound connection passes the SSRF connect guard wired on the client's
// primary handler — a blocked target throws at connect and surfaces here as a Failed attempt.
internal class HttpWebhookSender(HttpClient httpClient) : IWebhookSender
{
    private readonly HttpClient _httpClient = httpClient
        ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<WebhookDeliveryResult> SendAsync(BoardEvent boardEvent, WebhookTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(boardEvent);
        ArgumentNullException.ThrowIfNull(target);

        // Serialize ONCE — these exact bytes are what we sign and what we send.
        var body = JsonSerializer.SerializeToUtf8Bytes(boardEvent, JsonSerializerOptions.Web);

        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, target.Url)
            {
                Content = content,
            };

            // X-Collattice-Event lets a consumer route without parsing the body. The delivery-id
            // header carries the envelope eventId, so every retry of one event carries the same
            // dedup value.
            request.Headers.TryAddWithoutValidation("X-Collattice-Event", boardEvent.EventType);
            request.Headers.TryAddWithoutValidation("X-Collattice-Delivery-Id", boardEvent.EventId);

            if (!string.IsNullOrWhiteSpace(target.Secret))
            {
                request.Headers.TryAddWithoutValidation
                (
                    "X-Collattice-Signature",
                    WebhookSigner.Sign(body, target.Secret)
                );
            }

            using var response = await _httpClient.SendAsync(request, ct);

            var statusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return new WebhookDeliveryResult(true, statusCode, null);
            }

            // A non-2xx is a failed attempt; the reason phrase is a usable head-of-message signal.
            var error = $"{statusCode.ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase}".Trim();
            return new WebhookDeliveryResult(false, statusCode, error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown cancellation — propagate so the dispatcher loop exits cleanly; the
            // dispatcher does not record an attempt for a shutdown-cancelled send.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // No response: timeout (TaskCanceledException from HttpClient.Timeout, ct not the
            // trigger), DNS/TLS/connection-refused (HttpRequestException), or an SSRF connect-block
            // (the guard throws → SocketsHttpHandler wraps it in HttpRequestException). Head-of-
            // message carries the actionable error class.
            return new WebhookDeliveryResult(false, null, ex.Message);
        }
    }
}
