namespace Collabot.Collattice.Api.Endpoints;

internal static class SearchEndpoints
{
    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/search/cards", async (BoardDbContext db, string? q, int? limit, Guid? archiveBoardId, Guid? boardId, CancellationToken ct) =>
        {
            var effectiveLimit = Math.Clamp(limit ?? 20, 1, 50);
            var results = await SearchHelper.SearchCardsAsync(db, q, effectiveLimit, archiveBoardId, boardId, ct);
            return Results.Ok(results);
        }).RequireAuth();

        return group;
    }
}
