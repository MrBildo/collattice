using System.ComponentModel;
using System.Text.Json;
using Collabot.Collattice.Api.Endpoints;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Models;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Mcp;

// Duplication is only a shortcut for creating a card that starts from an existing one: nothing links
// the copy to its source, and nothing on either card records that a copy was made. So the copy goes
// through the exact create path create_card uses — CardCreateHelper builds it (name, archive-lane,
// size and lane-belongs-board validation, bottom-of-lane position), CardNumberHelper allocates its
// board-scoped number, and WebhookEventFactory emits card.created — which makes it a normal new card
// in every respect by construction rather than by imitation. Only name, description, labels and size
// travel; comments, attachments, description history and position deliberately do not.
[McpServerToolType]
public class DuplicateCardTools(BoardDbContext db, McpAuthService auth, BoardEventBroadcaster broadcaster)
{
    [McpServerTool(Name = "duplicate_card", Destructive = false)]
    [Description("Create a new card that starts from an existing one. The copy carries the source's name, description, labels and size — nothing else (no comments, attachments, description history or position) — and has no link back to the source. It is a normal new card: it gets the next card number on the board, lands at the bottom of its lane exactly as create_card would, and raises card.created. laneId defaults to the source card's lane; when the source is archived its lane cannot be reused, so laneId is required. The archive lane is never a valid target. Optional name / sizeName / labelIds override the copied values. Returns the enriched card summary, like create_card.")]
    public async Task<string> DuplicateCardAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the card to duplicate (provide this or cardNumber)")] Guid? cardId = null,
        [Description("The card number of the card to duplicate (provide this or cardId). Requires boardId or boardSlug.")] long? cardNumber = null,
        [Description("Board ID (required when using cardNumber)")] Guid? boardId = null,
        [Description("Board slug (alternative to boardId when using cardNumber)")] string? boardSlug = null,
        [Description("Lane (guid) for the copy, on the source card's board. Defaults to the source card's lane; required when the source card is archived.")] Guid? laneId = null,
        [Description("Optional name for the copy. Defaults to the source card's name.")] string? name = null,
        [Description("Optional size name (e.g. 'M', 'XL') for the copy. Defaults to the source card's size.")] string? sizeName = null,
        [Description("Optional label IDs (guids) for the copy, replacing the source card's labels. Accepts comma-separated GUIDs ('guid1,guid2') or a JSON array string ('[\"guid1\",\"guid2\"]'); an empty string or empty array gives the copy no labels. Defaults to the source card's labels.")] string? labelIds = null,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var (resolvedCardId, resolveError) = await McpCardResolver.ResolveCardIdAsync(db, cardId, cardNumber, boardId, boardSlug, ct);
        if (resolveError is not null)
        {
            return resolveError;
        }

        var source = await db.Cards.FindAsync([resolvedCardId!.Value], ct);
        if (source is null)
        {
            return "Error: Card not found.";
        }

        // A temp card is an unsaved draft behind someone's open new-card form, not a card yet — there
        // is nothing settled to copy, and copying it would publish their half-typed fields.
        if (source.IsTemp)
        {
            return "Error: Temp cards (unsaved drafts) cannot be duplicated.";
        }

        var sourceLane = await db.Lanes.FindAsync([source.LaneId], ct);
        var sourceIsArchived = sourceLane is not null && sourceLane.IsArchiveLane;

        // An archived card's lane is the hidden archive lane, which no card may be created in, so there
        // is no sensible default to fall back on — the caller has to say where the copy goes.
        if (laneId is null && sourceIsArchived)
        {
            return "Error: The source card is archived, so the copy cannot go in its lane. Pass laneId to choose the lane for the copy.";
        }

        var targetLaneId = laneId ?? source.LaneId;

        if (!await db.Lanes.AnyAsync(l => l.Id == targetLaneId, ct))
        {
            return "Error: Lane not found.";
        }

        // Labels and sizes are board-scoped, so the copy stays on the source's board: CardCreateHelper
        // rejects a lane from another board, and the label override is validated against this board.
        List<Guid> copiedLabelIds;
        if (labelIds is null)
        {
            copiedLabelIds = await db.CardLabels
                .Where(cl => cl.CardId == source.Id)
                    .Select(cl => cl.LabelId)
                        .ToListAsync(ct);
        }
        else
        {
            var (parsedLabelIds, labelError) = await McpLabelParsing.ParseAndValidateLabelIdsAsync(db, labelIds, source.BoardId, ct);
            if (labelError is not null)
            {
                return labelError;
            }

            copiedLabelIds = parsedLabelIds;
        }

        var request = new CreateCardRequest
        (
            targetLaneId,
            name ?? source.Name,
            source.DescriptionMarkdown,
            Position: null,
            SizeId: sizeName is null ? source.SizeId : null,
            SizeName: sizeName,
            LabelIds: null
        );

        var (copy, buildError) = await CardCreateHelper.BuildCardAsync(db, source.BoardId, request, copiedLabelIds, user!, ct);
        if (buildError is not null)
        {
            return $"Error: {buildError}";
        }

        await CardNumberHelper.InsertCardWithAutoNumberAsync(db, copy!, source.BoardId, ct);

        // card.created rings the SSE bell as well (the typed event downsamples to the board-updated
        // signal), so no separate board broadcast — the same emission create_card makes.
        await WebhookEventFactory.PublishCardCreatedAsync(db, broadcaster, copy!, user!, ct);

        var summaries = await CardSummaryBuilder.BuildAsync(db, [copy!], ct);
        return JsonSerializer.Serialize(summaries[0], JsonSerializerOptions.Web);
    }
}
