using Collabot.Collattice.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

internal static class CardReorderHelper
{
    internal const int PositionGap = 10;

    public static Task<int> MoveCardToLaneAsync
    (
        BoardDbContext db,
        CardItem card,
        Guid targetLaneId,
        int? index,
        CancellationToken ct = default
    ) => MoveCardsToLaneAsync(db, [card], targetLaneId, index, ct);

    // Places the cards in the target lane as one block, in the order given, starting at index
    // among the lane's cards that are not being moved. Every other card keeps its relative order,
    // and each lane the cards touch is renumbered densely.
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
            .Where(c => c.LaneId == targetLaneId && !batchIds.Contains(c.Id))
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
                .Where(c => sourceLaneIds.Contains(c.LaneId) && !batchIds.Contains(c.Id))
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
