using System.Collections.Concurrent;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// A test IWebhookSink that captures every enqueued BoardEvent in arrival order, so a
// test can assert on the typed event the seam produced — no HTTP delivery, no
// dispatcher. Swapped in for the production WebhookQueue via WebhookTestFactory. The
// broadcaster's Publish path (single-card sites) and BulkCardTools' direct enqueue (the
// bulk site) both resolve IWebhookSink, so both capture into this one instance.
public class CapturingWebhookSink : IWebhookSink
{
    private readonly ConcurrentQueue<BoardEvent> _events = new();

    public void Enqueue(BoardEvent boardEvent) => _events.Enqueue(boardEvent);

    public IReadOnlyList<BoardEvent> Captured => [.. _events];

    public void Clear() => _events.Clear();
}
