using Collabot.Collattice.Api.Auth;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

internal static class BoardEndpoints
{
    public static RouteGroupBuilder MapBoardEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/boards", async (BoardDbContext db) =>
            Results.Ok(await db.Boards.OrderBy(x => x.Name).ToListAsync()))
            .RequireAuth();

        group.MapGet("/boards/{idOrSlug}", async (BoardDbContext db, string idOrSlug) =>
        {
            var board = Guid.TryParse(idOrSlug, out var id)
                ? await db.Boards.FindAsync(id)
                : await db.Boards.SingleOrDefaultAsync(x => x.Slug == idOrSlug);

            return board is null ? Results.NotFound() : Results.Ok(board);
        }).RequireAuth();

        group.MapPost("/boards", async (BoardDbContext db, HttpContext http, IWebhookSink sink, CreateBoardRequest request, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest("Name is required.");
            }

            var name = request.Name;
            var slug = Board.GenerateSlug(name);

            if (await db.Boards.AnyAsync(x => x.Slug == slug, ct))
            {
                return Results.Conflict("A board with that slug already exists.");
            }

            var board = new Board
            {
                Id = Guid.NewGuid(),
                Name = name,
                Slug = slug,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            db.Boards.Add(board);

            BoardSeeder.Seed(db, board);

            await db.SaveChangesAsync(ct);

            // board.created — WEBHOOK-ONLY: enqueue straight to the sink, NO board bell (board CRUD
            // has no SSE broadcast, so the SSE wire stays byte-for-byte unchanged).
            WebhookEventFactory.PublishBoardCreated(sink, board, http.CurrentUser());
            return Results.Created($"/api/v1/boards/{board.Id}", board);
        }).RequireAdminOrAgentAdmin();

        group.MapPatch("/boards/{id:guid}", async (BoardDbContext db, HttpContext http, IWebhookSink sink, Guid id, UpdateBoardRequest request, CancellationToken ct) =>
        {
            var board = await db.Boards.FindAsync([id], ct);
            if (board is null)
            {
                return Results.NotFound();
            }

            var oldName = board.Name;

            if (request.Name is not null)
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest("Name cannot be empty.");
                }

                board.Name = request.Name;
            }

            await db.SaveChangesAsync(ct);

            // board.renamed — WEBHOOK-ONLY (no board bell), only on an actual name change (no-op
            // guard; the slug is immutable so a rename is the board's only mutation).
            if (request.Name is not null && request.Name != oldName)
            {
                WebhookEventFactory.PublishBoardRenamed(sink, board, http.CurrentUser());
            }

            return Results.Ok(board);
        }).RequireAdminOrAgentAdmin();

        group.MapDelete("/boards/{id:guid}", async (BoardDbContext db, HttpContext http, IWebhookSink sink, Guid id, CancellationToken ct) =>
        {
            var board = await db.Boards.FindAsync([id], ct);
            if (board is null)
            {
                return Results.NotFound();
            }

            if (await db.Lanes.AnyAsync(x => x.BoardId == id && !x.IsArchiveLane, ct))
            {
                return Results.BadRequest("Board must have no lanes before it can be deleted.");
            }

            var archivedCardsDeleted = await db.Cards.CountAsync(x => x.BoardId == id, ct);

            var deleteOutcome = await GuardedDelete.BoardAsync(db, board, ct);
            if (deleteOutcome is GuardedDeleteOutcome.InUse)
            {
                return Results.BadRequest("Board must have no lanes before it can be deleted.");
            }

            if (deleteOutcome is GuardedDeleteOutcome.NotFound)
            {
                return Results.NotFound();
            }

            // board.deleted — WEBHOOK-ONLY (no board bell), enqueued from the captured board after the
            // row is gone (state at occurrence; the event is self-contained).
            WebhookEventFactory.PublishBoardDeleted(sink, board, http.CurrentUser());
            return archivedCardsDeleted > 0 ? Results.Ok(new { deleted = true, archivedCardsDeleted }) : Results.NoContent();
        }).RequireAdmin();

        // Composite board view — lanes + cards for a specific board
        group.MapGet("/boards/{boardId:guid}/board", async (BoardDbContext db, Guid boardId) =>
        {
            if (!await db.Boards.AnyAsync(x => x.Id == boardId))
            {
                return Results.NotFound();
            }

            var lanes = await db.Lanes.Where(x => x.BoardId == boardId && !x.IsArchiveLane).OrderBy(x => x.Position).ToListAsync();

            // Composite view reuses the paginated cards-query path with no limit: same
            // board scope, temp/archive exclusion, and canonical ordering as GET /cards.
            var cardsQuery = CardQueryHelper.BoardCards(db.Cards, db.Lanes, boardId, includeArchived: false);
            var rawCards = await CardQueryHelper.OrderForBoard(cardsQuery).ToListAsync();
            var cards = await CardSummaryBuilder.BuildAsync(db, rawCards);

            var sizes = await db.CardSizes.Where(x => x.BoardId == boardId).OrderBy(x => x.Ordinal).ToListAsync();
            return Results.Ok(new { lanes, cards, sizes });
        }).RequireAuth();

        return group;
    }
}
