namespace Collabot.Collattice.Api.Endpoints;

// Shared prune-filter logic for the REST PruneEndpoints and the MCP PruneTools.
// Extracted out of PruneEndpoints so both surfaces build
// the filtered card query from a single source — REST/MCP drift is the top bug
// class on this codebase, and prune's match semantics (archive exclusion,
// olderThan DateTimeOffset comparison, lane/label filters) must stay identical
// across both entry points.
internal static class PruneFilter
{
    public const string NoFilterError = "At least one filter is required (olderThan, laneIds, or labelIds).";

    public static bool ValidateFilters(PruneRequest request, out string? error)
    {
        error = null;

        var hasAnyFilter = request.OlderThan is not null
            || (request.LaneIds is not null && request.LaneIds.Length > 0)
            || (request.LabelIds is not null && request.LabelIds.Length > 0);

        if (!hasAnyFilter)
        {
            error = NoFilterError;
            return false;
        }

        return true;
    }

    public static IQueryable<CardItem> BuildFilteredQuery
    (
        BoardDbContext db,
        Guid boardId,
        PruneRequest request
    )
    {
        // CardQueryHelper.BoardCards applies board scope, temp-card exclusion, and —
        // when includeArchived is false — the archive-lane exclusion via a correlated
        // sub-query (fully server-side, same converter path as the `since` filter).
        var query = CardQueryHelper.BoardCards
        (
            db.Cards,
            db.Lanes,
            boardId,
            includeArchived: request.IncludeArchived is true
        );

        if (request.OlderThan.HasValue)
        {
            // The model-wide DateTimeOffset value converter stores every
            // DateTimeOffset column as a normalized-UTC ISO-8601 string, so this
            // LINQ comparison translates natively to a TEXT < TEXT comparison in
            // SQLite. The converter calls .ToUniversalTime() on write, so a
            // non-UTC olderThan value is normalised before comparison — fixing a
            // latent bug the prior FromSqlInterpolated workaround had (it called
            // .ToString("O") without .ToUniversalTime()).
            var cutoff = request.OlderThan.Value;
            query = query.Where(c => c.LastUpdatedAtUtc < cutoff);
        }

        if (request.LaneIds is not null && request.LaneIds.Length > 0)
        {
            var laneIds = request.LaneIds.ToList();
            query = query.Where(c => laneIds.Contains(c.LaneId));
        }

        if (request.LabelIds is not null && request.LabelIds.Length > 0)
        {
            var labelIds = request.LabelIds.ToList();
            var cardIdsWithLabels = db.CardLabels
                .Where(cl => labelIds.Contains(cl.LabelId))
                    .Select(cl => cl.CardId);

            query = query.Where(c => cardIdsWithLabels.Contains(c.Id));
        }

        return query;
    }
}
