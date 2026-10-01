using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Events;

// The anti-drift core for webhook event emission. Per the project convention —
// every card mutation has two callers (REST + MCP) sharing a helper core; drift between
// them is the top bug class — the eight converted sites must NOT each hand-build a
// BoardEvent. All three create sites call PublishCardCreatedAsync; all five move sites
// call PublishCardMovedAsync. So REST and MCP emit identical events by construction
// (the same remedy as CardSummaryBuilder / SearchHelper).
//
// Everything the wire envelope needs is resolved here, at emit-time, before enqueuing
// (fidelity-required — the event must be self-contained): the fat CardSummary projection,
// the board slug, the lane name(s), a fresh ULID eventId, the occurredAt timestamp. The
// dispatcher then needs no DB.
internal static class WebhookEventFactory
{
    // The event-type identifiers now live in WebhookEventTypes (the catalog source of truth),
    // referenced below so a site can't emit "card.moved" while a test asserts "card_moved".
    // EventVersion is a wire-envelope version, not an event type, so it stays here.
    public const string EventVersion = "1";

    public static async Task PublishCardCreatedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        CardItem card,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var summary = await BuildSummaryAsync(db, card, ct);
        var (boardSlug, laneName) = await ResolveBoardSlugAndLaneNameAsync(db, card.BoardId, card.LaneId, ct);

        var boardEvent = BuildEvent
        (
            WebhookEventTypes.CardCreated,
            card.BoardId,
            boardSlug,
            actor,
            new WebhookCardCreatedData(summary, laneName)
        );

        broadcaster.Publish(boardEvent);
    }

    // fromLane / fromPosition are captured by the call-site BEFORE the move mutates the
    // card (MoveCardToLaneAsync renumbers both lanes, so the source position is gone once
    // it runs). By call time the card already
    // sits in the target lane at the target position. Used by the four single-card move
    // sites, which fan out to SSE + webhook together via broadcaster.Publish.
    public static async Task PublishCardMovedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        CardItem card,
        Lane fromLane,
        int fromPosition,
        Lane toLane,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildCardMovedAsync(db, card, fromLane, fromPosition, toLane, actor, ct);
        broadcaster.Publish(boardEvent);
    }

    // Builds the card.moved event WITHOUT publishing — for the bulk path
    // (bulk_update_cards), where the webhook projection needs one event PER moved card but
    // the SSE projection coalesces to one board-bell per board (BulkExecution's existing
    // contract). The bulk tool enqueues these directly to the sink after the batch saves,
    // and rings the single SSE bell separately — so calling broadcaster.Publish per card
    // (which would ring N SSE bells) would break the coalesce safety property.
    public static async Task<BoardEvent> BuildCardMovedAsync
    (
        BoardDbContext db,
        CardItem card,
        Lane fromLane,
        int fromPosition,
        Lane toLane,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var summary = await BuildSummaryAsync(db, card, ct);
        var boardSlug = await ResolveBoardSlugAsync(db, card.BoardId, ct);

        var data = new WebhookCardMovedData
        (
            summary,
            toLane.Name,
            new WebhookLaneRef(fromLane.Id, fromLane.Name, fromPosition),
            new WebhookLaneRef(toLane.Id, toLane.Name, card.Position)
        );

        return BuildEvent(WebhookEventTypes.CardMoved, card.BoardId, boardSlug, actor, data);
    }

    // ── Card-family emit helpers ──────────────────────────────────────────
    //
    // Build* methods produce a BoardEvent WITHOUT publishing — the co-fire sites
    // (PATCH /cards, update_card) collect several into one list and ring a single SSE
    // bell via broadcaster.PublishCoalesced; the bulk/prune sites enqueue them directly
    // to the sink. Publish* convenience wrappers cover the single-axis sites (archive,
    // restore, one label add/remove) where exactly one event fans out to one SSE bell.

    public static Task<BoardEvent> BuildCardUpdatedAsync
    (
        BoardDbContext db,
        CardItem card,
        BoardUser actor,
        CancellationToken ct
    ) =>
        BuildCardSummaryEventAsync(db, WebhookEventTypes.CardUpdated, card, actor, (summary, laneName) => new WebhookCardUpdatedData(summary, laneName), ct);

    public static Task<BoardEvent> BuildCardLabeledAsync
    (
        BoardDbContext db,
        CardItem card,
        Label label,
        BoardUser actor,
        CancellationToken ct
    ) =>
        BuildCardSummaryEventAsync(db, WebhookEventTypes.CardLabeled, card, actor, (summary, laneName) => new WebhookCardLabeledData(summary, laneName, new WebhookLabelRef(label.Id, label.Name, label.Color)), ct);

    public static Task<BoardEvent> BuildCardUnlabeledAsync
    (
        BoardDbContext db,
        CardItem card,
        Label label,
        BoardUser actor,
        CancellationToken ct
    ) =>
        BuildCardSummaryEventAsync(db, WebhookEventTypes.CardUnlabeled, card, actor, (summary, laneName) => new WebhookCardUnlabeledData(summary, laneName, new WebhookLabelRef(label.Id, label.Name, label.Color)), ct);

    public static async Task PublishCardArchivedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        CardItem card,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildCardSummaryEventAsync(db, WebhookEventTypes.CardArchived, card, actor, (summary, laneName) => new WebhookCardArchivedData(ForArchived(summary), laneName), ct);
        broadcaster.Publish(boardEvent);
    }

    public static async Task PublishCardRestoredAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        CardItem card,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildCardSummaryEventAsync(db, WebhookEventTypes.CardRestored, card, actor, (summary, laneName) => new WebhookCardRestoredData(summary, laneName), ct);
        broadcaster.Publish(boardEvent);
    }

    // card.deleted is BUILT here but NOT published — the caller must build it while the card row
    // still exists (the fat CardSummary enriches by querying the card id, so a build after the
    // Remove would return blank labels/counts), then Remove the card, save, and publish. Both
    // delete surfaces (REST DELETE /cards/{id} and prune delete) use the build-then-delete-then-
    // publish ordering.
    public static Task<BoardEvent> BuildCardDeletedAsync
    (
        BoardDbContext db,
        CardItem card,
        BoardUser actor,
        CancellationToken ct
    ) =>
        BuildCardSummaryEventAsync(db, WebhookEventTypes.CardDeleted, card, actor, (summary, laneName) => new WebhookCardDeletedData(ForDeleted(summary), laneName), ct);

    public static async Task PublishCardLabeledAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        CardItem card,
        Label label,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildCardLabeledAsync(db, card, label, actor, ct);
        broadcaster.Publish(boardEvent);
    }

    public static async Task PublishCardUnlabeledAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        CardItem card,
        Label label,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildCardUnlabeledAsync(db, card, label, actor, ct);
        broadcaster.Publish(boardEvent);
    }

    // Batch builders for the bulk (bulk_archive_cards / bulk_restore_cards) and prune-archive
    // paths: one CardSummaryBuilder pass + one slug query + one lane-name query for the whole
    // set, so an N-card archive does not fan out into N×(summary queries) on a concurrent
    // board. Cards may span boards/lanes (bulk_archive accepts a cross-board set), so slug and
    // lane name are resolved by id. The caller enqueues the events to the sink and rings one
    // bell per affected board (the BulkExecution coalesce contract).
    public static Task<List<BoardEvent>> BuildCardArchivedBatchAsync
    (
        BoardDbContext db,
        IReadOnlyList<CardItem> cards,
        BoardUser actor,
        CancellationToken ct
    ) =>
        BuildCardSummaryEventBatchAsync(db, WebhookEventTypes.CardArchived, cards, actor, (summary, laneName) => new WebhookCardArchivedData(ForArchived(summary), laneName), ct);

    public static Task<List<BoardEvent>> BuildCardRestoredBatchAsync
    (
        BoardDbContext db,
        IReadOnlyList<CardItem> cards,
        BoardUser actor,
        CancellationToken ct
    ) =>
        BuildCardSummaryEventBatchAsync(db, WebhookEventTypes.CardRestored, cards, actor, (summary, laneName) => new WebhookCardRestoredData(summary, laneName), ct);

    // The batch build for the prune delete path — built BEFORE RemoveRange (same reason as the
    // single delete). The caller enqueues each event to the sink and rings one bell per board (the
    // prune coalesce contract), mirroring the prune-archive path.
    public static Task<List<BoardEvent>> BuildCardDeletedBatchAsync
    (
        BoardDbContext db,
        IReadOnlyList<CardItem> cards,
        BoardUser actor,
        CancellationToken ct
    ) =>
        BuildCardSummaryEventBatchAsync(db, WebhookEventTypes.CardDeleted, cards, actor, (summary, laneName) => new WebhookCardDeletedData(ForDeleted(summary), laneName), ct);

    // The multi-axis co-fire assembly — the shared REST/MCP seam so PATCH /cards and
    // update_card emit the IDENTICAL event set for the same change by construction (the
    // anti-drift discipline; this must NOT be re-implemented per surface). One event per
    // CHANGED axis: content → card.updated, lane → card.moved, each added/removed label →
    // card.labeled / card.unlabeled. The caller rings exactly one SSE bell via
    // PublishCoalesced. Build* (no publish) so the events ride one coalesced bell, not N.
    public static async Task<List<BoardEvent>> BuildCardUpdateEventsAsync
    (
        BoardDbContext db,
        CardItem card,
        BoardUser actor,
        bool contentChanged,
        Lane? moveToLane,
        Lane? moveFromLane,
        int moveFromPosition,
        IReadOnlyList<Guid> addedLabelIds,
        IReadOnlyList<Guid> removedLabelIds,
        CancellationToken ct
    )
    {
        List<BoardEvent> events = [];

        if (contentChanged)
        {
            events.Add(await BuildCardUpdatedAsync(db, card, actor, ct));
        }

        if (moveToLane is not null)
        {
            events.Add(await BuildCardMovedAsync(db, card, moveFromLane!, moveFromPosition, moveToLane, actor, ct));
        }

        if (addedLabelIds.Count == 0 && removedLabelIds.Count == 0)
        {
            return events;
        }

        var changedLabelIds = addedLabelIds.Concat(removedLabelIds).ToList();
        var labelsById = await db.Labels
            .Where(l => changedLabelIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, ct);

        foreach (var labelId in addedLabelIds)
        {
            if (labelsById.TryGetValue(labelId, out var label))
            {
                events.Add(await BuildCardLabeledAsync(db, card, label, actor, ct));
            }
        }

        foreach (var labelId in removedLabelIds)
        {
            if (labelsById.TryGetValue(labelId, out var label))
            {
                events.Add(await BuildCardUnlabeledAsync(db, card, label, actor, ct));
            }
        }

        return events;
    }

    // ── Comment-family emit helpers ───────────────────────────────────────
    //
    // A comment mutation rings the same single board bell it always has (one "board-updated")
    // AND fans out one webhook event — routing REST and MCP through these helpers keeps the two
    // surfaces identical by construction. comment.deleted is published AFTER the row is removed,
    // from the captured comment object (its fields stay in memory); the card it belonged to still
    // exists, so the card ref still resolves.

    public static async Task PublishCommentCreatedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        CardComment comment,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildCommentEventAsync(db, WebhookEventTypes.CommentCreated, comment, actor, (c, card) => new WebhookCommentCreatedData(c, card), ct);
        broadcaster.Publish(boardEvent);
    }

    public static async Task PublishCommentUpdatedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        CardComment comment,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildCommentEventAsync(db, WebhookEventTypes.CommentUpdated, comment, actor, (c, card) => new WebhookCommentUpdatedData(c, card), ct);
        broadcaster.Publish(boardEvent);
    }

    public static async Task PublishCommentDeletedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        CardComment comment,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildCommentEventAsync(db, WebhookEventTypes.CommentDeleted, comment, actor, (c, card) => new WebhookCommentDeletedData(c, card), ct);
        broadcaster.Publish(boardEvent);
    }

    // ── Label-resource-family emit helpers ─────────────────────────────────
    //
    // The board-scoped label lifecycle (distinct from card.labeled / card.unlabeled). Same single
    // board bell the label CRUD sites always rang plus one webhook event. label.deleted is
    // published AFTER the row is removed, from the captured label object.

    public static async Task PublishLabelCreatedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        Label label,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildLabelEventAsync(db, WebhookEventTypes.LabelCreated, label, actor, l => new WebhookLabelCreatedData(l), ct);
        broadcaster.Publish(boardEvent);
    }

    public static async Task PublishLabelUpdatedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        Label label,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildLabelEventAsync(db, WebhookEventTypes.LabelUpdated, label, actor, l => new WebhookLabelUpdatedData(l), ct);
        broadcaster.Publish(boardEvent);
    }

    public static async Task PublishLabelDeletedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        Label label,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildLabelEventAsync(db, WebhookEventTypes.LabelDeleted, label, actor, l => new WebhookLabelDeletedData(l), ct);
        broadcaster.Publish(boardEvent);
    }

    // ── Attachment-family emit helpers ─────────────────────────────────────
    //
    // Metadata only — the file bytes never enter the event. Same single board bell the attachment
    // sites always rang plus one webhook event. attachment.deleted is published AFTER the row is
    // removed, from the captured attachment object (its Payload length stays readable in memory).

    public static async Task PublishAttachmentCreatedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        CardAttachment attachment,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildAttachmentEventAsync(db, WebhookEventTypes.AttachmentCreated, attachment, actor, (a, card) => new WebhookAttachmentCreatedData(a, card), ct);
        broadcaster.Publish(boardEvent);
    }

    public static async Task PublishAttachmentDeletedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        CardAttachment attachment,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildAttachmentEventAsync(db, WebhookEventTypes.AttachmentDeleted, attachment, actor, (a, card) => new WebhookAttachmentDeletedData(a, card), ct);
        broadcaster.Publish(boardEvent);
    }

    // ── Lane-family emit helpers ──────────────────────────────────────────
    //
    // lane.created / lane.deleted are single-axis lifecycle events: the same single board bell the
    // lane CRUD sites always rang, plus one webhook event (broadcaster.Publish). lane.reordered
    // carries the board's FULL new left-to-right order and fires from BOTH the bulk reorder (alone,
    // via PublishLaneReorderedAsync) and a single-lane update_lane position move (in the co-fire set,
    // via PublishCoalesced). update_lane can change name AND position in one call: it co-fires
    // lane.renamed + lane.reordered through PublishCoalesced (one bell, one webhook event per changed
    // axis), mirroring the card co-fire discipline. lane.deleted is published from the captured lane
    // after the row is gone. The SSE wire stays byte-for-byte unchanged — exactly one bell per call.

    public static async Task PublishLaneCreatedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        Lane lane,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildLaneLifecycleEventAsync(db, WebhookEventTypes.LaneCreated, lane, actor, l => new WebhookLaneCreatedData(l), ct);
        broadcaster.Publish(boardEvent);
    }

    public static async Task PublishLaneDeletedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        Lane lane,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildLaneLifecycleEventAsync(db, WebhookEventTypes.LaneDeleted, lane, actor, l => new WebhookLaneDeletedData(l), ct);
        broadcaster.Publish(boardEvent);
    }

    public static async Task PublishLaneReorderedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        Guid boardId,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildLaneReorderedAsync(db, boardId, actor, ct);
        broadcaster.Publish(boardEvent);
    }

    public static Task<BoardEvent> BuildLaneRenamedAsync
    (
        BoardDbContext db,
        Lane lane,
        BoardUser actor,
        CancellationToken ct
    ) =>
        BuildLaneLifecycleEventAsync(db, WebhookEventTypes.LaneRenamed, lane, actor, l => new WebhookLaneRenamedData(l), ct);

    // The board's full new left-to-right order, re-queried post-save so BOTH triggers (bulk reorder
    // and single-lane move) emit the IDENTICAL shape by construction. Off the request hot path and a
    // handful of rows, so the extra query is free.
    public static async Task<BoardEvent> BuildLaneReorderedAsync
    (
        BoardDbContext db,
        Guid boardId,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardSlug = await ResolveBoardSlugAsync(db, boardId, ct);
        var lanes = await db.Lanes
            .Where(l => l.BoardId == boardId && !l.IsArchiveLane)
            .OrderBy(l => l.Position)
                .Select(l => new WebhookLaneOrderEntry(l.Id, l.Name, l.Position))
                    .ToListAsync(ct);

        return BuildEvent(WebhookEventTypes.LaneReordered, boardId, boardSlug, actor, new WebhookLaneReorderedData(lanes));
    }

    // The update_lane co-fire assembly — the shared REST/MCP seam so PATCH /lanes and update_lane emit
    // the IDENTICAL event set for the same change by construction. One event per CHANGED axis: a name
    // change → lane.renamed; a position change → lane.reordered (full new order). The caller rings
    // exactly one SSE bell via PublishCoalesced (even when the list is empty — an all-no-op PATCH).
    public static async Task<List<BoardEvent>> BuildLaneUpdateEventsAsync
    (
        BoardDbContext db,
        Lane lane,
        BoardUser actor,
        bool nameChanged,
        bool positionChanged,
        CancellationToken ct
    )
    {
        List<BoardEvent> events = [];

        if (nameChanged)
        {
            events.Add(await BuildLaneRenamedAsync(db, lane, actor, ct));
        }

        if (positionChanged)
        {
            events.Add(await BuildLaneReorderedAsync(db, lane.BoardId, actor, ct));
        }

        return events;
    }

    // ── Size-family emit helpers ──────────────────────────────────────────
    //
    // A structural twin of the lane family. size.created / size.deleted are single-axis lifecycle
    // events: the same single board bell the size CRUD sites always rang, plus one webhook event
    // (broadcaster.Publish). size.reordered carries the board's FULL new order and fires from BOTH the
    // bulk reorder (alone, via PublishSizeReorderedAsync) and a single-size update_size ordinal move
    // (in the co-fire set, via PublishCoalesced). update_size can change name AND ordinal in one call:
    // it co-fires size.renamed + size.reordered through PublishCoalesced (one bell, one webhook event
    // per changed axis), mirroring the lane co-fire discipline. size.deleted is published from the
    // captured size after the row is gone. The SSE wire stays byte-for-byte unchanged — exactly one
    // bell per call. Unlike lanes there is no archive sentinel: every size is reorderable, so the
    // reorder query has no exclusion.

    public static async Task PublishSizeCreatedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        CardSize size,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildSizeLifecycleEventAsync(db, WebhookEventTypes.SizeCreated, size, actor, s => new WebhookSizeCreatedData(s), ct);
        broadcaster.Publish(boardEvent);
    }

    public static async Task PublishSizeDeletedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        CardSize size,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildSizeLifecycleEventAsync(db, WebhookEventTypes.SizeDeleted, size, actor, s => new WebhookSizeDeletedData(s), ct);
        broadcaster.Publish(boardEvent);
    }

    public static async Task PublishSizeReorderedAsync
    (
        BoardDbContext db,
        BoardEventBroadcaster broadcaster,
        Guid boardId,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardEvent = await BuildSizeReorderedAsync(db, boardId, actor, ct);
        broadcaster.Publish(boardEvent);
    }

    public static Task<BoardEvent> BuildSizeRenamedAsync
    (
        BoardDbContext db,
        CardSize size,
        BoardUser actor,
        CancellationToken ct
    ) =>
        BuildSizeLifecycleEventAsync(db, WebhookEventTypes.SizeRenamed, size, actor, s => new WebhookSizeRenamedData(s), ct);

    // The board's full new order, re-queried post-save so BOTH triggers (bulk reorder and single-size
    // move) emit the IDENTICAL shape by construction. Off the request hot path and a handful of rows,
    // so the extra query is free.
    public static async Task<BoardEvent> BuildSizeReorderedAsync
    (
        BoardDbContext db,
        Guid boardId,
        BoardUser actor,
        CancellationToken ct
    )
    {
        var boardSlug = await ResolveBoardSlugAsync(db, boardId, ct);
        var sizes = await db.CardSizes
            .Where(s => s.BoardId == boardId)
            .OrderBy(s => s.Ordinal)
                .Select(s => new WebhookSizeOrderEntry(s.Id, s.Name, s.Ordinal))
                    .ToListAsync(ct);

        return BuildEvent(WebhookEventTypes.SizeReordered, boardId, boardSlug, actor, new WebhookSizeReorderedData(sizes));
    }

    // The update_size co-fire assembly — the shared REST/MCP seam so PATCH /sizes and update_size emit
    // the IDENTICAL event set for the same change by construction. One event per CHANGED axis: a name
    // change → size.renamed; an ordinal change → size.reordered (full new order). The caller rings
    // exactly one SSE bell via PublishCoalesced (even when the list is empty — an all-no-op PATCH).
    public static async Task<List<BoardEvent>> BuildSizeUpdateEventsAsync
    (
        BoardDbContext db,
        CardSize size,
        BoardUser actor,
        bool nameChanged,
        bool ordinalChanged,
        CancellationToken ct
    )
    {
        List<BoardEvent> events = [];

        if (nameChanged)
        {
            events.Add(await BuildSizeRenamedAsync(db, size, actor, ct));
        }

        if (ordinalChanged)
        {
            events.Add(await BuildSizeReorderedAsync(db, size.BoardId, actor, ct));
        }

        return events;
    }

    // ── Board-family emit helpers — WEBHOOK-ONLY ───────────────────────────
    //
    // STRUCTURALLY NOVEL: board CRUD has no SSE broadcast today. These emit STRAIGHT to the sink and
    // NEVER call broadcaster.Publish — ringing a board bell here would change the SSE wire (a new
    // signal the browser consumer never saw), breaking the byte-for-byte-unchanged invariant. The
    // board entity carries everything the event needs (id, slug, name) and the slug is immutable, so
    // no DB query is required. board.deleted is enqueued from the captured board after the row is gone.

    public static void PublishBoardCreated(IWebhookSink sink, Board board, BoardUser actor) =>
        sink.Enqueue(BuildBoardEvent(WebhookEventTypes.BoardCreated, board, actor, b => new WebhookBoardCreatedData(b)));

    public static void PublishBoardRenamed(IWebhookSink sink, Board board, BoardUser actor) =>
        sink.Enqueue(BuildBoardEvent(WebhookEventTypes.BoardRenamed, board, actor, b => new WebhookBoardRenamedData(b)));

    public static void PublishBoardDeleted(IWebhookSink sink, Board board, BoardUser actor) =>
        sink.Enqueue(BuildBoardEvent(WebhookEventTypes.BoardDeleted, board, actor, b => new WebhookBoardDeletedData(b)));

    // The lane-lifecycle core: resolves the board slug and projects the single lane resource.
    private static async Task<BoardEvent> BuildLaneLifecycleEventAsync
    (
        BoardDbContext db,
        string eventType,
        Lane lane,
        BoardUser actor,
        Func<WebhookLaneData, object> dataFactory,
        CancellationToken ct
    )
    {
        var boardSlug = await ResolveBoardSlugAsync(db, lane.BoardId, ct);
        var laneData = new WebhookLaneData(lane.Id, lane.BoardId, lane.Name, lane.Position);

        return BuildEvent(eventType, lane.BoardId, boardSlug, actor, dataFactory(laneData));
    }

    // The size-lifecycle core: resolves the board slug and projects the single card-size resource.
    private static async Task<BoardEvent> BuildSizeLifecycleEventAsync
    (
        BoardDbContext db,
        string eventType,
        CardSize size,
        BoardUser actor,
        Func<WebhookSizeData, object> dataFactory,
        CancellationToken ct
    )
    {
        var boardSlug = await ResolveBoardSlugAsync(db, size.BoardId, ct);
        var sizeData = new WebhookSizeData(size.Id, size.BoardId, size.Name, size.Ordinal);

        return BuildEvent(eventType, size.BoardId, boardSlug, actor, dataFactory(sizeData));
    }

    // The board-event core: the envelope's board IS the subject, so no DB query — the entity carries
    // id, slug and name directly.
    private static BoardEvent BuildBoardEvent
    (
        string eventType,
        Board board,
        BoardUser actor,
        Func<WebhookBoardData, object> dataFactory
    )
    {
        var boardData = new WebhookBoardData(board.Id, board.Slug, board.Name);

        return BuildEvent(eventType, board.Id, board.Slug, actor, dataFactory(boardData));
    }

    // The fat-CardSummary single-event core. Resolves the summary + board slug +
    // current lane name, then stamps the envelope. dataFactory shapes the per-event-type
    // `data` block from the resolved summary and lane name.
    private static async Task<BoardEvent> BuildCardSummaryEventAsync
    (
        BoardDbContext db,
        string eventType,
        CardItem card,
        BoardUser actor,
        Func<CardSummary, string, object> dataFactory,
        CancellationToken ct
    )
    {
        var summary = await BuildSummaryAsync(db, card, ct);
        var (boardSlug, laneName) = await ResolveBoardSlugAndLaneNameAsync(db, card.BoardId, card.LaneId, ct);

        return BuildEvent(eventType, card.BoardId, boardSlug, actor, dataFactory(summary, laneName));
    }

    private static async Task<List<BoardEvent>> BuildCardSummaryEventBatchAsync
    (
        BoardDbContext db,
        string eventType,
        IReadOnlyList<CardItem> cards,
        BoardUser actor,
        Func<CardSummary, string, object> dataFactory,
        CancellationToken ct
    )
    {
        if (cards.Count == 0)
        {
            return [];
        }

        var summariesById = (await CardSummaryBuilder.BuildAsync(db, [.. cards], ct))
            .ToDictionary(summary => summary.Id);

        var boardIds = cards.Select(c => c.BoardId).Distinct().ToList();
        var slugByBoard = await db.Boards
            .Where(b => boardIds.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, b => b.Slug, ct);

        var laneIds = cards.Select(c => c.LaneId).Distinct().ToList();
        var laneNameById = await db.Lanes
            .Where(l => laneIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, l => l.Name, ct);

        List<BoardEvent> events = [];
        foreach (var card in cards)
        {
            var summary = summariesById[card.Id];
            var boardSlug = slugByBoard.GetValueOrDefault(card.BoardId, string.Empty);
            var laneName = laneNameById.GetValueOrDefault(card.LaneId, string.Empty);

            events.Add(BuildEvent(eventType, card.BoardId, boardSlug, actor, dataFactory(summary, laneName)));
        }

        return events;
    }

    // The comment-event core: resolves the card's board + number and the comment author's name,
    // then stamps the envelope. dataFactory shapes the per-event-type `data` block.
    private static async Task<BoardEvent> BuildCommentEventAsync
    (
        BoardDbContext db,
        string eventType,
        CardComment comment,
        BoardUser actor,
        Func<WebhookCommentData, WebhookCardRef, object> dataFactory,
        CancellationToken ct
    )
    {
        var card = await db.Cards
            .Where(c => c.Id == comment.CardId)
                .Select(c => new { c.BoardId, c.Number })
                    .FirstOrDefaultAsync(ct);

        var boardId = card?.BoardId ?? Guid.Empty;
        var cardNumber = card?.Number ?? 0;
        var boardSlug = await ResolveBoardSlugAsync(db, boardId, ct);
        var authorName = await ResolveUserNameAsync(db, comment.UserId, ct);

        var commentData = new WebhookCommentData
        (
            comment.Id,
            comment.CardId,
            cardNumber,
            comment.ContentMarkdown,
            comment.UserId,
            authorName,
            comment.LastUpdatedAtUtc
        );

        return BuildEvent(eventType, boardId, boardSlug, actor, dataFactory(commentData, new WebhookCardRef(comment.CardId, cardNumber)));
    }

    // The label-event core: resolves the board slug and projects the label resource.
    private static async Task<BoardEvent> BuildLabelEventAsync
    (
        BoardDbContext db,
        string eventType,
        Label label,
        BoardUser actor,
        Func<WebhookLabelData, object> dataFactory,
        CancellationToken ct
    )
    {
        var boardSlug = await ResolveBoardSlugAsync(db, label.BoardId, ct);
        var labelData = new WebhookLabelData(label.Id, label.BoardId, label.Name, label.Color);

        return BuildEvent(eventType, label.BoardId, boardSlug, actor, dataFactory(labelData));
    }

    // The attachment-event core: resolves the card's board + number and projects the attachment
    // metadata (SizeBytes is the stored payload length — the bytes never ride the wire).
    private static async Task<BoardEvent> BuildAttachmentEventAsync
    (
        BoardDbContext db,
        string eventType,
        CardAttachment attachment,
        BoardUser actor,
        Func<WebhookAttachmentData, WebhookCardRef, object> dataFactory,
        CancellationToken ct
    )
    {
        var card = await db.Cards
            .Where(c => c.Id == attachment.CardId)
                .Select(c => new { c.BoardId, c.Number })
                    .FirstOrDefaultAsync(ct);

        var boardId = card?.BoardId ?? Guid.Empty;
        var cardNumber = card?.Number ?? 0;
        var boardSlug = await ResolveBoardSlugAsync(db, boardId, ct);

        var attachmentData = new WebhookAttachmentData
        (
            attachment.Id,
            attachment.CardId,
            attachment.FileName,
            attachment.ContentType,
            attachment.Payload.Length,
            attachment.AddedByUserId,
            attachment.AddedAtUtc
        );

        return BuildEvent(eventType, boardId, boardSlug, actor, dataFactory(attachmentData, new WebhookCardRef(attachment.CardId, cardNumber)));
    }

    private static BoardEvent BuildEvent
    (
        string eventType,
        Guid boardId,
        string boardSlug,
        BoardUser actor,
        object data
    ) =>
        new
        (
            eventType,
            Ulid.NewUlid().ToString(),
            DateTimeOffset.UtcNow,
            EventVersion,
            boardId,
            boardSlug,
            new BoardEventActor(actor.Id, actor.Name, actor.Role.ToString()),
            data
        );

    private static async Task<CardSummary> BuildSummaryAsync(BoardDbContext db, CardItem card, CancellationToken ct)
    {
        var summaries = await CardSummaryBuilder.BuildAsync(db, [card], ct);
        return summaries[0];
    }

    // An archived card's lane is the board's hidden internal archive lane; its GUID is an internal
    // implementation detail, so card.archived blanks the embedded summary's lane id (it drops off the
    // wire — see CardSummary.LaneId). laneName + isArchived carry the state a consumer needs.
    private static CardSummary ForArchived(CardSummary summary) => summary with { LaneId = default };

    // Delete is allowed on an archived card, so a deleted card MAY be sitting in the hidden archive
    // lane — blank its internal lane GUID exactly as card.archived does, but only in that case. A
    // normal (non-archived) deleted card keeps its real laneId, so the blanking is conditional on
    // IsArchived (unlike card.archived, which blanks unconditionally).
    private static CardSummary ForDeleted(CardSummary summary) =>
        summary.IsArchived ? ForArchived(summary) : summary;

    private static async Task<string> ResolveBoardSlugAsync(BoardDbContext db, Guid boardId, CancellationToken ct) =>
        await db.Boards
            .Where(b => b.Id == boardId)
                .Select(b => b.Slug)
                    .FirstOrDefaultAsync(ct)
        ?? string.Empty;

    private static async Task<string> ResolveUserNameAsync(BoardDbContext db, Guid userId, CancellationToken ct) =>
        await db.Users
            .Where(u => u.Id == userId)
                .Select(u => u.Name)
                    .FirstOrDefaultAsync(ct)
        ?? string.Empty;

    private static async Task<(string BoardSlug, string LaneName)> ResolveBoardSlugAndLaneNameAsync
    (
        BoardDbContext db,
        Guid boardId,
        Guid laneId,
        CancellationToken ct
    )
    {
        var boardSlug = await ResolveBoardSlugAsync(db, boardId, ct);
        var laneName = await db.Lanes
            .Where(l => l.Id == laneId)
                .Select(l => l.Name)
                    .FirstOrDefaultAsync(ct)
            ?? string.Empty;

        return (boardSlug, laneName);
    }
}
