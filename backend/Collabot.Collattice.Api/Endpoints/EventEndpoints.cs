namespace Collabot.Collattice.Api.Endpoints;

internal static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/boards/{boardId:guid}/events", async (BoardEventBroadcaster broadcaster, HttpContext http, Guid boardId, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Connection = "keep-alive";

            var reader = broadcaster.Subscribe(boardId);

            try
            {
                // Emit a priming comment immediately on connect so the browser EventSource
                // fires onopen promptly without waiting for the first real event.
                // Also unblocks networkidle-style waits in browser automation.
                await http.Response.WriteAsync(": ok\n\n", ct);
                await http.Response.Body.FlushAsync(ct);

                await foreach (var eventType in reader.ReadAllAsync(ct))
                {
                    await http.Response.WriteAsync($"event: {eventType}\ndata: {{}}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected
            }
            finally
            {
                broadcaster.Unsubscribe(boardId, reader);
            }
        })
        .RequireCors(CorsPolicies.Default);

        return app;
    }
}
