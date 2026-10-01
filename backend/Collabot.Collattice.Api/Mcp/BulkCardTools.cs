using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Mcp;

// Bulk card tools. Three all-roles tools that batch the
// per-card archive_card / restore_card / update_card analogs over N cards in one
// call. Each follows the two-phase contract:
//
//   Phase 1 — pre-validation (fail loud, single "Error: ..." string, NO mutations):
//     ref shape/parse, card existence (one round-trip), and operation premises
//     (restore: all cards' boards match the target lane's board; update: target
//     lane/size/labels exist, are not archive-lane, and are board-consistent with
//     every card). Any failure here returns one error string and writes nothing.
//
//   Phase 2 — per-card execution (best-effort, per-item envelope):
//     iterate cards in stable input order; capture per-card ok/error; one card's
//     failure never aborts the loop. All staged changes persist in a SINGLE
//     SaveChangesAsync at the end, and each affected board is broadcast exactly
//     ONCE (deduplicated). If the final SaveChanges itself throws, the whole batch
//     is reported failed via an error string.
//
// These are all-roles tools — they gate via RequireUserAsync (the per-card analogs
// they batch are all-roles today), NOT RequireAdminLevelAsync.
[McpServerToolType]
public sealed class BulkCardTools(BoardDbContext db, McpAuthService auth, BoardEventBroadcaster broadcaster, IWebhookSink webhookSink)
{
    [McpServerTool(Name = "bulk_archive_cards", Destructive = false)]
    [Description("Archive multiple cards in a single call (move them to their boards' archive lanes). Provide cardIds (CSV of card GUIDs) OR cardNumbers (CSV) + boardId/boardSlug, not both. Pre-validates all refs (fails loud with no mutations if any is invalid or missing), then archives best-effort. Returns a per-card result envelope: { totalRequested, succeeded, failed, results: [{ cardId, number, status, error? }] } aligned 1:1 with the input order.")]
    public async Task<string> BulkArchiveCardsAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("CSV of card GUIDs (provide this OR cardNumbers)")] string? cardIds = null,
        [Description("CSV of card numbers (requires boardId or boardSlug)")] string? cardNumbers = null,
        [Description("Board ID (required with cardNumbers)")] Guid? boardId = null,
        [Description("Board slug (alternative to boardId, with cardNumbers)")] string? boardSlug = null,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var (cards, refError) = await McpCardResolver.ResolveCardRefsAsync(db, cardIds, cardNumbers, boardId, boardSlug, ct);
        if (refError is not null)
        {
            return refError;
        }

        // Archive lanes for the affected boards (one round-trip; keyed by board).
        var affectedBoardIds = cards!.Select(c => c.BoardId).Distinct().ToList();
        var archiveLanes = await db.Lanes
            .Where(l => affectedBoardIds.Contains(l.BoardId) && l.IsArchiveLane)
                .ToListAsync(ct);
        var archiveLaneByBoard = archiveLanes.ToDictionary(l => l.BoardId, l => l.Id);
        var archiveLaneIds = archiveLanes.Select(l => l.Id).ToHashSet();

        var now = DateTimeOffset.UtcNow;
        var execution = new BulkExecution(db, broadcaster, user!.Id, now);

        await execution.RunAsync(cards!, card =>
        {
            var perCardError = archiveLaneIds.Contains(card.LaneId)
                ? "Card is already archived."
                : archiveLaneByBoard.ContainsKey(card.BoardId) ? null : "Board has no archive lane.";

            return Task.FromResult(perCardError);
        });

        // card.archived per succeeded card — one webhook event each, one SSE bell per board
        // (the BulkExecution coalesce); built in one batch pass off the after-save hook.
        return await execution.SaveAndSerializeAsync
        (
            ct,
            beforeSave: async succeededCards =>
            {
                foreach (var boardCards in succeededCards.GroupBy(c => c.BoardId))
                {
                    await CardReorderHelper.MoveCardsToLaneAsync(db, [.. boardCards], archiveLaneByBoard[boardCards.Key], 0, ct);
                }
            },
            afterSave: async succeededCards =>
            {
                foreach (var archived in await WebhookEventFactory.BuildCardArchivedBatchAsync(db, succeededCards, user!, ct))
                {
                    webhookSink.Enqueue(archived);
                }
            }
        );
    }

    [McpServerTool(Name = "bulk_restore_cards", Destructive = false)]
    [Description("Restore multiple archived cards to a single target lane in one call. Provide cardIds (CSV of card GUIDs) OR cardNumbers (CSV) + boardId/boardSlug, not both. All cards must be on the same board as the target lane — cross-board mixing is rejected up-front with no mutations. Pre-validates all refs and the board match, then restores best-effort. Restored cards go to the top of the target lane, next to each other in the order you list them. Returns a per-card result envelope: { totalRequested, succeeded, failed, results: [{ cardId, number, status, error? }] }.")]
    public async Task<string> BulkRestoreCardsAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("Target lane ID to restore the cards into (required)")] Guid targetLaneId,
        [Description("CSV of card GUIDs (provide this OR cardNumbers)")] string? cardIds = null,
        [Description("CSV of card numbers (requires boardId or boardSlug)")] string? cardNumbers = null,
        [Description("Board ID (required with cardNumbers)")] Guid? boardId = null,
        [Description("Board slug (alternative to boardId, with cardNumbers)")] string? boardSlug = null,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var (cards, refError) = await McpCardResolver.ResolveCardRefsAsync(db, cardIds, cardNumbers, boardId, boardSlug, ct);
        if (refError is not null)
        {
            return refError;
        }

        // Phase 1 premise: target lane exists, is not an archive lane, and every
        // card is on the same board as the target lane.
        var targetLane = await db.Lanes.FindAsync([targetLaneId], ct);
        if (targetLane is null)
        {
            return "Error: Lane not found.";
        }

        if (targetLane.IsArchiveLane)
        {
            return "Error: Cannot restore to an archive lane.";
        }

        var crossBoard = cards!.Where(c => c.BoardId != targetLane.BoardId).ToList();
        if (crossBoard.Count > 0)
        {
            return $"Error: All cards must be on the target lane's board. Cards not on board {targetLane.BoardId}: {string.Join(", ", crossBoard.Select(c => $"#{c.Number.ToString(CultureInfo.InvariantCulture)}"))}";
        }

        var archiveLaneIds = await db.Lanes
            .Where(l => l.BoardId == targetLane.BoardId && l.IsArchiveLane)
                .Select(l => l.Id)
                    .ToListAsync(ct);
        var archiveLaneIdSet = archiveLaneIds.ToHashSet();

        var now = DateTimeOffset.UtcNow;
        var execution = new BulkExecution(db, broadcaster, user!.Id, now);

        await execution.RunAsync(cards!, card => Task.FromResult(archiveLaneIdSet.Contains(card.LaneId) ? null : "Card is not archived."));

        // card.restored per succeeded card — NOT card.moved. One SSE bell per board.
        return await execution.SaveAndSerializeAsync
        (
            ct,
            beforeSave: succeededCards => CardReorderHelper.MoveCardsToLaneAsync(db, succeededCards, targetLaneId, 0, ct),
            afterSave: async succeededCards =>
            {
                foreach (var restored in await WebhookEventFactory.BuildCardRestoredBatchAsync(db, succeededCards, user!, ct))
                {
                    webhookSink.Enqueue(restored);
                }
            }
        );
    }

    [McpServerTool(Name = "bulk_update_cards", Destructive = false)]
    [Description("Apply a uniform update to multiple cards in one call — lane/position move, size change, and/or label-set replace. (bulk_move_cards is folded in here: pass laneId to move N cards to one lane.) Per-card name/description bulk update is NOT offered. Provide cardIds (CSV of card GUIDs) OR cardNumbers (CSV) + boardId/boardSlug, not both. For labelIds, pass a CSV of label GUIDs or a JSON array string to replace all current labels (empty clears all). When laneId, sizeId/sizeName, or labelIds is provided, all cards must be on the same board as that target (validated up-front, no mutations on failure). Archived cards are rejected per-card. Returns a per-card result envelope: { totalRequested, succeeded, failed, results: [{ cardId, number, status, error? }] }.")]
    public async Task<string> BulkUpdateCardsAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("CSV of card GUIDs (provide this OR cardNumbers)")] string? cardIds = null,
        [Description("CSV of card numbers (requires boardId or boardSlug)")] string? cardNumbers = null,
        [Description("Target lane ID to move all cards to (optional)")] Guid? laneId = null,
        [Description("0-based index position in the target lane (optional, requires laneId — defaults to top of lane). The moved cards land next to each other in the order you list them, starting at this index. The index counts only the lane's cards that are not being moved, and those keep their order.")] int? index = null,
        [Description("New size ID (guid, optional) for all cards")] Guid? sizeId = null,
        [Description("New size name (e.g. 'M', 'XL', optional) for all cards. Used if sizeId is not provided.")] string? sizeName = null,
        [Description("Label GUIDs to replace current labels on all cards (optional). Accepts comma-separated ('guid1,guid2') or a JSON array string ('[\"guid1\",\"guid2\"]'). Empty string or empty array clears all.")] string? labelIds = null,
        [Description("Board ID (required with cardNumbers)")] Guid? boardId = null,
        [Description("Board slug (alternative to boardId, with cardNumbers)")] string? boardSlug = null,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var hasLane = laneId.HasValue;
        var hasSize = sizeId.HasValue || sizeName is not null;
        var hasLabels = labelIds is not null;
        if (!hasLane && !hasSize && !hasLabels)
        {
            return "Error: No changes specified. Provide laneId, sizeId/sizeName, and/or labelIds.";
        }

        var (cards, refError) = await McpCardResolver.ResolveCardRefsAsync(db, cardIds, cardNumbers, boardId, boardSlug, ct);
        if (refError is not null)
        {
            return refError;
        }

        // Phase 1 premise: a uniform update can only apply across cards that share a
        // board (lane/size/label are all board-scoped). When any board-scoped field
        // is set, every card must be on a single common board.
        var distinctBoardIds = cards!.Select(c => c.BoardId).Distinct().ToList();
        if ((hasLane || hasSize || hasLabels) && distinctBoardIds.Count > 1)
        {
            return "Error: A uniform lane/size/label update requires all cards to be on the same board.";
        }

        var commonBoardId = distinctBoardIds[0];

        // Target lane: exists, belongs to the common board, not an archive lane.
        if (laneId.HasValue)
        {
            var targetLane = await db.Lanes.FindAsync([laneId.Value], ct);
            if (targetLane is null)
            {
                return "Error: Lane not found.";
            }

            if (targetLane.IsArchiveLane)
            {
                return "Error: Cannot move cards to an archive lane. Use bulk_archive_cards.";
            }

            if (targetLane.BoardId != commonBoardId)
            {
                return "Error: Target lane does not belong to the cards' board.";
            }
        }

        // Target size: resolve once against the common board.
        Guid? resolvedSizeId = null;
        if (hasSize)
        {
            var (sid, sizeError) = await SizeResolver.ResolveAsync(db, commonBoardId, sizeId, sizeName, ct);
            if (sizeError is not null)
            {
                return $"Error: {sizeError}";
            }

            resolvedSizeId = sid;
        }

        // Target labels: parse + validate once against the common board.
        List<Guid>? desiredLabelIds = null;
        if (hasLabels)
        {
            var (parsed, labelError) = await McpLabelParsing.ParseAndValidateLabelIdsAsync(db, labelIds, commonBoardId, ct);
            if (labelError is not null)
            {
                return labelError;
            }

            desiredLabelIds = parsed;
        }

        // card.moved fires per actually-moved card. The bulk SSE side coalesces to
        // ONE board-bell per board (BulkExecution's existing contract — the safety
        // property), but the webhook projection must see N distinct card.moved events, so
        // they are built + enqueued in the after-save hook for the cards that succeeded —
        // never via broadcaster.Publish, which would ring N SSE bells and break the
        // one-bell coalesce.
        //
        // Which positions count: the batch saves once, so the only placements anyone
        // outside this call can ever observe are the ones before it ran and the ones after.
        // So "from" is the card's placement snapshotted here, before any card moves, and "to"
        // is its saved placement. A card moved iff those differ — a different lane, or a
        // different position in the same lane — which also makes a batch that leaves every
        // card where it was silent. Only cards in the batch report. A lane neighbour
        // renumbered by the batch does not, as with the single-card reorder.
        var startingPlacements = new Dictionary<Guid, (Guid LaneId, int Position)>();
        if (laneId.HasValue)
        {
            foreach (var card in cards!)
            {
                startingPlacements.TryAdd(card.Id, (card.LaneId, card.Position));
            }
        }

        // card.updated fires per card whose SIZE actually changed (size is a content axis);
        // card.labeled / card.unlabeled fire per actual add/remove. Both captured
        // per-card here and emitted in the after-save hook, mirroring the move coalesce — N
        // webhook events, one SSE bell per board.
        var sizeChangedCardIds = new HashSet<Guid>();
        var labelChangesByCard = new Dictionary<Guid, (List<Guid> Added, List<Guid> Removed)>();
        Lane? targetLaneForMove = null;
        if (laneId.HasValue)
        {
            targetLaneForMove = await db.Lanes.FindAsync([laneId.Value], ct);
        }

        var now = DateTimeOffset.UtcNow;
        var execution = new BulkExecution(db, broadcaster, user!.Id, now);

        await execution.RunAsync(cards!, async card =>
        {
            if (await ArchiveGuard.IsCardArchivedAsync(db, card.Id))
            {
                return "Archived cards cannot be edited. Restore the card first.";
            }

            if (resolvedSizeId.HasValue)
            {
                if (card.SizeId != resolvedSizeId.Value)
                {
                    sizeChangedCardIds.Add(card.Id);
                }

                card.SizeId = resolvedSizeId.Value;
            }

            if (desiredLabelIds is not null)
            {
                var (added, removed) = await ApplyLabelSetAsync(card.Id, desiredLabelIds, ct);
                if (added.Count > 0 || removed.Count > 0)
                {
                    labelChangesByCard[card.Id] = (added, removed);
                }
            }

            return null;
        });

        return await execution.SaveAndSerializeAsync
        (
            ct,
            beforeSave: succeededCards => laneId.HasValue
                ? CardReorderHelper.MoveCardsToLaneAsync(db, succeededCards, laneId.Value, index, ct)
                : Task.CompletedTask,
            afterSave: async succeededCards =>
            {
                // Resolve every label changed across the batch in one query for the
                // card.labeled / card.unlabeled payloads.
                var changedLabelIds = labelChangesByCard.Values
                    .SelectMany(change => change.Added.Concat(change.Removed))
                    .Distinct()
                        .ToList();
                Dictionary<Guid, Label> labelsById = [];
                if (changedLabelIds.Count > 0)
                {
                    labelsById = await db.Labels
                        .Where(l => changedLabelIds.Contains(l.Id))
                            .ToDictionaryAsync(l => l.Id, ct);
                }

                foreach (var card in succeededCards)
                {
                    if (targetLaneForMove is not null
                        && startingPlacements.TryGetValue(card.Id, out var start)
                        && (start.LaneId != card.LaneId || start.Position != card.Position))
                    {
                        var fromLane = await db.Lanes.FindAsync([start.LaneId], ct);
                        if (fromLane is not null)
                        {
                            webhookSink.Enqueue(await WebhookEventFactory.BuildCardMovedAsync(db, card, fromLane, start.Position, targetLaneForMove, user, ct));
                        }
                    }

                    if (sizeChangedCardIds.Contains(card.Id))
                    {
                        webhookSink.Enqueue(await WebhookEventFactory.BuildCardUpdatedAsync(db, card, user, ct));
                    }

                    if (!labelChangesByCard.TryGetValue(card.Id, out var labelChange))
                    {
                        continue;
                    }

                    foreach (var labelId in labelChange.Added)
                    {
                        if (labelsById.TryGetValue(labelId, out var label))
                        {
                            webhookSink.Enqueue(await WebhookEventFactory.BuildCardLabeledAsync(db, card, label, user, ct));
                        }
                    }

                    foreach (var labelId in labelChange.Removed)
                    {
                        if (labelsById.TryGetValue(labelId, out var label))
                        {
                            webhookSink.Enqueue(await WebhookEventFactory.BuildCardUnlabeledAsync(db, card, label, user, ct));
                        }
                    }
                }
            }
        );
    }

    // Returns the actually-added and actually-removed label ids so the after-save hook can
    // emit card.labeled / card.unlabeled per change.
    private async Task<(List<Guid> Added, List<Guid> Removed)> ApplyLabelSetAsync(Guid cardId, List<Guid> desiredLabelIds, CancellationToken ct)
    {
        var desired = desiredLabelIds.ToHashSet();
        var current = await db.CardLabels.Where(cl => cl.CardId == cardId).ToListAsync(ct);
        var currentIds = current.Select(cl => cl.LabelId).ToHashSet();

        var toRemove = current.Where(cl => !desired.Contains(cl.LabelId)).ToList();
        db.CardLabels.RemoveRange(toRemove);

        var added = desired.Where(id => !currentIds.Contains(id)).ToList();
        foreach (var labelId in added)
        {
            db.CardLabels.Add(new CardLabel { CardId = cardId, LabelId = labelId });
        }

        return (added, [.. toRemove.Select(cl => cl.LabelId)]);
    }
}

// The per-card execution engine shared by all three bulk tools. Owns the Phase-2
// contract: iterate in stable order, capture per-card ok/error without aborting,
// stamp the actor/timestamp on cards that mutated, then a SINGLE SaveChangesAsync
// and ONE deduplicated broadcast per affected board. A SaveChanges throw collapses
// the whole batch to a single error string (the only place "all-or-nothing"
// genuinely applies — at the persistence layer).
file sealed class BulkExecution(BoardDbContext db, BoardEventBroadcaster broadcaster, Guid userId, DateTimeOffset now)
{
    private readonly List<BulkCardResult> _results = [];
    private readonly HashSet<Guid> _affectedBoardIds = [];
    private readonly List<CardItem> _succeededCards = [];

    public async Task RunAsync(List<CardItem> cards, Func<CardItem, Task<string?>> operation)
    {
        foreach (var card in cards)
        {
            try
            {
                var perCardError = await operation(card);
                if (perCardError is not null)
                {
                    _results.Add(new BulkCardResult(card.Id, card.Number, "error", perCardError));
                    continue;
                }

                card.LastUpdatedAtUtc = now;
                card.LastUpdatedByUserId = userId;
                _affectedBoardIds.Add(card.BoardId);
                _succeededCards.Add(card);
                _results.Add(new BulkCardResult(card.Id, card.Number, "ok", null));
            }
#pragma warning disable CA1031 // Per-card best-effort: one card's failure must not abort the batch; the error is captured in the envelope.
            catch (Exception ex)
            {
                _results.Add(new BulkCardResult(card.Id, card.Number, "error", ex.Message));
            }
#pragma warning restore CA1031
        }
    }

    // beforeSave (optional) places the succeeded cards in their lane, all at once. Lane order
    // comes from the database, so a per-card move inside the loop would place every card after
    // the first against the order from before the batch. It shares the save's failure handling:
    // if it throws, nothing is saved.
    //
    // afterSave (optional) runs only after a successful save, with the cards that
    // succeeded — the hook bulk_update_cards uses to enqueue one card.moved per
    // actually-moved card to the webhook sink, without disturbing the single
    // SSE-bell-per-board coalesce above. It is not reached on a save failure.
    public async Task<string> SaveAndSerializeAsync
    (
        CancellationToken ct,
        Func<IReadOnlyList<CardItem>, Task>? beforeSave = null,
        Func<IReadOnlyList<CardItem>, Task>? afterSave = null
    )
    {
        try
        {
            if (beforeSave is not null)
            {
                await beforeSave(_succeededCards);
            }

            await db.SaveChangesAsync(ct);
        }
#pragma warning disable CA1031 // A SaveChanges failure collapses the whole batch — report it as a single error string.
        catch (Exception ex)
        {
            return $"Error: Failed to persist bulk operation; no changes were saved. {ex.Message}";
        }
#pragma warning restore CA1031

        foreach (var boardId in _affectedBoardIds)
        {
            broadcaster.PublishBoardUpdated(boardId);
        }

        if (afterSave is not null)
        {
            await afterSave(_succeededCards);
        }

        var succeeded = _results.Count(r => r.Status == "ok");
        var envelope = new BulkResultEnvelope(_results.Count, succeeded, _results.Count - succeeded, _results);
        return JsonSerializer.Serialize(envelope, JsonSerializerOptions.Web);
    }
}

// Error is omitted on "ok" results so the envelope matches the documented shape
// ({ cardId, number, status } for ok; + error for non-ok).
file record BulkCardResult
(
    Guid CardId,
    long Number,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error
);

file record BulkResultEnvelope(int TotalRequested, int Succeeded, int Failed, List<BulkCardResult> Results);
