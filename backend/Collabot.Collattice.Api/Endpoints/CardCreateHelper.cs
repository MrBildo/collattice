using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// Shared card-construction logic for every write path that creates a card: the REST
// standard create (POST /cards), the REST temp create (POST /cards/temp), and the
// MCP create_card tool. All three perform identical validation (name-required,
// lane-belongs-board, archive-lane block), size resolution (via SizeResolver — id
// or name or default), position calculation, and label staging. They differ only in
// how the card is persisted afterward (number allocation via CardNumberHelper vs.
// plain Add with IsTemp = true) and in how each surface parses/validates its label
// input — so the caller resolves labels its own way and passes the validated list.
//
// Promoted from a file-scoped helper in CardEndpoints to its own internal static
// file so the MCP assembly can route through it, on the PruneFilter /
// CardQueryHelper precedent. The return channel is the neutral (CardItem?, string?)
// idiom (McpLabelParsing / McpCardResolver). The error message is bare (no "Error: "
// prefix) so each front door applies its own idiom: REST maps the string? error to
// Results.BadRequest verbatim, MCP prefixes "Error: " at the call site (matching the
// rest of the MCP surface). BuildCardAsync returns the un-persisted CardItem; the
// caller sets Number / IsTemp and saves.
internal static class CardCreateHelper
{
    public static async Task<(CardItem? Card, string? Error)> BuildCardAsync
    (
        BoardDbContext db,
        Guid boardId,
        CreateCardRequest request,
        IReadOnlyList<Guid> labelIds,
        BoardUser currentUser,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (null, "Name is required.");
        }

        var targetLane = await db.Lanes.FirstOrDefaultAsync(x => x.Id == request.LaneId && x.BoardId == boardId, ct);
        if (targetLane is null)
        {
            return (null, "Lane does not belong to this board.");
        }

        if (targetLane.IsArchiveLane)
        {
            return (null, "Cards cannot be created in the archive lane.");
        }

        var (resolvedSizeId, sizeError) = await SizeResolver.ResolveAsync(db, boardId, request.SizeId, request.SizeName, ct);
        if (sizeError is not null)
        {
            return (null, sizeError);
        }

        var position = request.Position ?? await CardReorderHelper.EndOfLanePositionAsync(db, request.LaneId, ct: ct);

        var now = DateTimeOffset.UtcNow;
        var card = new CardItem
        {
            Id = Guid.NewGuid(),
            BoardId = boardId,
            Name = request.Name,
            DescriptionMarkdown = request.DescriptionMarkdown ?? "",
            SizeId = resolvedSizeId!.Value,
            LaneId = request.LaneId,
            Position = position,
            CreatedAtUtc = now,
            LastUpdatedAtUtc = now,
            CreatedByUserId = currentUser.Id,
            LastUpdatedByUserId = currentUser.Id,
        };

        foreach (var labelId in labelIds)
        {
            db.CardLabels.Add(new CardLabel { CardId = card.Id, LabelId = labelId });
        }

        return (card, null);
    }
}
