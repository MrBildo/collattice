using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// Shared renumber for whole-board size reordering, wrapped by the
// REST endpoint (POST /boards/{boardId}/sizes/reorder). Mirrors the lane
// reorder (LaneReorderHelper): the client states intent (the complete
// desired order of the board's sizes); the server owns all ordinal math.
//
// The hard part is the DB-level unique index on (BoardId, Ordinal)
// (BoardDbContext.cs). SQLite checks a unique index after every row UPDATE —
// even inside a transaction (it is not a DEFERRABLE constraint) — so a plain
// ordinal-rewrite that swaps two sizes hits a transient duplicate and is
// rejected at SaveChanges. We defeat that with a two-phase renumber inside one
// explicit transaction: phase 1 parks every size in a disjoint negative band
// that cannot collide with any live ordinal (all ordinals are >= 0) and saves,
// then phase 2 assigns the final dense 0..n-1 and saves. The transaction makes
// the pair atomic so the board is never observably half-renumbered.
//
// Unlike lanes there is no archive sentinel — every size is reorderable — so
// the validation here is the plain set-equality check with no exclusion.
internal static class SizeReorderHelper
{
    // Pre-validate the requested order against the board's current size set
    // (all-or-nothing). On failure returns a single error message and mutates
    // nothing. On success returns the loaded sizes (so the caller can avoid a
    // second query). The reordered sizes — for serialization back to the
    // client — are produced by ReorderAsync.
    public static async Task<(List<CardSize>? Sizes, string? Error)> ValidateAsync
    (
        BoardDbContext db,
        Guid boardId,
        Guid[]? requestedOrder,
        CancellationToken ct = default
    )
    {
        if (requestedOrder is null || requestedOrder.Length == 0)
        {
            return (null, "sizeIds is required.");
        }

        var requestedSet = requestedOrder.ToHashSet();
        if (requestedSet.Count != requestedOrder.Length)
        {
            return (null, "sizeIds contains duplicate ids.");
        }

        var sizeIds = await db.CardSizes
            .Where(s => s.BoardId == boardId)
                .Select(s => s.Id)
                    .ToListAsync(ct);

        if (sizeIds.Count != requestedOrder.Length || !requestedSet.SetEquals(sizeIds))
        {
            return (null, "sizeIds must be exactly the board's current sizes (no missing, extra, or unknown ids).");
        }

        var sizes = await db.CardSizes
            .Where(s => s.BoardId == boardId)
                .ToListAsync(ct);

        return (sizes, null);
    }

    // Two-phase renumber inside one transaction. `sizes` is the loaded size set
    // (from ValidateAsync); `order` is the validated desired order. Returns the
    // sizes in final order.
    public static async Task<List<CardSize>> ReorderAsync
    (
        BoardDbContext db,
        List<CardSize> sizes,
        Guid[] order,
        CancellationToken ct = default
    )
    {
        var byId = sizes.ToDictionary(s => s.Id);
        var ordered = order.Select(id => byId[id]).ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Phase 1: park every size in a disjoint negative band so no two share a
        // value and none collides with a live ordinal (>= 0).
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Ordinal = -(i + 1);
        }

        await db.SaveChangesAsync(ct);

        // Phase 2: assign the final dense 0..n-1. Every prior value was vacated
        // in phase 1, so no UPDATE in this batch can transiently collide.
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Ordinal = i;
        }

        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);

        return ordered;
    }
}
