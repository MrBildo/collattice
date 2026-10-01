namespace Collabot.Collattice.Api.Endpoints;

internal static class CardQueryHelper
{
    // Board-scoped card query shared by the paginated /cards endpoint and the composite
    // /board endpoint. Applies the board scope, the temp-card exclusion, and —
    // unless includeArchived is true — the archive-lane exclusion. Callers layer their
    // own optional filters (lane, since, label, search) and ordering/pagination on top.
    public static IQueryable<CardItem> BoardCards
    (
        IQueryable<CardItem> cards,
        IQueryable<Lane> lanes,
        Guid boardId,
        bool includeArchived
    )
    {
        var query = cards.Where(x => x.BoardId == boardId && !x.IsTemp);

        if (!includeArchived)
        {
            var archiveLaneIds = lanes
                .Where(l => l.BoardId == boardId && l.IsArchiveLane)
                    .Select(l => l.Id);
            query = query.Where(x => !archiveLaneIds.Contains(x.LaneId));
        }

        return query;
    }

    // Canonical card ordering: grouped by lane, then by intra-lane position. Shared so the
    // paginated and composite paths return cards in the same order.
    public static IOrderedQueryable<CardItem> OrderForBoard(IQueryable<CardItem> query) =>
        query.OrderBy(x => x.LaneId).ThenBy(x => x.Position);

    // "recent activity" = the card itself changed, OR it gained a
    // recent comment/attachment. The comment/attachment clauses are
    // correlated EXISTS sub-queries. These translate to SQL only because
    // the DateTimeOffset columns are stored as a sortable normalized-UTC
    // ISO-8601 string via a value converter (see
    // BoardDbContext.OnModelCreating) — SQLite cannot translate a
    // DateTimeOffset comparison in a nested query position without it. The
    // whole filter runs server-side; nothing is materialized client-side.
    public static IQueryable<CardItem> ApplySinceFilter
    (
        IQueryable<CardItem> query,
        BoardDbContext db,
        DateTimeOffset since
    ) =>
        query.Where(x =>
            x.CreatedAtUtc >= since
            || x.LastUpdatedAtUtc >= since
            || db.Comments.Any(c => c.CardId == x.Id && c.LastUpdatedAtUtc >= since)
            || db.Attachments.Any(a => a.CardId == x.Id && a.AddedAtUtc >= since));
}
