using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

internal static class CommentEndpoints
{
    public static RouteGroupBuilder MapCommentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/cards/{id:guid}/comments", async (BoardDbContext db, Guid id) =>
        {
            if (!await db.Cards.AnyAsync(x => x.Id == id))
            {
                return Results.NotFound();
            }

            var comments = (await db.Comments
                .Where(x => x.CardId == id)
                    .ToListAsync())
                .OrderBy(x => x.LastUpdatedAtUtc)
                    .ToList();
            return Results.Ok(comments);
        }).RequireAuth();

        group.MapPost("/cards/{id:guid}/comments", async (BoardDbContext db, HttpContext http, Guid id, CreateCommentRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            if (!await db.Cards.AnyAsync(x => x.Id == id, ct))
            {
                return Results.NotFound();
            }

            if (await ArchiveGuard.IsCardArchivedAsync(db, id))
            {
                return Results.BadRequest("Archived cards cannot be modified. Restore the card first.");
            }

            var now = DateTimeOffset.UtcNow;
            var comment = new CardComment
            {
                Id = Guid.NewGuid(),
                CardId = id,
                UserId = http.CurrentUser().Id,
                ContentMarkdown = request.ContentMarkdown,
                CreatedAtUtc = now,
                LastUpdatedAtUtc = now,
            };
            db.Comments.Add(comment);
            await db.SaveChangesAsync(ct);

            // comment.created — same single board bell, plus one webhook event.
            await WebhookEventFactory.PublishCommentCreatedAsync(db, broadcaster, comment, http.CurrentUser(), ct);
            return Results.Created($"/api/v1/cards/{id}/comments/{comment.Id}", comment);
        }).RequireAuth();

        group.MapDelete("/comments/{id:guid}", async (BoardDbContext db, HttpContext http, Guid id, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var comment = await db.Comments.FindAsync([id], ct);
            if (comment is null)
            {
                return Results.NotFound();
            }

            if (await ArchiveGuard.IsCardArchivedAsync(db, comment.CardId))
            {
                return Results.BadRequest("Archived cards cannot be modified. Restore the card first.");
            }

            var user = http.CurrentUser();

            // Own-or-admin-level: Administrator or AgentAdministrator may delete
            // another user's comment, matching the MCP delete_comment tool.
            if (comment.UserId != user.Id && !McpAuthService.IsAdminLevel(user))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            db.Comments.Remove(comment);
            await db.SaveChangesAsync(ct);

            // comment.deleted — published from the captured comment after the row is gone; the
            // card it belonged to still exists, so the card ref resolves.
            await WebhookEventFactory.PublishCommentDeletedAsync(db, broadcaster, comment, user, ct);
            return Results.NoContent();
        }).RequireAuth();

        group.MapPatch("/comments/{id:guid}", async (BoardDbContext db, HttpContext http, Guid id, UpdateCommentRequest request, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var comment = await db.Comments.FindAsync([id], ct);
            if (comment is null)
            {
                return Results.NotFound();
            }

            if (await ArchiveGuard.IsCardArchivedAsync(db, comment.CardId))
            {
                return Results.BadRequest("Archived cards cannot be modified. Restore the card first.");
            }

            var user = http.CurrentUser();

            // Own-or-admin-level: Administrator or AgentAdministrator may edit
            // another user's comment, matching the MCP update_comment tool.
            if (comment.UserId != user.Id && !McpAuthService.IsAdminLevel(user))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (request.ContentMarkdown is not null)
            {
                if (string.IsNullOrWhiteSpace(request.ContentMarkdown))
                {
                    return Results.BadRequest("Content cannot be empty.");
                }

                comment.ContentMarkdown = request.ContentMarkdown;
            }

            comment.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // comment.updated — same single board bell, plus one webhook event.
            await WebhookEventFactory.PublishCommentUpdatedAsync(db, broadcaster, comment, user, ct);
            return Results.Ok(comment);
        }).RequireAuth();

        return group;
    }
}
