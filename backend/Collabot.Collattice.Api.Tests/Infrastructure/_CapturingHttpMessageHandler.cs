using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// A stub HttpMessageHandler that captures every outbound webhook POST (method, URI, headers,
// the EXACT raw body bytes) and returns a programmable response. The webhook delivery
// tests point the dispatcher's typed HttpClient at this so they assert on the precise bytes and
// headers sent — no real socket, no flake. The raw body is captured ONCE per request so the
// HMAC-signature round-trip can run both the signature-check and the body-assert against the
// SAME captured array (the footgun hides in re-serializing the parsed body).
public class CapturingHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();

    // Signals each captured request so a test can await the next delivery instead of polling a wall
    // clock. Unbounded so a delivery that lands before the test starts waiting is retained, not
    // dropped — the read then returns it immediately with no lost-wakeup race.
    private readonly Channel<CapturedRequest> _signal = Channel.CreateUnbounded<CapturedRequest>();

    // The status code each request returns. Default 200 (success). Flip to 500 to exercise retry,
    // or use a hanging delay to exercise timeout. Read on each Send so a test can change it mid-run
    // (e.g. scenario 8: 500 until exhausted, then 200 on a fresh create).
    public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;

    // Optional per-request-URI status selector (fan-out isolation tests — one subscription's
    // URL returns 500 while another returns 200). When set it overrides ResponseStatusCode.
    public Func<Uri?, HttpStatusCode>? ResponseSelector { get; set; }

    // Optional artificial delay before responding — simulates a slow endpoint for the
    // never-blocks-the-mutation and timeout edge cases.
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

    public IReadOnlyList<CapturedRequest> Requests => [.. _requests];

    public int RequestCount => _requests.Count;

    public void Clear()
    {
        _requests.Clear();
        while (_signal.Reader.TryRead(out _))
        {
        }
    }

    // Completes when the next request is captured (or with OperationCanceledException if the token
    // fires first). The caller drives the delivery — the running dispatcher POSTs asynchronously —
    // so this replaces a wall-clock poll: the wait ends the instant the delivery lands, and a
    // caller-supplied timeout token is a backstop against a genuine non-delivery, not a race deadline.
    public async Task<CapturedRequest> WaitForNextRequestAsync(CancellationToken cancellationToken) =>
        await _signal.Reader.ReadAsync(cancellationToken);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        // Snapshot the headers (content + request) into a flat lookup the test can read.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in request.Headers)
        {
            headers[key] = string.Join(',', values);
        }

        if (request.Content is not null)
        {
            foreach (var (key, values) in request.Content.Headers)
            {
                headers[key] = string.Join(',', values);
            }
        }

        var captured = new CapturedRequest(request.Method.Method, request.RequestUri, headers, body);
        _requests.Enqueue(captured);
        _signal.Writer.TryWrite(captured);

        if (ResponseDelay > TimeSpan.Zero)
        {
            await Task.Delay(ResponseDelay, cancellationToken);
        }

        var status = ResponseSelector?.Invoke(request.RequestUri) ?? ResponseStatusCode;
        return new HttpResponseMessage(status);
    }
}

public sealed record CapturedRequest(string Method, Uri? Uri, IReadOnlyDictionary<string, string> Headers, byte[] Body);
