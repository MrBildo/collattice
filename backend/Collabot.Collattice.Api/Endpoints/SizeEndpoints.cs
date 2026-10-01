using Collabot.Collattice.Api.Auth;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

internal static class SizeEndpoints
{
    public static RouteGroupBuilder MapSizeEndpoints(this RouteGroupBuilder group)
    {
        // Board-scoped listing and creation
        group.MapGet("/boards/{boardId:guid}/sizes", async (BoardDbContext db, Guid boardId) =>
            !await db.Boards.AnyAsync(x => x.Id == boardId)
                ? Results.NotFound()
                : Results.Ok(await db.CardSizes.Where(x => x.BoardId == boardId).OrderBy(x => x.Ordinal).ToListAsync()))
            .RequireAuth();

        group.MapPost("/boards/{boardId:guid}/sizes", async (BoardDbContext db, HttpContext http, Guid boardId, CreateSizeRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            if (!await db.Boards.AnyAsync(x => x.Id == boardId, ct))
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest("Name is required.");
            }

            var created = await SizeCreateHelper.CreateAsync(db, boardId, request.Name, request.Ordinal, ct);
            if (created.Size is not CardSize size)
            {
                return created.IsConflict
                    ? Results.Conflict(created.Error)
                    : Results.BadRequest(created.Error);
            }

            // size.created — same single board bell, plus one webhook event.
            await WebhookEventFactory.PublishSizeCreatedAsync(db, broadcaster, size, http.CurrentUser(), ct);
            return Results.Created($"/api/v1/sizes/{size.Id}", size);
        }).RequireAdminOrAgentAdmin();

        // Whole-board size reorder. Client sends the complete desired
        // order of the board's sizes; server owns all ordinal math (two-phase
        // renumber under the unique (BoardId, Ordinal) index — see
        // SizeReorderHelper). Mirrors the lane reorder.
        group.MapPost("/boards/{boardId:guid}/sizes/reorder", async (BoardDbContext db, HttpContext http, Guid boardId, ReorderSizesRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            if (!await db.Boards.AnyAsync(x => x.Id == boardId, ct))
            {
                return Results.NotFound();
            }

            var (sizes, error) = await SizeReorderHelper.ValidateAsync(db, boardId, request.SizeIds, ct);
            if (error is not null)
            {
                return Results.BadRequest(error);
            }

            var ordered = await SizeReorderHelper.ReorderAsync(db, sizes!, request.SizeIds!, ct);

            // size.reordered — ONE event carrying the board's full new order (never N), same single
            // board bell the reorder always rang.
            await WebhookEventFactory.PublishSizeReorderedAsync(db, broadcaster, boardId, http.CurrentUser(), ct);
            return Results.Ok(ordered);
        }).RequireAdminOrAgentAdmin();

        // By-ID operations (flat)
        group.MapGet("/sizes/{id:guid}", async (BoardDbContext db, Guid id) =>
        {
            var size = await db.CardSizes.FindAsync(id);
            return size is null ? Results.NotFound() : Results.Ok(size);
        }).RequireAuth();

        group.MapDelete("/sizes/{id:guid}", async (BoardDbContext db, HttpContext http, Guid id, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var size = await db.CardSizes.FindAsync([id], ct);
            if (size is null)
            {
                return Results.NotFound();
            }

            if (await db.Cards.AnyAsync(x => x.SizeId == id, ct))
            {
                return Results.Conflict("Size is in use by cards.");
            }

            db.CardSizes.Remove(size);
            await db.SaveChangesAsync(ct);

            // size.deleted — published from the captured size after the row is gone; the board still
            // exists, so the slug resolves.
            await WebhookEventFactory.PublishSizeDeletedAsync(db, broadcaster, size, http.CurrentUser(), ct);
            return Results.NoContent();
        }).RequireAdminOrAgentAdmin();

        group.MapPatch("/sizes/{id:guid}", async (BoardDbContext db, HttpContext http, Guid id, UpdateSizeRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var size = await db.CardSizes.FindAsync([id], ct);
            if (size is null)
            {
                return Results.NotFound();
            }

            // Capture the pre-mutation values for the per-axis no-op guard.
            var oldName = size.Name;
            var oldOrdinal = size.Ordinal;

            if (request.Name is not null)
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest("Name cannot be empty.");
                }

                size.Name = request.Name;
            }

            if (request.Ordinal is not null)
            {
                var newOrd = request.Ordinal.Value;
                if (await db.CardSizes.AnyAsync(x => x.BoardId == size.BoardId && x.Ordinal == newOrd && x.Id != id, ct))
                {
                    return Results.Conflict(SizeCreateHelper.TakenMessage);
                }

                size.Ordinal = newOrd;
            }

            await db.SaveChangesAsync(ct);

            // Split by axis: a name change → size.renamed; an ordinal change → size.reordered
            // (the board's full new order). Both can co-fire from one PATCH; PublishCoalesced rings
            // EXACTLY ONE SSE bell (byte-for-byte unchanged) and enqueues one webhook per changed axis.
            var nameChanged = request.Name is not null && request.Name != oldName;
            var ordinalChanged = request.Ordinal is not null && request.Ordinal.Value != oldOrdinal;

            var events = await WebhookEventFactory.BuildSizeUpdateEventsAsync(db, size, http.CurrentUser(), nameChanged, ordinalChanged, ct);
            broadcaster.PublishCoalesced(size.BoardId, events);
            return Results.Ok(size);
        }).RequireAdminOrAgentAdmin();

        return group;
    }
}
