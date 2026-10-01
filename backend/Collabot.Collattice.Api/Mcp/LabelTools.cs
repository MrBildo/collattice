using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Mcp;

[McpServerToolType]
public sealed class LabelTools(BoardDbContext db, McpAuthService auth, BoardEventBroadcaster broadcaster)
{
    [McpServerTool(Name = "get_labels", ReadOnly = true, Destructive = false)]
    [Description("Get all labels for a specific board.")]
    public async Task<string> GetLabelsAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The board ID to list labels from")] Guid boardId,
        CancellationToken ct = default
    )
    {
        var (_, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct))
        {
            return "Error: Board not found.";
        }

        var labels = await db.Labels.Where(l => l.BoardId == boardId).OrderBy(l => l.Name).ToListAsync(ct);
        return JsonSerializer.Serialize(labels, JsonSerializerOptions.Web);
    }

    // Admin-level label CRUD. Mirrors the REST surface in
    // LabelEndpoints.cs (POST/PATCH/DELETE /boards/{boardId}/labels). All three
    // gate via RequireAdminLevelAsync.
    [McpServerTool(Name = "create_label", Destructive = false)]
    [Description("Create a label on a board. Requires Administrator or AgentAdministrator role. Label names must be unique within a board. Color is an optional string (e.g. a hex value).")]
    public async Task<string> CreateLabelAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The board ID to create the label on")] Guid boardId,
        [Description("The label name")] string name,
        [Description("The label color (optional)")] string? color = null,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct))
        {
            return "Error: Board not found.";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return "Error: Name is required.";
        }

        if (await db.Labels.AnyAsync(l => l.BoardId == boardId && l.Name == name, ct))
        {
            return $"Error: {LabelUpdateHelper.NameTakenMessage}";
        }

        var label = new Label
        {
            Id = Guid.NewGuid(),
            BoardId = boardId,
            Name = name,
            Color = color,
        };
        db.Labels.Add(label);
        await db.SaveChangesAsync(ct);

        // label.created — REST/MCP emit the identical event through the shared factory.
        await WebhookEventFactory.PublishLabelCreatedAsync(db, broadcaster, label, user!, ct);
        return JsonSerializer.Serialize(label, JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "update_label", Destructive = false)]
    [Description("Update a label's name and/or color. Requires Administrator or AgentAdministrator role. Color is an optional string (e.g. a hex value). A name already taken by another label on the board is a conflict, and nothing in the call is saved.")]
    public async Task<string> UpdateLabelAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the label to update")] Guid labelId,
        [Description("The new label name (optional)")] string? name = null,
        [Description("The new label color (optional)")] string? color = null,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var label = await db.Labels.FindAsync([labelId], ct);
        if (label is null)
        {
            return "Error: Label not found.";
        }

        var (updateError, _) = await LabelUpdateHelper.UpdateAsync(db, label, name, color, ct);
        if (updateError is not null)
        {
            return $"Error: {updateError}";
        }

        // label.updated — REST/MCP emit the identical event through the shared factory.
        await WebhookEventFactory.PublishLabelUpdatedAsync(db, broadcaster, label, user!, ct);
        return JsonSerializer.Serialize(label, JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "delete_label", Destructive = true)]
    [Description("Delete a label. Requires Administrator or AgentAdministrator role. Removing a label un-assigns it from any cards it was applied to; it does not delete cards.")]
    public async Task<string> DeleteLabelAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the label to delete")] Guid labelId,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var label = await db.Labels.FindAsync([labelId], ct);
        if (label is null)
        {
            return "Error: Label not found.";
        }

        var cardLabels = await db.CardLabels.Where(cl => cl.LabelId == labelId).ToListAsync(ct);
        db.CardLabels.RemoveRange(cardLabels);
        db.Labels.Remove(label);
        await db.SaveChangesAsync(ct);

        // label.deleted — published from the captured label after the row is gone.
        await WebhookEventFactory.PublishLabelDeletedAsync(db, broadcaster, label, user!, ct);
        return "Label deleted.";
    }

    [McpServerTool(Name = "add_label_to_card", Destructive = false)]
    [Description("Add a label to a card. Identify the card by cardId or cardNumber. Identify the label by labelId or labelName. The label must belong to the same board as the card.")]
    public async Task<string> AddLabelToCardAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the card (provide this or cardNumber)")] Guid? cardId = null,
        [Description("The card number (provide this or cardId). Requires boardId or boardSlug.")] long? cardNumber = null,
        [Description("The ID (guid) of the label to add. Provide this or labelName, not both.")] Guid? labelId = null,
        [Description("The name of the label to add (matched case-insensitively within the card's board). Provide this or labelId, not both.")] string? labelName = null,
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

        var (resolvedCardId, cardResolveError) = await McpCardResolver.ResolveCardIdAsync(db, cardId, cardNumber, boardId, boardSlug, ct);
        if (cardResolveError is not null)
        {
            return cardResolveError;
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

        var cardBoardId = await db.Lanes.Where(l => l.Id == card.LaneId).Select(l => l.BoardId).FirstOrDefaultAsync(ct);

        var (resolvedLabelId, resolveError) = await ResolveLabelAsync(labelId, labelName, cardBoardId, ct);
        if (resolveError is not null)
        {
            return resolveError;
        }

        var label = await db.Labels.FindAsync([resolvedLabelId], ct);
        if (label is null)
        {
            return "Error: Label not found.";
        }

        if (label.BoardId != cardBoardId)
        {
            return "Error: Label does not belong to the same board as the card.";
        }

        if (await CardLabelHelper.AssignAsync(db, card.Id, label.Id, ct) is null)
        {
            return "Label already assigned to this card.";
        }

        // card.labeled — REST/MCP emit the identical event through the shared factory.
        await WebhookEventFactory.PublishCardLabeledAsync(db, broadcaster, card, label, user!, ct);
        return "Label added successfully.";
    }

    [McpServerTool(Name = "remove_label_from_card", Destructive = false)]
    [Description("Remove a label from a card. Identify the card by cardId or cardNumber. Identify the label by labelId or labelName.")]
    public async Task<string> RemoveLabelFromCardAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the card (provide this or cardNumber)")] Guid? cardId = null,
        [Description("The card number (provide this or cardId). Requires boardId or boardSlug.")] long? cardNumber = null,
        [Description("The ID (guid) of the label to remove. Provide this or labelName, not both.")] Guid? labelId = null,
        [Description("The name of the label to remove (matched case-insensitively within the card's board). Provide this or labelId, not both.")] string? labelName = null,
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

        var (resolvedCardId, cardResolveError) = await McpCardResolver.ResolveCardIdAsync(db, cardId, cardNumber, boardId, boardSlug, ct);
        if (cardResolveError is not null)
        {
            return cardResolveError;
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

        var cardBoardId = await db.Lanes.Where(l => l.Id == card.LaneId).Select(l => l.BoardId).FirstOrDefaultAsync(ct);

        var (resolvedLabelId, resolveError) = await ResolveLabelAsync(labelId, labelName, cardBoardId, ct);
        if (resolveError is not null)
        {
            return resolveError;
        }

        var cardLabel = await db.CardLabels.FirstOrDefaultAsync(cl => cl.CardId == card.Id && cl.LabelId == resolvedLabelId, ct);
        if (cardLabel is null)
        {
            return "Error: Label not assigned to this card.";
        }

        // Resolve the label resource for the event BEFORE removing the association (the Label
        // row itself persists; only the card↔label join is removed).
        var label = await db.Labels.FindAsync([resolvedLabelId!.Value], ct);

        if (!await CardLabelHelper.UnassignAsync(db, cardLabel, ct))
        {
            return "Error: Label not assigned to this card.";
        }

        // card.unlabeled — REST/MCP emit the identical event through the shared factory.
        if (label is not null)
        {
            await WebhookEventFactory.PublishCardUnlabeledAsync(db, broadcaster, card, label, user!, ct);
        }

        return "Label removed successfully.";
    }

    private async Task<(Guid? LabelId, string? Error)> ResolveLabelAsync
    (
        Guid? labelId,
        string? labelName,
        Guid boardId,
        CancellationToken ct
    )
    {
        var hasId = labelId.HasValue && labelId.Value != Guid.Empty;
        var hasName = !string.IsNullOrWhiteSpace(labelName);

        if (hasId && hasName)
        {
            return (null, "Error: Provide either labelId or labelName, not both.");
        }

        if (!hasId && !hasName)
        {
            return (null, "Error: Provide either labelId or labelName.");
        }

        if (hasId)
        {
            return (labelId, null);
        }

        var matches = await db.Labels
            .Where(l => l.BoardId == boardId && EF.Functions.Collate(l.Name, "NOCASE") == labelName!)
                .ToListAsync(ct);

        if (matches.Count == 0)
        {
            var available = await db.Labels
                .Where(l => l.BoardId == boardId)
                .OrderBy(l => l.Name)
                    .Select(l => l.Name)
                        .ToListAsync(ct);

            var availableList = available.Count > 0
                ? string.Join(", ", available)
                : "(none)";

            return (null, $"Error: No label named '{labelName}' found on this board. Available labels: {availableList}");
        }

        if (matches.Count > 1)
        {
            return (null, $"Error: Multiple labels named '{labelName}' found on this board. Use labelId instead.");
        }

        return (matches[0].Id, null);
    }
}
