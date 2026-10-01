using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Collabot.Collattice.Api.Endpoints;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Models;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Mcp;

[McpServerToolType]
public sealed class CardTools(BoardDbContext db, McpAuthService auth, BoardEventBroadcaster broadcaster)
{
    [McpServerTool(Name = "create_card", Destructive = false)]
    [Description("Create a new card on the kanban board.")]
    public async Task<string> CreateCardAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The title/name of the card")] string name,
        [Description("The ID (guid) of the lane to place the card in")] Guid laneId,
        [Description("Optional markdown description")] string? descriptionMarkdown = null,
        [Description("Optional size ID (guid). If omitted, uses the board's lowest-ordinal size.")] Guid? sizeId = null,
        [Description("Optional size name (e.g. 'M', 'XL'). Used if sizeId is not provided.")] string? sizeName = null,
        [Description("Optional label IDs (guids) to assign to the card at creation. Accepts comma-separated GUIDs ('guid1,guid2') or a JSON array string ('[\"guid1\",\"guid2\"]'). All labels must belong to the same board as the lane.")] string? labelIds = null,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        // MCP create_card takes a laneId and derives the board from it; the shared
        // helper is board-scoped, so resolve the lane's board up front. Lane-not-found
        // keeps the MCP wording here; archive-lane / size / name validation lives in
        // the shared CardCreateHelper both surfaces route through.
        var lane = await db.Lanes.FirstOrDefaultAsync(l => l.Id == laneId, ct);
        if (lane is null)
        {
            return "Error: Lane not found.";
        }

        // Parse and validate label IDs the MCP way — CSV or JSON-array input, MCP error
        // wording. The shared helper then stages the already-validated list.
        var (parsedLabelIds, labelError) = await McpLabelParsing.ParseAndValidateLabelIdsAsync(db, labelIds, lane.BoardId, ct);
        if (labelError is not null)
        {
            return labelError;
        }

        var request = new CreateCardRequest(laneId, name, descriptionMarkdown, Position: null, sizeId, sizeName, LabelIds: null);
        var (card, buildError) = await CardCreateHelper.BuildCardAsync(db, lane.BoardId, request, parsedLabelIds, user!, ct);
        if (buildError is not null)
        {
            return $"Error: {buildError}";
        }

        if (!await CardNumberHelper.TryInsertCardWithAutoNumberAsync(db, card!, lane.BoardId, ct))
        {
            return $"Error: {CardNumberHelper.ContendedMessage}";
        }

        // The webhook fan-out already rings the SSE bell (the typed event downsamples to the
        // same "board-updated" signal), so a separate board broadcast here would double-ring
        // SSE.
        await WebhookEventFactory.PublishCardCreatedAsync(db, broadcaster, card!, user!, ct);
        var summaries = await CardSummaryBuilder.BuildAsync(db, [card!], ct);
        return JsonSerializer.Serialize(summaries[0], JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "move_card", Destructive = false)]
    [Description("Move a card to a different lane and/or position (index) within that lane. If index is omitted, the card is placed at the top of the target lane (index 0).")]
    public async Task<string> MoveCardAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the target lane")] Guid laneId,
        [Description("The ID (guid) of the card to move (provide this or cardNumber)")] Guid? cardId = null,
        [Description("The card number (provide this or cardId). Requires boardId or boardSlug.")] long? cardNumber = null,
        [Description("Optional 0-based index position in the target lane. Defaults to top of lane (index 0).")] int? index = null,
        [Description("Board ID (required when using cardNumber)")] Guid? boardId = null,
        [Description("Board slug (alternative to boardId when using cardNumber)")] string? boardSlug = null,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var (resolvedCardId, resolveError) = await McpCardResolver.ResolveCardIdAsync(db, cardId, cardNumber, boardId, boardSlug, ct);
        if (resolveError is not null)
        {
            return resolveError;
        }

        var card = await db.Cards.FindAsync([resolvedCardId!.Value], ct);
        if (card is null)
        {
            return "Error: Card not found.";
        }

        // Block moving FROM an archive lane
        var sourceLane = await db.Lanes.FindAsync([card.LaneId], ct);
        if (sourceLane is not null && sourceLane.IsArchiveLane)
        {
            return "Use restore_card to restore archived cards.";
        }

        var targetLane = await db.Lanes.FirstOrDefaultAsync(l => l.Id == laneId, ct);
        if (targetLane is null)
        {
            return "Error: Lane not found.";
        }

        // Block moving TO an archive lane
        if (targetLane.IsArchiveLane)
        {
            return "Use archive_card to archive cards.";
        }

        // Snapshot source lane/position BEFORE MoveCardToLaneAsync mutates + renumbers
        // both lanes. sourceLane is resolved above (the archive-lane guard).
        var fromPosition = card.Position;

        var resolvedIndex = await CardReorderHelper.MoveCardToLaneAsync(db, card, laneId, index, ct);

        card.LastUpdatedByUserId = user!.Id;
        card.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await WebhookEventFactory.PublishCardMovedAsync(db, broadcaster, card, sourceLane!, fromPosition, targetLane, user, ct);
        return $"Card '{card.Name}' moved to lane at index {resolvedIndex.ToString(CultureInfo.InvariantCulture)}.";
    }

    [McpServerTool(Name = "update_card", Destructive = false)]
    [Description("Update a card's name, description, size, lane/position, or labels. All fields are optional — only provided fields are changed. For labelIds, pass either a comma-separated list of label GUIDs ('guid1,guid2') or a JSON array string ('[\"guid1\",\"guid2\"]') to replace all current labels (empty string or empty array clears all). Returns the enriched card summary (with labels, sizeName, commentCount, attachmentCount, isArchived) — no follow-up get_card needed. COLLISION AWARENESS on description edits: the update always succeeds (last write wins; it is never blocked or refused). To learn whether your description edit landed on top of someone else's, pass expectedDescriptionRevision = the descriptionHistoryCount you read from get_card. If the description moved past that revision meanwhile, the returned card carries a 'collision' field: { kind: 'exact', field: 'description', actor: { userId, name } } — actor is who you overwrote. Omit expectedDescriptionRevision and you still get a best-effort 'collision' with kind 'approximate' (field null) when another user edited this card within a few seconds before your write. No 'collision' field means no overlap was detected.")]
    public async Task<string> UpdateCardAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the card to update (provide this or cardNumber)")] Guid? cardId = null,
        [Description("The card number (provide this or cardId). Requires boardId or boardSlug.")] long? cardNumber = null,
        [Description("New name/title (optional)")] string? name = null,
        [Description("New markdown description (optional)")] string? descriptionMarkdown = null,
        [Description("New size ID (guid, optional)")] Guid? sizeId = null,
        [Description("New size name (e.g. 'M', 'XL', optional). Used if sizeId is not provided.")] string? sizeName = null,
        [Description("Target lane ID to move the card to (optional)")] Guid? laneId = null,
        [Description("0-based index position in the target lane (optional, requires laneId — defaults to top of lane)")] int? index = null,
        [Description("Label GUIDs to replace current labels (optional). Accepts comma-separated ('guid1,guid2') or a JSON array string ('[\"guid1\",\"guid2\"]'). Empty string or empty array clears all.")] string? labelIds = null,
        [Description("Collision-awareness baseline (optional): the descriptionHistoryCount you read from get_card before editing. Pass it alongside descriptionMarkdown to be told exactly whether your edit overwrote another user's — the returned card's 'collision' field names them. Awareness only; your save is never blocked. Does not itself count as a change (passing only this returns 'No changes specified.').")] int? expectedDescriptionRevision = null,
        [Description("Board ID (required when using cardNumber)")] Guid? boardId = null,
        [Description("Board slug (alternative to boardId when using cardNumber)")] string? boardSlug = null,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var (resolvedCardId, resolveError) = await McpCardResolver.ResolveCardIdAsync(db, cardId, cardNumber, boardId, boardSlug, ct);
        if (resolveError is not null)
        {
            return resolveError;
        }

        if (await ArchiveGuard.IsCardArchivedAsync(db, resolvedCardId!.Value))
        {
            return "Archived cards cannot be edited. Restore the card first.";
        }

        // No-op guard: if no optional params are provided, skip DB writes
        if (name is null && descriptionMarkdown is null && sizeId is null && sizeName is null && laneId is null && index is null && labelIds is null)
        {
            return "No changes specified.";
        }

        var card = await db.Cards.FindAsync([resolvedCardId.Value], ct);
        if (card is null)
        {
            return "Error: Card not found.";
        }

        // Snapshot the content axes BEFORE mutating, so card.updated fires only on a real
        // change (the per-axis no-op guard).
        var oldName = card.Name;
        var oldDescription = card.DescriptionMarkdown;
        var oldSizeId = card.SizeId;
        var oldPosition = card.Position;

        // Snapshot the pre-write last-editor/last-edit too, before this request overwrites them below
        // — the approximate collision signal reads them to tell whether someone else was working this
        // card moments ago.
        var priorEditorId = card.LastUpdatedByUserId;
        var priorEditedAtUtc = card.LastUpdatedAtUtc;

        if (name is not null)
        {
            card.Name = name;
        }

        if (descriptionMarkdown is not null)
        {
            card.DescriptionMarkdown = descriptionMarkdown;
        }

        if (sizeId.HasValue || sizeName is not null)
        {
            var cardBoardId = await db.Lanes.Where(l => l.Id == card.LaneId).Select(l => l.BoardId).FirstOrDefaultAsync(ct);
            var (resolvedSizeId, sizeError) = await SizeResolver.ResolveAsync(db, cardBoardId, sizeId, sizeName, ct);
            if (sizeError is not null)
            {
                return $"Error: {sizeError}";
            }

            card.SizeId = resolvedSizeId!.Value;
        }

        // Lane move: if laneId provided, move card to that lane with optional index.
        // card.moved fires when the card actually lands somewhere new — a different lane, or a
        // different position in its current lane (a size/label/name-only update raises no move
        // event). Snapshot source lane/position before MoveCardToLaneAsync mutates + renumbers
        // both lanes.
        Lane? moveFromLane = null;
        Lane? moveToLane = null;
        var moveFromPosition = 0;

        if (laneId is not null)
        {
            var targetLane = await db.Lanes.FindAsync([laneId.Value], ct);
            if (targetLane is null)
            {
                return "Error: Lane not found.";
            }

            // Block moving TO an archive lane — archiving must go through archive_card.
            // Mirrors move_card and REST PATCH /cards/{id}; without it update_card was a
            // back-door archive that also emitted a wrong card.moved webhook event.
            if (targetLane.IsArchiveLane)
            {
                return "Use archive_card to archive cards.";
            }

            if (laneId.Value != card.LaneId)
            {
                moveFromLane = await db.Lanes.FindAsync([card.LaneId], ct);
                moveToLane = targetLane;
                moveFromPosition = card.Position;
            }

            await CardReorderHelper.MoveCardToLaneAsync(db, card, laneId.Value, index, ct);

            // Within-lane movement is a move, keyed on the resolved position exactly as REST PATCH
            // /cards/{id} keys it: re-asserting the current lane (with or without an index) emits
            // card.moved with equal from/to lane ids when the card's position changed, and stays
            // silent when it did not.
            if (moveToLane is null && card.Position != oldPosition)
            {
                moveFromLane = targetLane;
                moveToLane = targetLane;
                moveFromPosition = oldPosition;
            }
        }

        // Label replace: diff against current assignments. The added/removed sets drive
        // card.labeled / card.unlabeled (one event per add/remove).
        List<Guid> addedLabelIds = [];
        List<Guid> removedLabelIds = [];
        if (labelIds is not null)
        {
            var cardBoardId = await db.Lanes.Where(l => l.Id == card.LaneId).Select(l => l.BoardId).FirstOrDefaultAsync(ct);
            var (desiredLabelIdList, labelError) = await McpLabelParsing.ParseAndValidateLabelIdsAsync(db, labelIds, cardBoardId, ct);
            if (labelError is not null)
            {
                return labelError;
            }

            var desiredLabelIds = desiredLabelIdList.ToHashSet();
            var currentLabels = await db.CardLabels.Where(cl => cl.CardId == card.Id).ToListAsync(ct);
            var currentLabelIds = currentLabels.Select(cl => cl.LabelId).ToHashSet();

            // Remove labels no longer desired
            var toRemove = currentLabels.Where(cl => !desiredLabelIds.Contains(cl.LabelId)).ToList();
            db.CardLabels.RemoveRange(toRemove);
            removedLabelIds = [.. toRemove.Select(cl => cl.LabelId)];

            // Add missing labels
            addedLabelIds = [.. desiredLabelIds.Where(id => !currentLabelIds.Contains(id))];
            foreach (var labelId in addedLabelIds)
            {
                db.CardLabels.Add(new CardLabel { CardId = card.Id, LabelId = labelId });
            }
        }

        card.LastUpdatedByUserId = user!.Id;
        card.LastUpdatedAtUtc = DateTimeOffset.UtcNow;

        // Collision awareness, computed before the save against the state the caller was racing: an
        // exact answer when expectedDescriptionRevision was passed, a best-effort card-level signal
        // otherwise. Only when this write sets the description — the one field lit today. Reports;
        // never blocks (last-write-wins is unchanged). Shared detector with the REST PATCH path.
        CardCollision? collision = null;
        if (descriptionMarkdown is not null)
        {
            collision = await CardCollisionDetector.DetectAsync(db, card.Id, CardHistoryHelper.DescriptionField, expectedDescriptionRevision, priorEditorId, priorEditedAtUtc, user.Id, ct);
        }

        // Staged after all validation and before the single save, so the new description and the
        // record of the old one commit together. Shared with the REST PATCH path so the two
        // surfaces cannot drift on what a description edit records, or on how a concurrent edit
        // racing the same revision number is resolved.
        var descriptionChange = await CardHistoryHelper.StageDescriptionChangeAsync(db, card.Id, oldDescription, card.DescriptionMarkdown, user.Id, ct);

        if (!await CardHistoryHelper.TrySaveWithRevisionRetryAsync(db, descriptionChange, ct))
        {
            return $"Error: {CardHistoryHelper.ContendedMessage}";
        }

        // Multi-axis co-fire: one webhook event per changed axis (content / lane /
        // labels), all riding ONE coalesced SSE bell. Routed through the shared factory seam so
        // REST PATCH and update_card emit the identical event set by construction. Size is a
        // content axis here (alongside name/description); compare post-resolution.
        var contentChanged =
            (name is not null && name != oldName)
            || (descriptionMarkdown is not null && descriptionMarkdown != oldDescription)
            || card.SizeId != oldSizeId;

        var events = await WebhookEventFactory.BuildCardUpdateEventsAsync(db, card, user, contentChanged, moveToLane, moveFromLane, moveFromPosition, addedLabelIds, removedLabelIds, ct);
        broadcaster.PublishCoalesced(card.BoardId, events);

        // CardUpdateResult attaches the collision here, at the write site — never through the shared
        // CardSummaryBuilder — so it cannot appear in get_cards, search or webhook payloads. Its
        // converter flattens the card's fields to the top level with 'collision' beside them, so the
        // returned shape is the enriched summary plus one optional field.
        var summaries = await CardSummaryBuilder.BuildAsync(db, [card], ct);
        return JsonSerializer.Serialize(new CardUpdateResult(summaries[0], collision), JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "get_cards", ReadOnly = true, Destructive = false)]
    [Description("List cards for a board with optional filters. Use the 'since' filter to check for recent activity (includes cards with new/edited comments and new attachments). Returns a paged envelope: { items, totalCount, offset, limit }.")]
    public async Task<string> GetCardsAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The board ID to list cards from")] Guid boardId,
        [Description("Only return cards with activity (created, updated, commented, attachment added) after this date. ISO 8601 format.")] DateTimeOffset? since = null,
        [Description("Only return cards with this label assigned")] Guid? labelId = null,
        [Description("Only return cards in this lane")] Guid? laneId = null,
        [Description("Search term. Prefix with # for card number lookup (e.g. '#42'). Plain numbers match card number or name/description. Text matches name or description.")] string? search = null,
        [Description("Include archived cards in results (default false)")] bool? includeArchived = null,
        [Description("Number of cards to skip (default 0). Use with limit for pagination.")] int? offset = null,
        [Description("Maximum number of cards to return (default 200, max 500). Use to avoid exceeding token limits on large boards.")] int? limit = null,
        CancellationToken ct = default
    )
    {
        var (_, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct))
        {
            return "Error: Board not found.";
        }

        // Board scope + temp exclusion + archive-lane exclusion via the shared helper
        // REST's GET /cards already uses — the same 3-clause exclusion both surfaces
        // must keep identical.
        var query = CardQueryHelper.BoardCards(db.Cards, db.Lanes, boardId, includeArchived is true);

        if (laneId.HasValue)
        {
            query = query.Where(c => c.LaneId == laneId.Value);
        }

        if (since.HasValue)
        {
            query = CardQueryHelper.ApplySinceFilter(query, db, since.Value);
        }

        if (labelId.HasValue)
        {
            var cardIdsWithLabel = db.CardLabels.Where(cl => cl.LabelId == labelId.Value).Select(cl => cl.CardId);
            query = query.Where(c => cardIdsWithLabel.Contains(c.Id));
        }

        query = SearchHelper.ApplySearchFilter(query, search);

        var totalCount = await query.CountAsync(ct);
        var effectiveOffset = Math.Max(offset ?? 0, 0);
        var effectiveLimit = Math.Clamp(limit ?? 200, 1, 500);
        var cards = await query.OrderBy(c => c.LaneId).ThenBy(c => c.Position).Skip(effectiveOffset).Take(effectiveLimit).ToListAsync(ct);
        var summaries = await CardSummaryBuilder.BuildAsync(db, cards, ct);
        var paged = new PagedResult<CardSummary>(summaries, totalCount, effectiveOffset, effectiveLimit);
        return JsonSerializer.Serialize(paged, JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "get_card", ReadOnly = true, Destructive = false)]
    [Description("Get a single card by its ID or card number — labels, attachments (metadata only), user names, isArchived, and descriptionHistoryCount (how many description revisions get_card_history would return; zero means nothing to show, never one, since a first edit records both the old and new value). ALWAYS PASS commentsLimit. With commentsLimit, comments come back as a paged envelope { items, totalCount, offset, limit } newest activity first (commentsLimit=0 = count only with no bodies; max 500; page further with commentsOffset) — the shape to use on any card whose comment thread may be long. WITHOUT commentsLimit you get the DEPRECATED legacy path: the whole comment thread as a plain array, oldest-first and unbounded, kept only for byte-compatibility with older clients and to be REMOVED at a future major version — do not rely on it, pass commentsLimit. FIELD PROJECTION: includeDescription=false drops the description body (the largest single field on a heavy card). Each comment carries createdAtUtc (stamped once at posting) beside lastUpdatedAtUtc (bumped on edit). To download attachment content, GET /api/v1/attachments/{id} with X-User-Key header.")]
    public async Task<string> GetCardAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the card (provide this or cardNumber)")] Guid? cardId = null,
        [Description("The card number (provide this or cardId). Requires boardId or boardSlug.")] long? cardNumber = null,
        [Description("Board ID (required when using cardNumber)")] Guid? boardId = null,
        [Description("Board slug (alternative to boardId when using cardNumber)")] string? boardSlug = null,
        [Description("Include the card's full description body (default true). Pass false to omit it — on a heavy card the description is the largest single field, and this drops it when you only need metadata or comments.")] bool includeDescription = true,
        [Description("Number of comments to skip, counting from the newest, to page a long thread. Requires commentsLimit — passing commentsOffset without commentsLimit is an error.")] int? commentsOffset = null,
        [Description("ALWAYS PASS THIS. Maximum comments to return, newest activity first (max 500; 0 = count only with no bodies). Passing it selects the paged comment envelope { items, totalCount, offset, limit }. OMITTING it selects the DEPRECATED legacy full comment array (whole thread, oldest-first, unbounded), slated for removal at a future major — so pass a value here even on a small card.")] int? commentsLimit = null,
        CancellationToken ct = default
    )
    {
        var (_, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var (resolvedCardId, resolveError) = await McpCardResolver.ResolveCardIdAsync(db, cardId, cardNumber, boardId, boardSlug, ct);
        if (resolveError is not null)
        {
            return resolveError;
        }

        // commentsOffset is a paging control and means nothing without a page size — fail loud rather
        // than silently ignore it, so a caller who set the offset but forgot the limit is told, not
        // handed the whole thread as if they had asked for it.
        if (commentsLimit is null && commentsOffset is not null)
        {
            return "Error: commentsOffset requires commentsLimit. Pass commentsLimit to page the comment thread (the paged envelope), or omit both for the full comment array.";
        }

        var card = await db.Cards.FindAsync([resolvedCardId!.Value], ct);
        if (card is null)
        {
            return "Error: Card not found.";
        }

        // No commentsLimit → the DEPRECATED legacy path: the whole comment thread as a plain array,
        // oldest activity first, byte-compatible with what production served before comment paging. Kept
        // for older clients and slated for removal at a future major; the tool description steers callers
        // to always pass commentsLimit. Shares CardDetailBuilder with REST v1 GET /cards/{id}.
        if (commentsLimit is null)
        {
            var legacy = await CardDetailBuilder.BuildLegacyAsync(db, card, includeDescription, ct);
            return JsonSerializer.Serialize(legacy, JsonSerializerOptions.Web);
        }

        // commentsLimit present → the paged envelope. Capped like get_cards and get_card_history: this is
        // the surface that pays per token, and the comment thread is unbounded. commentsLimit = 0 is the
        // count-only read; comments.totalCount always reports the whole thread so the cap is visible.
        // Shares CardDetailBuilder with REST v2 GET /api/v2/cards/{id}, so the two paged surfaces can't drift.
        var effectiveCommentsOffset = Math.Max(commentsOffset ?? 0, 0);
        var effectiveCommentsLimit = commentsLimit.Value == 0
            ? 0
            : Math.Clamp(commentsLimit.Value, 1, 500);

        var detail = await CardDetailBuilder.BuildAsync(db, card, includeDescription, effectiveCommentsOffset, effectiveCommentsLimit, ct);
        return JsonSerializer.Serialize(detail, JsonSerializerOptions.Web);
    }
}
