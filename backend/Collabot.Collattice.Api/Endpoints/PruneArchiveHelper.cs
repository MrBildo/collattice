using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// Shared prune archive-loop orchestration for the REST PruneEndpoints and the MCP
// PruneTools. PruneFilter already shares the match query; the mutation loop that
// archives the matched cards (archive-lane lookup -> MoveCardsToLaneAsync -> save) was
// duplicated across both surfaces. Extracted on the PruneFilter
// precedent so the archive mutation cannot drift either.
//
// Archive only — the REST delete branch is REST-only by design and
// stays inline at its call site. The caller owns the post-save broadcast.
internal static class PruneArchiveHelper
{
    // Returns the cards it archived (not just a count) so the caller can emit one
    // card.archived webhook event per card while ringing a single SSE bell (prune
    // is a card.archived emit surface). The count is the list's Count.
    public static async Task<(IReadOnlyList<CardItem> ArchivedCards, string? Error)> ArchiveMatchedAsync
    (
        BoardDbContext db,
        Guid boardId,
        IQueryable<CardItem> filtered,
        CancellationToken ct
    )
    {
        var archiveLane = await db.Lanes.FirstOrDefaultAsync(l => l.BoardId == boardId && l.IsArchiveLane, ct);
        if (archiveLane is null)
        {
            return ([], "Board has no archive lane.");
        }

        var cards = await filtered.ToListAsync(ct);

        await CardReorderHelper.MoveCardsToLaneAsync(db, cards, archiveLane.Id, 0, ct);

        await db.SaveChangesAsync(ct);
        return (cards, null);
    }
}
