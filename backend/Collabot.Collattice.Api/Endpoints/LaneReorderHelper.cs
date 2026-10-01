using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// Shared renumber for whole-board lane reordering, wrapped by both
// the REST endpoint (POST /boards/{boardId}/lanes/reorder) and the MCP tool
// (reorder_lanes). The client states intent (the complete left-to-right order
// of the board's non-archive lanes); the server owns all position math.
//
// The hard part is the DB-level unique index on (BoardId, Position)
// (BoardDbContext.cs). SQLite checks a unique index after every row UPDATE —
// even inside a transaction (it is not a DEFERRABLE constraint) — so a plain
// position-rewrite that swaps two lanes hits a transient duplicate and is
// rejected at SaveChanges. We defeat that with a two-phase renumber inside one
// explicit transaction: phase 1 parks every affected lane in a disjoint
// negative band (-1, -2, …) that cannot collide with any live position (>= 0)
// or the archive lane (int.MaxValue) and saves; phase 2 assigns the final
// dense 0..n-1 and saves. The transaction makes the pair atomic so the board
// is never observably half-renumbered.
internal static class LaneReorderHelper
{
    // Pre-validate the requested order against the board's current non-archive
    // lane set (all-or-nothing). On failure returns a single error message and
    // mutates nothing. On success returns the loaded non-archive lanes (so the
    // caller can avoid a second query). The reordered lanes — for serialization
    // back to the client — are produced by ReorderAsync.
    public static async Task<(List<Lane>? Lanes, string? Error)> ValidateAsync
    (
        BoardDbContext db,
        Guid boardId,
        Guid[]? requestedOrder,
        CancellationToken ct = default
    )
    {
        if (requestedOrder is null || requestedOrder.Length == 0)
        {
            return (null, "laneIds is required.");
        }

        var requestedSet = requestedOrder.ToHashSet();
        if (requestedSet.Count != requestedOrder.Length)
        {
            return (null, "laneIds contains duplicate ids.");
        }

        // Defense in depth: the archive lane is excluded from get_lanes/fetchLanes
        // so it should never appear in a client's order, but reject it explicitly
        // rather than silently renumber around it.
        if (await db.Lanes.AnyAsync(l => requestedOrder.Contains(l.Id) && l.IsArchiveLane, ct))
        {
            return (null, "Archive lanes cannot be reordered.");
        }

        var laneIds = await db.Lanes
            .Where(l => l.BoardId == boardId && !l.IsArchiveLane)
                .Select(l => l.Id)
                    .ToListAsync(ct);

        if (laneIds.Count != requestedOrder.Length || !requestedSet.SetEquals(laneIds))
        {
            return (null, "laneIds must be exactly the board's current non-archive lanes (no missing, extra, or unknown ids).");
        }

        var lanes = await db.Lanes
            .Where(l => l.BoardId == boardId && !l.IsArchiveLane)
                .ToListAsync(ct);

        return (lanes, null);
    }

    // Two-phase renumber inside one transaction. `lanes` is the loaded
    // non-archive lane set (from ValidateAsync); `order` is the validated
    // desired left-to-right order. Returns the lanes in final order.
    public static async Task<List<Lane>> ReorderAsync
    (
        BoardDbContext db,
        List<Lane> lanes,
        Guid[] order,
        CancellationToken ct = default
    )
    {
        var byId = lanes.ToDictionary(l => l.Id);
        var ordered = order.Select(id => byId[id]).ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Phase 1: park every lane in a disjoint negative band so no two share a
        // value and none collides with a live position (>= 0) or int.MaxValue.
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Position = -(i + 1);
        }

        await db.SaveChangesAsync(ct);

        // Phase 2: assign the final dense 0..n-1. Every prior value was vacated
        // in phase 1, so no UPDATE in this batch can transiently collide.
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Position = i;
        }

        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);

        return ordered;
    }
}
