using Collabot.Collattice.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// Three deletes are allowed only while nothing depends on the row: a lane with no cards, a size no
// card uses, a board with no lanes but its archive lane. Each caller checks that first, for its usual
// answer, but a check and a delete are separate statements, and a card or lane can land in between.
// For a lane or a board that was worse than an error: the delete cascaded, so the card or lane just
// created (or moved in) was removed with it while its caller had been told it succeeded. Measured
// against a running API with the delete released together with the writes, 25 of 25 cards created
// into a lane as it was deleted, 32 of 32 cards moved into it, and 43 of 51 lanes created on a board
// as it was deleted were gone afterwards. A size cannot cascade, so its delete failed with a 500.
//
// Here the condition is part of the delete statement itself, which SQLite runs under its single
// write lock, so nothing can land between the check and the delete. When the statement removes
// nothing, a re-read tells "something now depends on it" from "someone else deleted it first".
internal static class GuardedDelete
{
    public static async Task<GuardedDeleteOutcome> LaneAsync(BoardDbContext db, Lane lane, CancellationToken ct = default)
    {
        var deleted = await db.Lanes
            .Where(l => l.Id == lane.Id && !db.Cards.Any(c => c.LaneId == l.Id))
                .ExecuteDeleteAsync(ct);

        return await OutcomeAsync(db, lane, deleted, () => db.Lanes.AnyAsync(l => l.Id == lane.Id, ct));
    }

    public static async Task<GuardedDeleteOutcome> SizeAsync(BoardDbContext db, CardSize size, CancellationToken ct = default)
    {
        var deleted = await db.CardSizes
            .Where(s => s.Id == size.Id && !db.Cards.Any(c => c.SizeId == s.Id))
                .ExecuteDeleteAsync(ct);

        return await OutcomeAsync(db, size, deleted, () => db.CardSizes.AnyAsync(s => s.Id == size.Id, ct));
    }

    // The archive lane and the archived cards in it go with the board, as they always have.
    public static async Task<GuardedDeleteOutcome> BoardAsync(BoardDbContext db, Board board, CancellationToken ct = default)
    {
        var deleted = await db.Boards
            .Where(b => b.Id == board.Id && !db.Lanes.Any(l => l.BoardId == b.Id && !l.IsArchiveLane))
                .ExecuteDeleteAsync(ct);

        return await OutcomeAsync(db, board, deleted, () => db.Boards.AnyAsync(b => b.Id == board.Id, ct));
    }

    // The re-read only runs when nothing was deleted. The loaded row is detached either way,
    // so nothing later in the same context mistakes it for a row that still exists.
    private static async Task<GuardedDeleteOutcome> OutcomeAsync
    (
        BoardDbContext db,
        object entity,
        int deleted,
        Func<Task<bool>> stillExistsAsync
    )
    {
        db.Entry(entity).State = EntityState.Detached;

        if (deleted > 0)
        {
            return GuardedDeleteOutcome.Deleted;
        }

        var stillExists = await stillExistsAsync();

        return stillExists ? GuardedDeleteOutcome.InUse : GuardedDeleteOutcome.NotFound;
    }
}

internal enum GuardedDeleteOutcome
{
    Deleted,
    InUse,
    NotFound,
}
