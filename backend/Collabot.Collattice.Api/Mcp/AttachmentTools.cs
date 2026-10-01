using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Mcp;

[McpServerToolType]
public sealed class AttachmentTools
(
    BoardDbContext db,
    McpAuthService auth,
    BoardEventBroadcaster broadcaster,
    IOptions<AttachmentSettings> attachmentSettings
)
{
    [McpServerTool(Name = "download_attachment", ReadOnly = true, Destructive = false)]
    [Description("Download an attachment's content as base64. Returns the file name, content type, and base64-encoded payload.")]
    public async Task<string> DownloadAttachmentAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the attachment to download")] Guid attachmentId,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var attachment = await McpCardResolver.FindVisibleAttachmentAsync(db, user!, attachmentId, ct);
        return attachment is null
            ? "Error: Attachment not found."
            : JsonSerializer.Serialize(new
            {
                attachment.Id,
                attachment.FileName,
                attachment.ContentType,
                FileSize = (long)attachment.Payload.Length,
                Base64Content = Convert.ToBase64String(attachment.Payload),
            }, JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "delete_attachment", Destructive = true)]
    [Description("Delete an attachment you added. Administrator and AgentAdministrator roles can delete any attachment.")]
    public async Task<string> DeleteAttachmentAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the attachment to delete")] Guid attachmentId,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var attachment = await McpCardResolver.FindVisibleAttachmentAsync(db, user!, attachmentId, ct);
        if (attachment is null)
        {
            return "Error: Attachment not found.";
        }

        if (await ArchiveGuard.IsCardArchivedAsync(db, attachment.CardId))
        {
            return "Archived cards cannot be modified.";
        }

        // Own-or-admin widens to own-or-admin-or-agent-admin —
        // AgentAdministrator inherits the admin's "delete others'" privilege.
        if (attachment.AddedByUserId != user!.Id && !McpAuthService.IsAdminLevel(user))
        {
            return "Error: You can only delete your own attachments.";
        }

        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync(ct);

        // attachment.deleted — published from the captured attachment after the row is gone.
        await WebhookEventFactory.PublishAttachmentDeletedAsync(db, broadcaster, attachment, user!, ct);
        return $"Attachment '{attachment.FileName}' deleted.";
    }

    [McpServerTool(Name = "upload_attachment", Destructive = false)]
    [Description("Upload a file attachment to a card using base64-encoded content. Limited to 5MB. For larger files (up to 50MB), use the REST endpoint POST /api/v1/cards/{cardId}/attachments (multipart/form-data).")]
    public async Task<string> UploadAttachmentAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The file name (e.g., 'report.pdf')")] string fileName,
        [Description("The base64-encoded file content")] string base64Content,
        [Description("The ID (guid) of the card to attach the file to (provide this or cardNumber)")] Guid? cardId = null,
        [Description("The card number (provide this or cardId). Requires boardId or boardSlug.")] long? cardNumber = null,
        [Description("The MIME content type (e.g., 'application/pdf', 'image/png'). Defaults to 'application/octet-stream'")] string? contentType = null,
        [Description("Board ID (required when using cardNumber)")] Guid? boardId = null,
        [Description("Board slug (alternative to boardId when using cardNumber)")] string? boardSlug = null,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var (resolvedCardId, resolveError) = await McpCardResolver.ResolveCardIdAsync(db, user!, cardId, cardNumber, boardId, boardSlug, ct);
        if (resolveError is not null)
        {
            return resolveError;
        }

        if (await ArchiveGuard.IsCardArchivedAsync(db, resolvedCardId!.Value))
        {
            return "Archived cards cannot be modified.";
        }

        var card = await db.Cards.FindAsync([resolvedCardId.Value], ct);
        if (card is null)
        {
            return "Error: Card not found.";
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(base64Content);
        }
        catch (FormatException)
        {
            return "Error: Invalid base64 content.";
        }

        var maxBytes = attachmentSettings.Value.MaxMcpUploadBytes;
        if (payload.Length > maxBytes)
        {
            var maxMb = maxBytes / (1024 * 1024);
            var restMaxMb = attachmentSettings.Value.MaxRestUploadBytes / (1024 * 1024);
            return $"Error: File exceeds {maxMb.ToString(CultureInfo.InvariantCulture)}MB limit for MCP uploads. Use the REST endpoint POST /api/v1/cards/{{cardId}}/attachments for larger files (up to {restMaxMb.ToString(CultureInfo.InvariantCulture)}MB).";
        }

        var attachment = new CardAttachment
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            FileName = fileName,
            ContentType = contentType ?? "application/octet-stream",
            Payload = payload,
            AddedByUserId = user!.Id,
            AddedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(ct);

        // attachment.created — REST/MCP emit the identical event through the shared factory.
        await WebhookEventFactory.PublishAttachmentCreatedAsync(db, broadcaster, attachment, user!, ct);
        return JsonSerializer.Serialize(new { attachment.Id, attachment.FileName }, JsonSerializerOptions.Web);
    }
}
