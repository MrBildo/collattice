using System.Collections.Concurrent;

namespace Collabot.Collattice.Api.Events;

// The in-memory webhook sink. Enqueue is called (after a successful SaveChanges) by the
// shared emit helper via BoardEventBroadcaster.Publish; the dispatcher drains via
// TryDequeue. In-memory means an API restart drops un-drained events — the deliberate v1
// reliability bar, and the documented swap-point for a durable outbox: the
// IWebhookSink seam is what makes that a swap, not a rewrite.
public class WebhookQueue : IWebhookSink
{
    private readonly ConcurrentQueue<BoardEvent> _queue = new();

    // An event about an unsaved draft is never delivered, so it never enters the queue (see
    // BoardEvent.AboutDraft).
    public void Enqueue(BoardEvent boardEvent)
    {
        ArgumentNullException.ThrowIfNull(boardEvent);

        if (boardEvent.AboutDraft)
        {
            return;
        }

        _queue.Enqueue(boardEvent);
    }

    // The dispatcher is the queue's only consumer, so the event it peeks is the one it dequeues.
    public bool TryPeek(out BoardEvent? boardEvent) => _queue.TryPeek(out boardEvent);

    public bool TryDequeue(out BoardEvent? boardEvent) => _queue.TryDequeue(out boardEvent);

    public int Count => _queue.Count;
}
