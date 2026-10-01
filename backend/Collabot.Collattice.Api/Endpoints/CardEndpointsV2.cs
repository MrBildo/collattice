namespace Collabot.Collattice.Api.Endpoints;

// Sparse versioning: v2 exists ONLY for this one resource — GET /api/v2/cards/{id} — because the card
// detail is the only endpoint whose shape changed in a breaking way (comments array → paged
// sub-envelope). This surface carries the paged comment envelope and the field-projection levers;
// v1 GET /cards/{id} keeps the v2.0.2 plain-array shape and is deprecated in favour of this one.
// There is no full-surface v2 alias and no other v2 route — every other endpoint stays v1.
internal static class CardEndpointsV2
{
    public static RouteGroupBuilder MapCardV2Endpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/cards/{id:guid}", async (BoardDbContext db, Guid id, bool? includeDescription, int? commentsOffset, int? commentsLimit, CancellationToken ct) =>
        {
            var card = await db.Cards.FindAsync([id], ct);
            if (card is null)
            {
                return Results.NotFound();
            }

            // Comments are the paged sub-envelope: an omitted limit returns the whole thread (a browser
            // client is not paying MCP's token cost), a given limit clamps rather than errors, and
            // commentsLimit = 0 is the count-only read. Field projection defaults to the full card —
            // includeDescription is the heavy-field opt-out.
            var effectiveCommentsOffset = Math.Max(commentsOffset ?? 0, 0);
            int? effectiveCommentsLimit = commentsLimit switch
            {
                null => null,
                0 => 0,
                _ => Math.Clamp(commentsLimit.Value, 1, 200),
            };

            var detail = await CardDetailBuilder.BuildAsync(db, card, includeDescription ?? true, effectiveCommentsOffset, effectiveCommentsLimit, ct);
            return Results.Ok(detail);
        }).RequireAuth();

        return group;
    }
}
