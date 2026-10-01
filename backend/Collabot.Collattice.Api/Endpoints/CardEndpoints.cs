using System.Globalization;
using Collabot.Collattice.Api.Auth;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

internal static class CardEndpoints
{
    private const string _notADraftMessage = "Card is not a temp card.";

    public static RouteGroupBuilder MapCardEndpoints(this RouteGroupBuilder group)
    {
        // Board-scoped listing and creation
        group.MapGet("/boards/{boardId:guid}/cards", async (BoardDbContext db, Guid boardId, DateTimeOffset? since, Guid? labelId, Guid? laneId, string? search, bool? includeArchived, int? offset, int? limit, CancellationToken ct) =>
        {
            if (!await db.Boards.AnyAsync(x => x.Id == boardId, ct))
            {
                return Results.NotFound();
            }

            var query = CardQueryHelper.BoardCards(db.Cards, db.Lanes, boardId, includeArchived is true);

            if (laneId.HasValue)
            {
                query = query.Where(x => x.LaneId == laneId.Value);
            }

            if (since.HasValue)
            {
                query = CardQueryHelper.ApplySinceFilter(query, db, since.Value);
            }

            if (labelId.HasValue)
            {
                var cardIdsWithLabel = db.CardLabels.Where(cl => cl.LabelId == labelId.Value).Select(cl => cl.CardId);
                query = query.Where(x => cardIdsWithLabel.Contains(x.Id));
            }

            query = SearchHelper.ApplySearchFilter(query, search);

            var orderedQuery = CardQueryHelper.OrderForBoard(query);

            // Two queries: COUNT then offset/limit. The count re-executes filter predicates
            // (including since subqueries). Acceptable at current scale; revisit if perf degrades.
            var totalCount = await query.CountAsync(ct);

            var effectiveOffset = Math.Max(offset ?? 0, 0);
            int? effectiveLimit = limit.HasValue ? Math.Clamp(limit.Value, 1, 200) : null;

            var pagedQuery = orderedQuery.Skip(effectiveOffset);
            if (effectiveLimit.HasValue)
            {
                pagedQuery = pagedQuery.Take(effectiveLimit.Value);
            }

            var cards = await pagedQuery.ToListAsync(ct);
            var summaries = await CardSummaryBuilder.BuildAsync(db, cards, ct);
            return Results.Ok(new PagedResult<CardSummary>(summaries, totalCount, effectiveOffset, effectiveLimit));
        }).RequireAuth();

        group.MapPost("/boards/{boardId:guid}/cards", async (BoardDbContext db, HttpContext http, Guid boardId, CreateCardRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            if (!await db.Boards.AnyAsync(x => x.Id == boardId, ct))
            {
                return Results.NotFound();
            }

            var (labelIds, labelError) = await ValidateCreateLabelsAsync(db, request.LabelIds, boardId, ct);
            if (labelError is not null)
            {
                return Results.BadRequest(labelError);
            }

            var (card, error) = await CardCreateHelper.BuildCardAsync(db, boardId, request, labelIds, http.CurrentUser(), ct);
            if (error is not null)
            {
                return Results.BadRequest(error);
            }

            if (!await CardNumberHelper.TryInsertCardWithAutoNumberAsync(db, card!, boardId, ct))
            {
                return Results.Conflict(CardNumberHelper.ContendedMessage);
            }

            await WebhookEventFactory.PublishCardCreatedAsync(db, broadcaster, card!, http.CurrentUser(), ct);

            var summaries = await CardSummaryBuilder.BuildAsync(db, [card!], ct);
            return Results.Created($"/api/v1/cards/{card!.Id}", summaries[0]);
        }).RequireAuth();

        // By-ID operations (flat)
        //
        // v1 card detail is a DEPRECATED resource (RFC 9745): it restores the v2.0.2 production shape —
        // comments as a plain array (the whole thread, oldest activity first) plus the additive-only
        // createdAtUtc / descriptionHistoryCount fields — so a consumer written against the last release
        // deserializes it unchanged. The paged successor is GET /api/v2/cards/{id}; every response here advertises
        // that with Deprecation + Link headers. includeDescription stays (an additive, never-breaking
        // projection); commentsOffset/commentsLimit are the paged surface's and live only on v2.
        group.MapGet("/cards/{id:guid}", async (BoardDbContext db, HttpContext http, Guid id, bool? includeDescription, CancellationToken ct) =>
        {
            // Deprecation is a property of the resource, not of a particular card, so it is advertised on
            // every response from this route — the 404 as much as the 200.
            StampV1CardDetailDeprecation(http.Response, id);

            var card = await db.Cards.FindAsync([id], ct);
            if (card is null)
            {
                return Results.NotFound();
            }

            var detail = await CardDetailBuilder.BuildLegacyAsync(db, card, includeDescription ?? true, ct);
            return Results.Ok(detail);
        }).RequireAuth();

        group.MapPatch("/cards/{id:guid}", async (BoardDbContext db, HttpContext http, Guid id, UpdateCardRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var card = await db.Cards.FindAsync([id], ct);
            if (card is null)
            {
                return Results.NotFound();
            }

            if (await ArchiveGuard.IsCardArchivedAsync(db, id))
            {
                return Results.BadRequest("Archived cards cannot be modified. Restore the card first.");
            }

            if (card.IsTemp)
            {
                return Results.BadRequest("Temp cards cannot be modified via this endpoint.");
            }

            // Snapshot the content axes BEFORE mutating, so card.updated fires only on a real
            // change (the per-axis no-op guard). Name/description/size are the content
            // axes; lane → card.moved, labels → card.labeled/unlabeled, each guarded separately.
            var oldName = card.Name;
            var oldDescription = card.DescriptionMarkdown;
            var oldSizeId = card.SizeId;
            var oldPosition = card.Position;

            // Snapshot the pre-write last-editor/last-edit too, before this request overwrites them
            // below — the approximate collision signal reads them to tell whether someone else was
            // working this card moments ago.
            var priorEditorId = card.LastUpdatedByUserId;
            var priorEditedAtUtc = card.LastUpdatedAtUtc;

            if (request.Name is not null)
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest("Name cannot be empty.");
                }

                card.Name = request.Name;
            }

            if (request.DescriptionMarkdown is not null)
            {
                card.DescriptionMarkdown = request.DescriptionMarkdown;
            }

            if (request.SizeId is not null)
            {
                var newSizeId = request.SizeId.Value;
                var sizeLane = await db.Lanes.FindAsync([card.LaneId], ct);
                if (sizeLane is null || !await db.CardSizes.AnyAsync(s => s.Id == newSizeId && s.BoardId == sizeLane.BoardId, ct))
                {
                    return Results.BadRequest("Size does not belong to this board.");
                }

                card.SizeId = newSizeId;
            }

            // This block handles a lane change; a within-lane position change is handled after the
            // position mutation below. A name/size/label-only update raises no move event. This
            // site mutates LaneId/Position INLINE (it does not route through MoveCardToLaneAsync),
            // so the source lane/position must be snapshotted before the mutation. Resolved only on
            // a real lane change.
            Lane? moveFromLane = null;
            Lane? moveToLane = null;
            var moveFromPosition = 0;

            if (request.LaneId is not null)
            {
                var newLaneId = request.LaneId.Value;
                var targetLane = await db.Lanes.FindAsync([newLaneId], ct);
                if (targetLane is null)
                {
                    return Results.BadRequest("Lane not found.");
                }

                if (targetLane.BoardId != card.BoardId)
                {
                    return Results.BadRequest("Lane does not belong to this board.");
                }

                if (targetLane.IsArchiveLane)
                {
                    return Results.BadRequest("Use the archive endpoint to archive cards.");
                }

                if (newLaneId != card.LaneId)
                {
                    moveFromLane = await db.Lanes.FindAsync([card.LaneId], ct);
                    moveToLane = targetLane;
                    moveFromPosition = card.Position;
                }

                card.LaneId = newLaneId;

                if (request.Position is null)
                {
                    var maxPosition = await db.Cards.Where(c => c.LaneId == newLaneId && c.Id != id).MaxAsync(c => (int?)c.Position, ct) ?? -10;
                    card.Position = maxPosition + 10;
                }
            }

            if (request.Position is not null)
            {
                card.Position = request.Position.Value;
            }

            // Within-lane movement is a move: a position change with no lane change emits card.moved
            // with equal from/to lane ids, so it is reported the same as a within-lane reorder. The
            // guard is the resolved position, not how the request expressed it — an explicit position,
            // or re-asserting the current lane with no position (which re-appends the card to the
            // lane's end), both count when the card actually lands somewhere new. A same-value result
            // is a no-op and stays silent. The lane-change block above already set the move for a
            // genuine lane change; this covers every within-lane case.
            if (moveToLane is null && card.Position != oldPosition)
            {
                var currentLane = await db.Lanes.FindAsync([card.LaneId], ct);
                moveFromLane = currentLane;
                moveToLane = currentLane;
                moveFromPosition = oldPosition;
            }

            // Label diff captured for card.labeled / card.unlabeled (one event per
            // add/remove). Computed against the current assignments before the replace.
            List<Guid> addedLabelIds = [];
            List<Guid> removedLabelIds = [];

            if (request.LabelIds is not null)
            {
                if (request.LabelIds.Length > 0)
                {
                    var validCount = await db.Labels.CountAsync(l => request.LabelIds.Contains(l.Id) && l.BoardId == card.BoardId, ct);
                    if (validCount != request.LabelIds.Length)
                    {
                        return Results.BadRequest("One or more labels do not belong to this board.");
                    }
                }

                var existingLabels = await db.CardLabels.Where(x => x.CardId == id).ToListAsync(ct);
                var oldLabelIds = existingLabels.Select(x => x.LabelId).ToHashSet();
                var newLabelIds = request.LabelIds.ToHashSet();
                addedLabelIds = [.. newLabelIds.Where(labelId => !oldLabelIds.Contains(labelId))];
                removedLabelIds = [.. oldLabelIds.Where(labelId => !newLabelIds.Contains(labelId))];

                db.CardLabels.RemoveRange(existingLabels);
                foreach (var labelId in request.LabelIds)
                {
                    db.CardLabels.Add(new CardLabel { CardId = id, LabelId = labelId });
                }
            }

            var actor = http.CurrentUser();
            card.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            card.LastUpdatedByUserId = actor.Id;

            // Collision awareness, computed before the save against the state the caller was racing:
            // an exact answer when the caller passed the revision it read, a best-effort card-level
            // signal otherwise. Only when this write sets the description — the one field lit today.
            // Reports; never blocks (last-write-wins is unchanged).
            CardCollision? collision = null;
            if (request.DescriptionMarkdown is not null)
            {
                collision = await CardCollisionDetector.DetectAsync(db, card.Id, CardHistoryHelper.DescriptionField, request.ExpectedDescriptionRevision, priorEditorId, priorEditedAtUtc, actor.Id, ct);
            }

            // Staged after all validation and before the single save, so the new description and the
            // record of the old one commit together — a description can never replace an unrecorded
            // one. Shared with the MCP update_card path so the two surfaces cannot drift, including
            // on how a concurrent edit racing the same revision number is resolved.
            var descriptionChange = await CardHistoryHelper.StageDescriptionChangeAsync(db, card.Id, oldDescription, card.DescriptionMarkdown, actor.Id, ct);

            if (!await CardHistoryHelper.TrySaveWithRevisionRetryAsync(db, descriptionChange, ct))
            {
                return Results.Conflict(CardHistoryHelper.ContendedMessage);
            }

            // Multi-axis co-fire: a single PATCH can change content + lane + labels and
            // emits one webhook event per CHANGED axis, while ringing EXACTLY ONE SSE bell via
            // PublishCoalesced (the byte-for-byte-unchanged safety property). Unchanged axes emit
            // nothing; an all-no-op PATCH still rings the one bell (empty event list).
            var contentChanged =
                (request.Name is not null && request.Name != oldName)
                || (request.DescriptionMarkdown is not null && request.DescriptionMarkdown != oldDescription)
                || (request.SizeId is not null && request.SizeId.Value != oldSizeId);

            var events = await WebhookEventFactory.BuildCardUpdateEventsAsync(db, card, actor, contentChanged, moveToLane, moveFromLane, moveFromPosition, addedLabelIds, removedLabelIds, ct);
            broadcaster.PublishCoalesced(card.BoardId, events);

            // CardUpdateResult attaches the collision here, at the write site — never through the
            // shared CardSummaryBuilder — so it cannot appear in list, search or webhook payloads.
            var summaries = await CardSummaryBuilder.BuildAsync(db, [card], ct);
            return Results.Ok(new CardUpdateResult(summaries[0], collision));
        }).RequireAuth();

        group.MapPost("/cards/{id:guid}/reorder", async (BoardDbContext db, HttpContext http, Guid id, ReorderCardRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var card = await db.Cards.FindAsync([id], ct);
            if (card is null)
            {
                return Results.NotFound();
            }

            if (await ArchiveGuard.IsCardArchivedAsync(db, id))
            {
                return Results.BadRequest("Archived cards cannot be modified. Restore the card first.");
            }

            if (await TempGuard.IsCardTempAsync(db, id))
            {
                return Results.BadRequest("Temp cards cannot be reordered.");
            }

            if (request.LaneId is null)
            {
                return Results.BadRequest("laneId is required.");
            }

            var targetLaneId = request.LaneId.Value;

            if (request.Index is null)
            {
                return Results.BadRequest("index is required.");
            }

            var targetIndex = request.Index.Value;

            var targetLane = await db.Lanes.FindAsync([targetLaneId], ct);
            if (targetLane is null)
            {
                return Results.BadRequest("Lane not found.");
            }

            if (targetLane.IsArchiveLane)
            {
                return Results.BadRequest("Use archive_card to archive cards.");
            }

            var sourceLane = await db.Lanes.FindAsync([card.LaneId], ct);
            if (sourceLane is null || sourceLane.BoardId != targetLane.BoardId)
            {
                return Results.BadRequest("Cannot move cards between boards.");
            }

            // Snapshot source lane/position BEFORE MoveCardToLaneAsync mutates + renumbers
            // both lanes — once it runs, the card's source position is gone.
            var fromPosition = card.Position;

            await CardReorderHelper.MoveCardToLaneAsync(db, card, targetLaneId, targetIndex, ct);

            card.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            card.LastUpdatedByUserId = http.CurrentUser().Id;

            await db.SaveChangesAsync(ct);
            await WebhookEventFactory.PublishCardMovedAsync(db, broadcaster, card, sourceLane, fromPosition, targetLane, http.CurrentUser(), ct);

            var boardLaneIds = await db.Lanes.Where(x => x.BoardId == targetLane.BoardId).Select(x => x.Id).ToListAsync(ct);
            var lanes = await db.Lanes.Where(x => x.BoardId == targetLane.BoardId).OrderBy(l => l.Position).ToListAsync(ct);
            var cards = await db.Cards.Where(x => boardLaneIds.Contains(x.LaneId)).OrderBy(c => c.LaneId).ThenBy(c => c.Position).ToListAsync(ct);
            return Results.Ok(new { lanes, cards });
        }).RequireAuth();

        group.MapDelete("/cards/{id:guid}", async (BoardDbContext db, HttpContext http, Guid id, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var card = await db.Cards.FindAsync([id], ct);
            if (card is null)
            {
                return Results.NotFound();
            }

            var user = http.CurrentUser();
            if (user.Role != UserRole.Administrator && card.CreatedByUserId != user.Id)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // Build the card.deleted event while the card still exists — the fat summary enriches by
            // querying the card id, so building after the Remove would blank labels/counts. Publish
            // (SSE bell + webhook) only after the row is gone. This replaces the bare SSE bell the
            // delete used to ring.
            var deletedEvent = await WebhookEventFactory.BuildCardDeletedAsync(db, card, user, ct);

            db.Cards.Remove(card);
            await db.SaveChangesAsync(ct);

            broadcaster.Publish(deletedEvent);

            return Results.NoContent();
        }).RequireRole(UserRole.Administrator, UserRole.HumanUser);

        group.MapPost("/cards/{id:guid}/archive", async (BoardDbContext db, HttpContext http, Guid id, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var card = await db.Cards.FindAsync([id], ct);
            if (card is null)
            {
                return Results.NotFound();
            }

            if (card.IsTemp)
            {
                return Results.BadRequest("Temp cards cannot be archived.");
            }

            var currentLane = await db.Lanes.FindAsync([card.LaneId], ct);
            if (currentLane is not null && currentLane.IsArchiveLane)
            {
                return Results.BadRequest("Card is already archived.");
            }

            var archiveLane = await db.Lanes.FirstOrDefaultAsync(l => l.BoardId == card.BoardId && l.IsArchiveLane, ct);
            if (archiveLane is null)
            {
                return Results.BadRequest("Board has no archive lane.");
            }

            await CardReorderHelper.MoveCardToLaneAsync(db, card, archiveLane.Id, 0, ct);

            card.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            card.LastUpdatedByUserId = http.CurrentUser().Id;

            await db.SaveChangesAsync(ct);

            // card.archived — emitted at the call-site, NOT card.moved (the shared move
            // helper stays emission-free). One webhook event + the same SSE bell.
            await WebhookEventFactory.PublishCardArchivedAsync(db, broadcaster, card, http.CurrentUser(), ct);

            return Results.NoContent();
        }).RequireAuth();

        group.MapPost("/cards/{id:guid}/restore", async (BoardDbContext db, HttpContext http, Guid id, RestoreCardRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var card = await db.Cards.FindAsync([id], ct);
            if (card is null)
            {
                return Results.NotFound();
            }

            var currentLane = await db.Lanes.FindAsync([card.LaneId], ct);
            if (currentLane is null || !currentLane.IsArchiveLane)
            {
                return Results.BadRequest("Card is not archived.");
            }

            var targetLane = await db.Lanes.FindAsync([request.LaneId], ct);
            if (targetLane is null)
            {
                return Results.NotFound();
            }

            if (targetLane.IsArchiveLane)
            {
                return Results.BadRequest("Cannot restore to an archive lane.");
            }

            if (targetLane.BoardId != card.BoardId)
            {
                return Results.BadRequest("Lane does not belong to this board.");
            }

            await CardReorderHelper.MoveCardToLaneAsync(db, card, targetLane.Id, 0, ct);

            card.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            card.LastUpdatedByUserId = http.CurrentUser().Id;

            await db.SaveChangesAsync(ct);

            // card.restored — emitted at the call-site, NOT card.moved.
            await WebhookEventFactory.PublishCardRestoredAsync(db, broadcaster, card, http.CurrentUser(), ct);

            return Results.NoContent();
        }).RequireAuth();

        // Temp card lifecycle endpoints
        group.MapPost("/boards/{boardId:guid}/cards/temp", async (BoardDbContext db, HttpContext http, Guid boardId, CreateCardRequest request, CancellationToken ct) =>
        {
            if (!await db.Boards.AnyAsync(x => x.Id == boardId, ct))
            {
                return Results.NotFound();
            }

            var (labelIds, labelError) = await ValidateCreateLabelsAsync(db, request.LabelIds, boardId, ct);
            if (labelError is not null)
            {
                return Results.BadRequest(labelError);
            }

            var (card, error) = await CardCreateHelper.BuildCardAsync(db, boardId, request, labelIds, http.CurrentUser(), ct);
            if (error is not null)
            {
                return Results.BadRequest(error);
            }

            card!.Number = 0;
            card.IsTemp = true;
            db.Cards.Add(card);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/cards/{card.Id}", new { card.Id });
        }).RequireAuth();

        group.MapPost("/cards/{id:guid}/finalize", async (BoardDbContext db, HttpContext http, Guid id, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var card = await db.Cards.FindAsync([id], ct);
            if (card is null)
            {
                return Results.NotFound();
            }

            if (!card.IsTemp)
            {
                return Results.BadRequest(_notADraftMessage);
            }

            if (http.CurrentUser().Id != card.CreatedByUserId)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            card.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            card.LastUpdatedByUserId = http.CurrentUser().Id;

            try
            {
                if (!await CardNumberHelper.TryFinalizeCardNumberAsync(db, card, card.BoardId, ct))
                {
                    return Results.Conflict(CardNumberHelper.ContendedMessage);
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                return await AnswerDraftSettledMeanwhileAsync(db, id, ct);
            }

            // card.created fires here, on finalize — never at temp-insert (a temp card is
            // invisible pre-creation limbo and may be cancelled). The cancel site emits
            // nothing. (the temp-card create wrinkle.)
            await WebhookEventFactory.PublishCardCreatedAsync(db, broadcaster, card, http.CurrentUser(), ct);
            return Results.Ok(new { card.Id, card.Number });
        }).RequireAuth();

        group.MapPost("/cards/{id:guid}/cancel", async (BoardDbContext db, HttpContext http, Guid id, CancellationToken ct) =>
        {
            var card = await db.Cards.FindAsync([id], ct);
            if (card is null)
            {
                return Results.NotFound();
            }

            if (!card.IsTemp)
            {
                return Results.BadRequest(_notADraftMessage);
            }

            if (http.CurrentUser().Id != card.CreatedByUserId)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            db.Cards.Remove(card);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return await AnswerDraftSettledMeanwhileAsync(db, id, ct);
            }

            return Results.NoContent();
        }).RequireAuth();

        return group;

        // A finalize or cancel whose save found the draft already finalized or cancelled by another
        // request since it was loaded. It answers as it would have arriving after that request: no
        // longer a draft if the card is still there, not found if it was cancelled.
        static async Task<IResult> AnswerDraftSettledMeanwhileAsync(BoardDbContext db, Guid id, CancellationToken ct) =>
            await db.Cards.AnyAsync(c => c.Id == id, ct)
                ? Results.BadRequest(_notADraftMessage)
                : Results.NotFound();

        // Validates that every requested label belongs to the board, returning the
        // validated list (empty when none requested) or a REST-worded error. The
        // REST create paths own their own label-error wording; CardCreateHelper stages
        // the already-validated list. Shared by the standard and temp create
        // endpoints so the rule lives in one place.
        static async Task<(IReadOnlyList<Guid> LabelIds, string? Error)> ValidateCreateLabelsAsync
        (
            BoardDbContext db,
            Guid[]? requestedLabelIds,
            Guid boardId,
            CancellationToken ct
        )
        {
            if (requestedLabelIds is null || requestedLabelIds.Length == 0)
            {
                return ([], null);
            }

            var validCount = await db.Labels.CountAsync(l => requestedLabelIds.Contains(l.Id) && l.BoardId == boardId, ct);
            if (validCount != requestedLabelIds.Length)
            {
                return ([], "One or more labels do not belong to this board.");
            }

            return (requestedLabelIds, null);
        }
    }

    // RFC 9745 requires the Deprecation header's value to be a structured-field Date (§2.1) — there is
    // no "deprecated, date unknown" form — so this is the date v1 card detail was declared deprecated.
    // It is a fixed point (the ruling date), stable across responses and reading as a past date once
    // shipped. No Sunset header is emitted: the removal date does not exist yet (a future MAJOR sets
    // it, and §4 keeps Sunset separate from Deprecation for exactly this "deprecated but no end date"
    // case). Only this resource is deprecated; the rest of v1 lives on.
    private static readonly long _v1CardDetailDeprecatedAtUnixSeconds =
        new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    // Advertises the deprecation of v1 GET /cards/{id} on the response: a Deprecation date and a Link
    // to the paged successor (v2), the successor-version relation of RFC 8288 / RFC 5829. The successor
    // target is an absolute-path reference so it resolves correctly behind any host or reverse proxy.
    private static void StampV1CardDetailDeprecation(HttpResponse response, Guid id)
    {
        response.Headers.Append("Deprecation", $"@{_v1CardDetailDeprecatedAtUnixSeconds.ToString(CultureInfo.InvariantCulture)}");
        response.Headers.Append("Link", $"</api/v2/cards/{id}>; rel=\"successor-version\"");
    }
}
