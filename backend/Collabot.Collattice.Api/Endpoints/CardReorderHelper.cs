using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// A lane's order is the order of its saved cards. A draft (a card started in the create dialog and
// not yet saved) is left out of every card read, so it takes no place in that order: an index
// counts only the cards a caller can see, renumbering and the end of the lane pass over drafts, a
// draft's stored number is left alone, and finalizing a draft puts it at the end of its lane, where
// a create without attachments puts a card.
internal static class CardReorderHelper
{
    internal const int PositionGap = 10;

    // The position that sorts after every saved card in the lane except the one being placed.
    public static async Task<int> EndOfLanePositionAsync
    (
        BoardDbContext db,
        Guid laneId,
        Guid? placedCardId = null,
        CancellationToken ct = default
    ) =>
        (await db.Cards
            .Where(c => c.LaneId == laneId && !c.IsTemp && c.Id != placedCardId)
                .MaxAsync(c => (int?)c.Position, ct) ?? -PositionGap) + PositionGap;

    public static Task<int> MoveCardToLaneAsync
    (
        BoardDbContext db,
        CardItem card,
        Guid targetLaneId,
        int? index,
        CancellationToken ct = default
    ) => MoveCardsToLaneAsync(db, [card], targetLaneId, index, ct);

    // Places the cards in the target lane as one block, in the order given, starting at index
    // among the lane's saved cards that are not being moved. Every other saved card keeps its
    // relative order, and each lane the cards touch has its saved cards renumbered densely.
    //
    // A batch that saves once must place all of its cards in one call. Lane order is read from
    // the database, which cannot see a move staged earlier in the same unsaved batch, so one
    // call per card places every card after the first against the order from before the batch.
    public static async Task<int> MoveCardsToLaneAsync
    (
        BoardDbContext db,
        IReadOnlyList<CardItem> cards,
        Guid targetLaneId,
        int? index,
        CancellationToken ct = default
    )
    {
        var batch = cards
            .DistinctBy(c => c.Id)
                .ToList();
        var batchIds = batch
            .Select(c => c.Id)
                .ToList();
        var sourceLaneIds = batch
            .Select(c => c.LaneId)
            .Where(laneId => laneId != targetLaneId)
            .Distinct()
                .ToList();

        var targetCards = await db.Cards
            .Where(c => c.LaneId == targetLaneId && !c.IsTemp && !batchIds.Contains(c.Id))
            .OrderBy(c => c.Position)
                .ToListAsync(ct);

        var resolvedIndex = Math.Clamp(index ?? 0, 0, targetCards.Count);
        targetCards.InsertRange(resolvedIndex, batch);

        Renumber(targetCards);

        foreach (var card in batch)
        {
            card.LaneId = targetLaneId;
        }

        if (sourceLaneIds.Count > 0)
        {
            var sourceCards = await db.Cards
                .Where(c => sourceLaneIds.Contains(c.LaneId) && !c.IsTemp && !batchIds.Contains(c.Id))
                .OrderBy(c => c.Position)
                    .ToListAsync(ct);

            foreach (var lane in sourceCards.GroupBy(c => c.LaneId))
            {
                Renumber([.. lane]);
            }
        }

        return resolvedIndex;
    }

    private static void Renumber(List<CardItem> laneCards)
    {
        for (var i = 0; i < laneCards.Count; i++)
        {
            laneCards[i].Position = i * PositionGap;
        }
    }
}
