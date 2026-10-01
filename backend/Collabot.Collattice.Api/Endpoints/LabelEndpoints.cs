using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

internal static class LabelEndpoints
{
    public static RouteGroupBuilder MapLabelEndpoints(this RouteGroupBuilder group)
    {
        // Board-scoped label CRUD
        group.MapGet("/boards/{boardId:guid}/labels", async (BoardDbContext db, Guid boardId) =>
        {
            if (!await db.Boards.AnyAsync(x => x.Id == boardId))
            {
                return Results.NotFound();
            }

            var labels = await db.Labels.Where(x => x.BoardId == boardId).OrderBy(x => x.Name).ToListAsync();
            return Results.Ok(labels);
        }).RequireAuth();

        group.MapPost("/boards/{boardId:guid}/labels", async (BoardDbContext db, HttpContext http, Guid boardId, CreateLabelRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            if (!await db.Boards.AnyAsync(x => x.Id == boardId, ct))
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest("Name is required.");
            }

            if (await db.Labels.AnyAsync(x => x.BoardId == boardId && x.Name == request.Name, ct))
            {
                return Results.Conflict(LabelUpdateHelper.NameTakenMessage);
            }

            var label = new Label
            {
                Id = Guid.NewGuid(),
                BoardId = boardId,
                Name = request.Name,
                Color = request.Color,
            };
            db.Labels.Add(label);
            await db.SaveChangesAsync(ct);

            // label.created — same single board bell, plus one webhook event.
            await WebhookEventFactory.PublishLabelCreatedAsync(db, broadcaster, label, http.CurrentUser(), ct);
            return Results.Created($"/api/v1/boards/{boardId}/labels/{label.Id}", label);
        }).RequireAdminOrAgentAdmin();

        group.MapPatch("/boards/{boardId:guid}/labels/{id:guid}", async (BoardDbContext db, HttpContext http, Guid boardId, Guid id, UpdateLabelRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var label = await db.Labels.FindAsync([id], ct);
            if (label is null || label.BoardId != boardId)
            {
                return Results.NotFound();
            }

            var (error, isConflict) = await LabelUpdateHelper.UpdateAsync(db, label, request.Name, request.Color, ct);
            if (error is not null)
            {
                return isConflict
                    ? Results.Conflict(error)
                    : Results.BadRequest(error);
            }

            // label.updated — same single board bell, plus one webhook event.
            await WebhookEventFactory.PublishLabelUpdatedAsync(db, broadcaster, label, http.CurrentUser(), ct);
            return Results.Ok(label);
        }).RequireAdminOrAgentAdmin();

        group.MapDelete("/boards/{boardId:guid}/labels/{id:guid}", async (BoardDbContext db, HttpContext http, Guid boardId, Guid id, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var label = await db.Labels.FindAsync([id], ct);
            if (label is null || label.BoardId != boardId)
            {
                return Results.NotFound();
            }

            var cardLabels = await db.CardLabels.Where(x => x.LabelId == id).ToListAsync(ct);
            db.CardLabels.RemoveRange(cardLabels);
            db.Labels.Remove(label);
            await db.SaveChangesAsync(ct);

            // label.deleted — published from the captured label after the row is gone.
            await WebhookEventFactory.PublishLabelDeletedAsync(db, broadcaster, label, http.CurrentUser(), ct);
            return Results.NoContent();
        }).RequireAdminOrAgentAdmin();

        // Card-label operations (card-scoped routes, unchanged)
        group.MapGet("/cards/{id:guid}/labels", async (BoardDbContext db, Guid id) =>
        {
            if (!await db.Cards.AnyAsync(x => x.Id == id))
            {
                return Results.NotFound();
            }

            var labels = await db.CardLabels
                .Where(cl => cl.CardId == id)
                    .Join(db.Labels, cl => cl.LabelId, l => l.Id, (cl, l) => l)
                        .ToListAsync();

            return Results.Ok(labels);
        }).RequireAuth();

        group.MapPost("/cards/{id:guid}/labels", async (BoardDbContext db, HttpContext http, Guid id, AddCardLabelRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var card = await db.Cards.FindAsync([id], ct);
            if (card is null)
            {
                return Results.NotFound();
            }

            if (await ArchiveGuard.IsCardArchivedAsync(db, id))
            {
                return Results.BadRequest("Archived cards cannot be modified. Restore the card first.");
            }

            var labelId = request.LabelId;
            var label = await db.Labels.FindAsync([labelId], ct);
            if (label is null)
            {
                return Results.NotFound();
            }

            // Validate that the label belongs to the same board as the card
            var cardBoardId = await db.Lanes.Where(l => l.Id == card.LaneId).Select(l => l.BoardId).FirstOrDefaultAsync(ct);
            if (label.BoardId != cardBoardId)
            {
                return Results.BadRequest("Label does not belong to the same board as the card.");
            }

            if (await CardLabelHelper.AssignAsync(db, id, labelId, ct) is not CardLabel cardLabel)
            {
                return Results.Conflict("Label is already assigned to this card.");
            }

            // card.labeled — the card's label-set changed; the label resource is embedded so a
            // consumer knows which label without a follow-up fetch. Same SSE bell.
            await WebhookEventFactory.PublishCardLabeledAsync(db, broadcaster, card, label, http.CurrentUser(), ct);
            return Results.Created($"/api/v1/cards/{id}/labels/{labelId}", cardLabel);
        }).RequireAuth();

        group.MapDelete("/cards/{id:guid}/labels/{labelId:guid}", async (BoardDbContext db, HttpContext http, Guid id, Guid labelId, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var cardLabel = await db.CardLabels.FindAsync([id, labelId], ct);
            if (cardLabel is null)
            {
                return Results.NotFound();
            }

            if (await ArchiveGuard.IsCardArchivedAsync(db, id))
            {
                return Results.BadRequest("Archived cards cannot be modified. Restore the card first.");
            }

            var card = await db.Cards.FindAsync([id], ct);
            var label = await db.Labels.FindAsync([labelId], ct);

            if (!await CardLabelHelper.UnassignAsync(db, cardLabel, ct))
            {
                return Results.NotFound();
            }

            // card.unlabeled — the label row itself persists (only the card↔label association
            // is removed), so the embedded label resource is still resolvable.
            if (card is not null && label is not null)
            {
                await WebhookEventFactory.PublishCardUnlabeledAsync(db, broadcaster, card, label, http.CurrentUser(), ct);
            }

            return Results.NoContent();
        }).RequireAuth();

        return group;
    }
}
