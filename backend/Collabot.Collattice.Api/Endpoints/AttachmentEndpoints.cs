using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Collabot.Collattice.Api.Endpoints;

internal static class AttachmentEndpoints
{
    public static RouteGroupBuilder MapAttachmentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/cards/{id:guid}/attachments", async (BoardDbContext db, Guid id) =>
        {
            if (!await db.Cards.AnyAsync(x => x.Id == id))
            {
                return Results.NotFound();
            }

            var attachments = await db.Attachments
                .Where(x => x.CardId == id)
                    .Select(x => new { x.Id, x.FileName, x.ContentType, FileSize = (long)x.Payload.Length, x.AddedByUserId, x.AddedAtUtc })
                        .ToListAsync();
            return Results.Ok(attachments);
        }).RequireAuth();

        group.MapPost("/cards/{id:guid}/attachments", async (BoardDbContext db, HttpContext http, Guid id, IFormFile file, BoardEventBroadcaster broadcaster, IOptions<AttachmentSettings> settings, CancellationToken ct) =>
        {
            if (!await db.Cards.AnyAsync(x => x.Id == id, ct))
            {
                return Results.NotFound();
            }

            if (await ArchiveGuard.IsCardArchivedAsync(db, id))
            {
                return Results.BadRequest("Archived cards cannot be modified. Restore the card first.");
            }

            if (file.Length > settings.Value.MaxRestUploadBytes)
            {
                return Results.BadRequest("File too large.");
            }

            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var attachment = new CardAttachment
            {
                Id = Guid.NewGuid(),
                CardId = id,
                FileName = file.FileName,
                ContentType = file.ContentType,
                Payload = ms.ToArray(),
                AddedByUserId = http.CurrentUser().Id,
                AddedAtUtc = DateTimeOffset.UtcNow,
            };
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync(ct);

            // attachment.created — metadata only, same single board bell plus one webhook.
            await WebhookEventFactory.PublishAttachmentCreatedAsync(db, broadcaster, attachment, http.CurrentUser(), ct);
            return Results.Created($"/api/v1/cards/{id}/attachments/{attachment.Id}", new { attachment.Id, attachment.FileName });
        }).DisableAntiforgery().RequireAuth();

        group.MapGet("/attachments/{id:guid}", async (BoardDbContext db, Guid id) =>
        {
            var attachment = await db.Attachments.FindAsync(id);
            return attachment is null ? Results.NotFound() : Results.File(attachment.Payload, attachment.ContentType, attachment.FileName);
        }).RequireAuth();

        group.MapDelete("/attachments/{id:guid}", async (BoardDbContext db, HttpContext http, Guid id, BoardEventBroadcaster broadcaster, CancellationToken ct) =>
        {
            var attachment = await db.Attachments.FindAsync([id], ct);
            if (attachment is null)
            {
                return Results.NotFound();
            }

            if (await ArchiveGuard.IsCardArchivedAsync(db, attachment.CardId))
            {
                return Results.BadRequest("Archived cards cannot be modified. Restore the card first.");
            }

            var user = http.CurrentUser();

            // Own-or-admin-level: Administrator or AgentAdministrator may delete
            // another user's attachment, matching the MCP delete_attachment tool.
            if (attachment.AddedByUserId != user.Id && !McpAuthService.IsAdminLevel(user))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            db.Attachments.Remove(attachment);
            await db.SaveChangesAsync(ct);

            // attachment.deleted — published from the captured attachment after the row is gone.
            await WebhookEventFactory.PublishAttachmentDeletedAsync(db, broadcaster, attachment, user, ct);
            return Results.NoContent();
        }).RequireAuth();

        return group;
    }
}
