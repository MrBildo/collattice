using Collabot.Collattice.Api.Models;
using Collabot.Collattice.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// Applies a lane's new name and position, shared by the REST endpoint (PATCH /lanes/{id}) and the MCP
// tool (update_lane) so the two front doors cannot drift. Positions are unique per board, so a
// position another lane holds is answered here as a conflict instead of surfacing as an unhandled save
// failure (a 500 on REST).
internal static class LaneUpdateHelper
{
    public const string ArchiveLaneMessage = "Archive lanes cannot be modified.";
    public const string ContendedMessage = "Another change to this board's lanes landed at the same time; try again.";

    // Null Error means the lane was saved. IsConflict marks the answers REST gives as 409.
    public static async Task<(string? Error, bool IsConflict)> UpdateAsync
    (
        BoardDbContext db,
        Lane lane,
        string? name,
        int? position,
        CancellationToken ct = default
    )
    {
        if (lane.IsArchiveLane)
        {
            return (ArchiveLaneMessage, false);
        }

        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            return ("Name cannot be empty.", false);
        }

        // The archive lane sits at int.MaxValue.
        if (position == int.MaxValue)
        {
            return (LanePositionHelper.ReservedMessage, false);
        }

        if (await IsPositionTakenAsync(db, lane, position, ct))
        {
            return (LanePositionHelper.TakenMessage, true);
        }

        if (name is not null)
        {
            lane.Name = name;
        }

        if (position is int newPosition)
        {
            lane.Position = newPosition;
        }

        using var expected = ExpectedSaveFailure.Expect(IsPositionCollision);

        try
        {
            await db.SaveChangesAsync(ct);
            return (null, false);
        }
        catch (DbUpdateException ex) when (IsPositionCollision(ex))
        {
            // The check and the save are separate statements, so a concurrent create or move can take
            // the position in between: measured before this helper, simultaneous moves of different
            // lanes to one position returned 500 on 9 of 10 losers at two at once and on about half at
            // sixteen. There is nothing to allocate on an update, so the loser is not retried. It is
            // answered as if the position had been taken before the call, and if the position has
            // moved on again by the re-read, it is told to try again. A name change rides the same save
            // and is reverted with it.
            var entry = db.Entry(lane);
            entry.CurrentValues.SetValues(entry.OriginalValues);
            entry.State = EntityState.Unchanged;

            return (await IsPositionTakenAsync(db, lane, position, ct) ? LanePositionHelper.TakenMessage : ContendedMessage, true);
        }
    }

    private static async Task<bool> IsPositionTakenAsync(BoardDbContext db, Lane lane, int? position, CancellationToken ct) =>
        position is int newPosition
            && await db.Lanes.AnyAsync(l => l.BoardId == lane.BoardId && l.Position == newPosition && l.Id != lane.Id, ct);

    // SQLITE_CONSTRAINT_UNIQUE; the lane's only unique index is (BoardId, Position).
    private static bool IsPositionCollision(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };
}
