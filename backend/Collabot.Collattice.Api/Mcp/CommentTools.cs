using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Mcp;

[McpServerToolType]
public class CommentTools(BoardDbContext db, McpAuthService auth, BoardEventBroadcaster broadcaster)
{
    [McpServerTool(Name = "add_comment", Destructive = false)]
    [Description("Add a comment to a card. Provide either cardId or cardNumber to identify the card.")]
    public async Task<string> AddCommentAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The comment text (Markdown supported).")] string contentMarkdown,
        [Description("The ID (guid) of the card to comment on (provide this or cardNumber)")] Guid? cardId = null,
        [Description("The card number (provide this or cardId). Requires boardId or boardSlug.")] long? cardNumber = null,
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

        if (string.IsNullOrWhiteSpace(contentMarkdown))
        {
            return "Error: contentMarkdown is required.";
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

        var now = DateTimeOffset.UtcNow;
        var comment = new CardComment
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            UserId = user!.Id,
            ContentMarkdown = contentMarkdown,
            CreatedAtUtc = now,
            LastUpdatedAtUtc = now,
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);

        // comment.created — REST/MCP emit the identical event through the shared factory.
        await WebhookEventFactory.PublishCommentCreatedAsync(db, broadcaster, comment, user!, ct);
        return JsonSerializer.Serialize(comment, JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "update_comment", Destructive = false)]
    [Description("Edit the text of a comment you wrote. Administrator and AgentAdministrator roles can edit any comment. Mirrors REST PATCH /comments/{id}. Blocked on archived cards.")]
    public async Task<string> UpdateCommentAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the comment to edit")] Guid commentId,
        [Description("The new comment text (Markdown supported).")] string contentMarkdown,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(contentMarkdown))
        {
            return "Error: contentMarkdown is required.";
        }

        var comment = await McpCardResolver.FindVisibleCommentAsync(db, user!, commentId, ct);
        if (comment is null)
        {
            return "Error: Comment not found.";
        }

        if (await ArchiveGuard.IsCardArchivedAsync(db, comment.CardId))
        {
            return "Archived cards cannot be modified.";
        }

        // Own-or-admin-level, matching delete_comment: the author edits
        // their own comment; Administrator and AgentAdministrator may edit any comment.
        if (comment.UserId != user!.Id && !McpAuthService.IsAdminLevel(user))
        {
            return "Error: You can only edit your own comments.";
        }

        comment.ContentMarkdown = contentMarkdown;
        comment.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // comment.updated — REST/MCP emit the identical event through the shared factory.
        await WebhookEventFactory.PublishCommentUpdatedAsync(db, broadcaster, comment, user!, ct);
        return JsonSerializer.Serialize(comment, JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "delete_comment", Destructive = true)]
    [Description("Delete a comment you wrote. Administrator and AgentAdministrator roles can delete any comment.")]
    public async Task<string> DeleteCommentAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the comment to delete")] Guid commentId,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var comment = await McpCardResolver.FindVisibleCommentAsync(db, user!, commentId, ct);
        if (comment is null)
        {
            return "Error: Comment not found.";
        }

        if (await ArchiveGuard.IsCardArchivedAsync(db, comment.CardId))
        {
            return "Archived cards cannot be modified.";
        }

        // Own-or-admin widens to own-or-admin-or-agent-admin —
        // AgentAdministrator inherits the admin's "delete others'" privilege.
        if (comment.UserId != user!.Id && !McpAuthService.IsAdminLevel(user))
        {
            return "Error: You can only delete your own comments.";
        }

        db.Comments.Remove(comment);
        await db.SaveChangesAsync(ct);

        // comment.deleted — published from the captured comment after the row is gone.
        await WebhookEventFactory.PublishCommentDeletedAsync(db, broadcaster, comment, user!, ct);
        return "Comment deleted.";
    }
}
