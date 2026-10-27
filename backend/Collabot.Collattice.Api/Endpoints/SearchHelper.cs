using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

internal static class SearchHelper
{
    public static IQueryable<CardItem> ApplySearchFilter(IQueryable<CardItem> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var term = search.Trim();

        // exact card number lookup
        if (term.StartsWith('#') && long.TryParse(term[1..], CultureInfo.InvariantCulture, out var cardNumber))
        {
            return query.Where(c => c.Number == cardNumber);
        }

        // Plain number — match card number OR name/description
        if (long.TryParse(term, CultureInfo.InvariantCulture, out var num))
        {
            var pattern = $"%{EscapeLike(term)}%";
            return query.Where(c =>
                c.Number == num
                || EF.Functions.Like(c.Name, pattern, "\\")
                || EF.Functions.Like(c.DescriptionMarkdown, pattern, "\\"));
        }

        // Free-text — match name or description
        var likePattern = $"%{EscapeLike(term)}%";
        return query.Where(c =>
            EF.Functions.Like(c.Name, likePattern, "\\")
            || EF.Functions.Like(c.DescriptionMarkdown, likePattern, "\\"));
    }

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);

    // Cross-board card search shared by the REST GET /search/cards endpoint and the
    // MCP search_cards tool. Both surfaces must return the identical
    // board-grouped CardSummary shape — keeping the logic here is the same
    // anti-drift discipline that routes both surfaces through CardSummaryBuilder.
    // The boardId param only affects result ordering (priority board first); it does
    // not scope the query — that's what makes this search cross-board.
    public static async Task<List<SearchResult>> SearchCardsAsync
    (
        BoardDbContext db,
        string? q,
        int limit,
        Guid? archiveBoardId,
        Guid? boardId,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return [];
        }

        // Load all archive lane IDs upfront (spans all boards)
        var allArchiveLanes = await db.Lanes
            .Where(l => l.IsArchiveLane)
                .Select(l => new { l.Id, l.BoardId })
                    .ToListAsync(ct);

        // Exclude archive lanes from all boards except the archiveBoardId
        var excludeArchiveLaneIds = allArchiveLanes
            .Where(l => l.BoardId != archiveBoardId)
                .Select(l => l.Id)
                    .ToList();

        // The priority board's own archive lanes — used to keep archived current-board
        // cards out of the top priority bucket so they can't consume the limit budget
        // ahead of non-archived matches.
        var priorityArchiveLaneIds = boardId is null
            ? []
            : allArchiveLanes
                .Where(l => l.BoardId == boardId)
                    .Select(l => l.Id)
                        .ToHashSet();

        var query = db.Cards.Where(c => !c.IsTemp);
        query = ApplySearchFilter(query, q);

        if (excludeArchiveLaneIds.Count > 0)
        {
            query = query.Where(c => !excludeArchiveLaneIds.Contains(c.LaneId));
        }

        // Prioritize BEFORE the cut so the limit budget goes to the current board's
        // non-archived matches first. Without this, the Take ran against a
        // board-GUID-sorted list and could drop current-board matches before the
        // priority reorder ever saw them. The OrderBy sorts the current board's
        // non-archived cards into the first bucket and everything else — other boards
        // plus the current board's archived cards — into the second.
        var cards = await query
            .OrderBy(c => boardId != null && c.BoardId == boardId && !priorityArchiveLaneIds.Contains(c.LaneId) ? 0 : 1)
            .ThenBy(c => c.BoardId)
            .ThenByDescending(c => c.Number)
            .Take(limit)
                .ToListAsync(ct);

        if (cards.Count == 0)
        {
            return [];
        }

        var boardIds = cards.Select(c => c.BoardId).Distinct().ToList();

        var boards = await db.Boards
            .Where(b => boardIds.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, b => b, ct);

        var cardBoardMap = cards.ToDictionary(c => c.Id, c => c.BoardId);

        // One shared projection across surfaces — the builder owns sizes, labels,
        // counts, archive flag, and the latest-comment enrichment. Keeping
        // search on the builder avoids a second CardSummary projection drifting.
        var summaries = await CardSummaryBuilder.BuildAsync(db, cards, ct);

        // Group by board; current board (if specified) ranks first
        return
        [
            .. summaries
                .GroupBy(s => cardBoardMap[s.Id])
                .Where(g => boards.ContainsKey(g.Key))
                    .Select(g =>
                    {
                        var board = boards[g.Key];
                        return new SearchResult(board.Id, board.Name, board.Slug, [.. g]);
                    })
                    .OrderBy(r => r.BoardId == boardId ? 0 : 1)
        ];
    }
}
