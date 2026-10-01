using Collabot.Collattice.Api.Auth;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

internal static class LaneEndpoints
{
    public static RouteGroupBuilder MapLaneEndpoints(this RouteGroupBuilder group)
    {
        // Board-scoped listing and creation
        group.MapGet("/boards/{boardId:guid}/lanes", async (BoardDbContext db, Guid boardId) =>
            !await db.Boards.AnyAsync(x => x.Id == boardId)
                ? Results.NotFound()
                : Results.Ok(await db.Lanes.Where(x => x.BoardId == boardId && !x.IsArchiveLane).OrderBy(x => x.Position).ToListAsync()))
            .RequireAuth();

        group.MapPost("/boards/{boardId:guid}/lanes", async (BoardDbContext db, HttpContext http, Guid boardId, CreateLaneRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            if (!await db.Boards.AnyAsync(x => x.Id == boardId, ct))
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest("Name is required.");
            }

            var created = await LanePositionHelper.CreateAsync(db, boardId, request.Name, request.Position, ct);
            if (created.Lane is not Lane lane)
            {
                return created.IsConflict
                    ? Results.Conflict(created.Error)
                    : Results.BadRequest(created.Error);
            }

            // lane.created — same single board bell, plus one webhook event.
            await WebhookEventFactory.PublishLaneCreatedAsync(db, broadcaster, lane, http.CurrentUser(), ct);
            return Results.Created($"/api/v1/lanes/{lane.Id}", lane);
        }).RequireAdminOrAgentAdmin();

        // Whole-board lane reorder. Client sends the complete desired
        // left-to-right order of the board's non-archive lanes; server owns all
        // position math (two-phase renumber under the unique (BoardId, Position)
        // index — see LaneReorderHelper).
        group.MapPost("/boards/{boardId:guid}/lanes/reorder", async (BoardDbContext db, HttpContext http, Guid boardId, ReorderLanesRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            if (!await db.Boards.AnyAsync(x => x.Id == boardId, ct))
            {
                return Results.NotFound();
            }

            var (lanes, error) = await LaneReorderHelper.ValidateAsync(db, boardId, request.LaneIds, ct);
            if (error is not null)
            {
                return Results.BadRequest(error);
            }

            var ordered = await LaneReorderHelper.ReorderAsync(db, lanes!, request.LaneIds!, ct);

            // lane.reordered — ONE event carrying the board's full new order (never N), same single
            // board bell the reorder always rang.
            await WebhookEventFactory.PublishLaneReorderedAsync(db, broadcaster, boardId, http.CurrentUser(), ct);
            return Results.Ok(ordered);
        }).RequireAdminOrAgentAdmin();

        // By-ID operations (flat)
        group.MapGet("/lanes/{id:guid}", async (BoardDbContext db, Guid id) =>
        {
            var lane = await db.Lanes.FindAsync(id);
            return lane is null ? Results.NotFound() : Results.Ok(lane);
        }).RequireAuth();

        group.MapDelete("/lanes/{id:guid}", async (BoardDbContext db, HttpContext http, Guid id, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var lane = await db.Lanes.FindAsync([id], ct);
            if (lane is null)
            {
                return Results.NotFound();
            }

            if (lane.IsArchiveLane)
            {
                return Results.BadRequest("Archive lanes cannot be deleted.");
            }

            if (await db.Cards.AnyAsync(x => x.LaneId == id, ct))
            {
                return Results.Conflict("Lane must be empty.");
            }

            db.Lanes.Remove(lane);
            await db.SaveChangesAsync(ct);

            // lane.deleted — published from the captured lane after the row is gone; the board still
            // exists, so the slug resolves.
            await WebhookEventFactory.PublishLaneDeletedAsync(db, broadcaster, lane, http.CurrentUser(), ct);
            return Results.NoContent();
        }).RequireAdminOrAgentAdmin();

        group.MapPatch("/lanes/{id:guid}", async (BoardDbContext db, HttpContext http, Guid id, UpdateLaneRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var lane = await db.Lanes.FindAsync([id], ct);
            if (lane is null)
            {
                return Results.NotFound();
            }

            if (lane.IsArchiveLane)
            {
                return Results.BadRequest("Archive lanes cannot be modified.");
            }

            // Capture the pre-mutation values for the per-axis no-op guard.
            var oldName = lane.Name;
            var oldPosition = lane.Position;

            if (request.Name is not null)
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest("Name cannot be empty.");
                }

                lane.Name = request.Name;
            }

            if (request.Position is not null)
            {
                var newPos = request.Position.Value;

                if (newPos == int.MaxValue)
                {
                    return Results.BadRequest("Position value is reserved.");
                }

                if (await db.Lanes.AnyAsync(x => x.BoardId == lane.BoardId && x.Position == newPos && x.Id != id, ct))
                {
                    return Results.Conflict("Position already taken by another lane.");
                }

                lane.Position = newPos;
            }

            await db.SaveChangesAsync(ct);

            // Split by axis: a name change → lane.renamed; a position change → lane.reordered
            // (the board's full new order). Both can co-fire from one PATCH; PublishCoalesced rings
            // EXACTLY ONE SSE bell (byte-for-byte unchanged) and enqueues one webhook per changed axis.
            var nameChanged = request.Name is not null && request.Name != oldName;
            var positionChanged = request.Position is not null && request.Position.Value != oldPosition;

            var events = await WebhookEventFactory.BuildLaneUpdateEventsAsync(db, lane, http.CurrentUser(), nameChanged, positionChanged, ct);
            broadcaster.PublishCoalesced(lane.BoardId, events);
            return Results.Ok(lane);
        }).RequireAdminOrAgentAdmin();

        return group;
    }
}
