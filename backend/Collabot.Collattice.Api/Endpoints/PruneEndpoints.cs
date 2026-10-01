using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

internal static class PruneEndpoints
{
    private static readonly string[] _validActions = ["archive", "delete"];

    public static RouteGroupBuilder MapPruneEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/boards/{boardId:guid}/prune/preview", async (BoardDbContext db, Guid boardId, PruneRequest request) =>
        {
            if (!await db.Boards.AnyAsync(x => x.Id == boardId))
            {
                return Results.NotFound();
            }

            if (!PruneFilter.ValidateFilters(request, out var error))
            {
                return Results.BadRequest(error);
            }

            var query = PruneFilter.BuildFilteredQuery(db, boardId, request);
            var cards = await query.ToListAsync();

            // Batch load lane names
            var laneIdSet = cards.Select(c => c.LaneId).Distinct().ToList();
            var laneNames = await db.Lanes
                .Where(l => laneIdSet.Contains(l.Id))
                    .ToDictionaryAsync(l => l.Id, l => l.Name);

            var cardSummaries = cards
                .Select(c => new
                {
                    c.Id,
                    c.Number,
                    c.Name,
                    laneName = laneNames.GetValueOrDefault(c.LaneId, "?"),
                    c.LastUpdatedAtUtc,
                })
                    .ToList();

            return Results.Ok(new { matchCount = cards.Count, cards = cardSummaries });
        }).RequireAdminOrAgentAdmin();

        group.MapPost("/boards/{boardId:guid}/prune", async (HttpContext http, BoardDbContext db, Guid boardId, PruneRequest request, BoardEventBroadcaster broadcaster, IWebhookSink webhookSink, CancellationToken ct) =>
        {
            if (!await db.Boards.AnyAsync(x => x.Id == boardId, ct))
            {
                return Results.NotFound();
            }

            if (!PruneFilter.ValidateFilters(request, out var error))
            {
                return Results.BadRequest(error);
            }

            if (!ValidateAction(request.Action, out var actionError))
            {
                return Results.BadRequest(actionError);
            }

            var action = string.IsNullOrEmpty(request.Action) ? "archive" : request.Action;

            // AgentAdministrator is blocked from the destructive delete action.
            // Bulk delete is deliberately excluded from the widened roles; only Administrator may invoke it.
            if (action == "delete" && http.CurrentUser().Role == UserRole.AgentAdministrator)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var query = PruneFilter.BuildFilteredQuery(db, boardId, request);

            if (action == "archive")
            {
                var (archivedCards, archiveError) = await PruneArchiveHelper.ArchiveMatchedAsync(db, boardId, query, ct);
                if (archiveError is not null)
                {
                    return Results.BadRequest(archiveError);
                }

                // card.archived per pruned card — N webhook events, one SSE bell.
                foreach (var archived in await WebhookEventFactory.BuildCardArchivedBatchAsync(db, archivedCards, http.CurrentUser(), ct))
                {
                    webhookSink.Enqueue(archived);
                }

                broadcaster.PublishBoardUpdated(boardId);

                return Results.Ok(new { archivedCount = archivedCards.Count });
            }
            else
            {
                var cards = await query.ToListAsync(ct);

                // card.deleted per pruned card — built BEFORE RemoveRange (the fat summary enriches
                // by querying the card ids, so building after the delete would blank them). N webhook
                // events, one SSE bell — mirroring the prune-archive path above.
                var deletedEvents = await WebhookEventFactory.BuildCardDeletedBatchAsync(db, cards, http.CurrentUser(), ct);

                db.Cards.RemoveRange(cards);
                await db.SaveChangesAsync(ct);

                foreach (var deleted in deletedEvents)
                {
                    webhookSink.Enqueue(deleted);
                }

                broadcaster.PublishBoardUpdated(boardId);

                return Results.Ok(new { deletedCount = cards.Count });
            }
        }).RequireAdminOrAgentAdmin();

        return group;
    }

    private static bool ValidateAction(string? action, out string? error)
    {
        error = null;

        if (string.IsNullOrEmpty(action))
        {
            return true;
        }

        if (!_validActions.Contains(action))
        {
            error = $"Invalid action '{action}'. Valid actions are: archive, delete.";
            return false;
        }

        return true;
    }
}
