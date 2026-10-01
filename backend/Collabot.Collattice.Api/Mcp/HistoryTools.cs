using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Mcp;

[McpServerToolType]
public sealed class HistoryTools(BoardDbContext db, McpAuthService auth)
{
    [McpServerTool(Name = "get_card_history", ReadOnly = true, Destructive = false)]
    [Description("Get the edit history of a card's description — every recorded version with who replaced it and when, newest first. Defaults to format 'diff', which returns a unified (git-style) diff of what each edit changed instead of full snapshots; pass 'full' for the whole text at each revision or 'both' for each. Returns { cardId, field, entries, totalCount, offset, limit } — the newest 200 revisions by default; page a longer trail with offset/limit and compare entries.length against totalCount. Supply from AND to to get the diff between two arbitrary revisions instead of the whole trail. The oldest revision (the text as it stood when recording began) has a null editedByName because nobody observed who wrote it; it carries inferredEditor { userId, name, basis } instead — basis 'creator' means the card's creator, the best available attribution, not an observed one. History starts at a card's first description edit — a never-edited card returns an empty trail and its current text is available from get_card.")]
    public async Task<string> GetCardHistoryAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the card (provide this or cardNumber)")] Guid? cardId = null,
        [Description("The card number (provide this or cardId). Requires boardId or boardSlug.")] long? cardNumber = null,
        [Description("Board ID (required when using cardNumber)")] Guid? boardId = null,
        [Description("Board slug (alternative to boardId when using cardNumber)")] string? boardSlug = null,
        [Description("Which field's history to return. Defaults to 'description', the only field recorded today.")] string? field = null,
        [Description("One of 'diff' (default — unified diff of what each edit changed), 'full' (the whole value at each revision), or 'both'.")] string? format = null,
        [Description("Start revision of an arbitrary-pair comparison. Requires 'to'.")] int? from = null,
        [Description("End revision of an arbitrary-pair comparison. Requires 'from'.")] int? to = null,
        [Description("Number of revisions to skip, counting back from the newest (default 0). Use with limit to walk a long trail.")] int? offset = null,
        [Description("Maximum number of revisions to return (default 200, max 500). The response's totalCount is the whole trail's length regardless.")] int? limit = null,
        CancellationToken ct = default
    )
    {
        var (_, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var (resolvedCardId, resolveError) = await McpCardResolver.ResolveCardIdAsync(db, cardId, cardNumber, boardId, boardSlug, ct);
        if (resolveError is not null)
        {
            return resolveError;
        }

        if (!await db.Cards.AnyAsync(c => c.Id == resolvedCardId!.Value, ct))
        {
            return "Error: Card not found.";
        }

        var (resolvedField, fieldError) = CardHistoryHelper.ResolveField(field);
        if (fieldError is not null)
        {
            return $"Error: {fieldError}";
        }

        // Diff is the default here and 'both' on REST: a bot asking "what changed?" wants the
        // change, not N full snapshots it has to diff itself, and snapshots of a long description
        // are the expensive part of this response.
        if (!CardHistoryBuilder.TryParseFormat(format, CardHistoryFormat.Diff, out var resolvedFormat))
        {
            return $"Error: {CardHistoryBuilder.FormatError}";
        }

        if (from.HasValue != to.HasValue)
        {
            return "Error: from and to must be supplied together.";
        }

        if (from.HasValue && to.HasValue)
        {
            // A pair comparison answers with one object, so there is no page to take of it.
            if (offset.HasValue || limit.HasValue)
            {
                return "Error: offset and limit do not apply to a from/to comparison.";
            }

            var (pair, pairError) = await CardHistoryBuilder.BuildPairAsync(db, resolvedCardId!.Value, resolvedField!, resolvedFormat, from.Value, to.Value, ct);

            return pairError is not null
                ? $"Error: {pairError}"
                : JsonSerializer.Serialize(pair, JsonSerializerOptions.Web);
        }

        // Capped by default, matching get_cards: this is the surface that pays per token, and a
        // trail is the one list in this API whose whole premise is that it grows without bound.
        // The cap is visible rather than silent — totalCount always reports the full length.
        var effectiveOffset = Math.Max(offset ?? 0, 0);
        var effectiveLimit = Math.Clamp(limit ?? 200, 1, 500);

        var trail = await CardHistoryBuilder.BuildTrailAsync(db, resolvedCardId!.Value, resolvedField!, resolvedFormat, effectiveOffset, effectiveLimit, ct);
        return JsonSerializer.Serialize(trail, JsonSerializerOptions.Web);
    }
}
