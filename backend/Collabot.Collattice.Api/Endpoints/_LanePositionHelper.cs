using Collabot.Collattice.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// Creates a lane and resolves the position it takes, shared by the REST endpoint
// (POST /boards/{boardId}/lanes) and the MCP tool (create_lane) so the two front
// doors cannot drift. Every rejection is decided here because the unique
// (BoardId, Position) index would otherwise surface a taken position as an
// unhandled save failure (a 500 on REST).
internal static class LanePositionHelper
{
    public const string ReservedMessage = "Position value is reserved.";
    public const string TakenMessage = "Position already taken by another lane.";
    public const string NoRoomMessage = "No position is free after the board's last lane; pass an explicit position.";
    public const string ContendedMessage = "Other lanes were being created on this board at the same time; try again.";

    // Attempts for an omitted position. The read of the last lane and the insert are separate
    // statements, so concurrent creates on one board can resolve the same slot and all but one lose
    // the unique index. A loser re-reads and takes the next slot. This is the same max+1 shape as
    // card numbers, so it follows their measured choice: immediate retries, no pause, because a
    // pause stops the max advancing and wakes the losers into re-colliding clusters.
    private const int _maxAttempts = 8;

    public static async Task<LaneCreateResult> CreateAsync
    (
        BoardDbContext db,
        Guid boardId,
        string name,
        int? requestedPosition,
        CancellationToken ct = default
    )
    {
        for (var attempt = 0; attempt < _maxAttempts; attempt++)
        {
            var (position, error, isConflict) = await ResolveAsync(db, boardId, requestedPosition, ct);
            if (error is not null)
            {
                return LaneCreateResult.Rejected(error, isConflict);
            }

            var lane = new Lane { Id = Guid.NewGuid(), BoardId = boardId, Name = name, Position = position };
            db.Lanes.Add(lane);

            try
            {
                await db.SaveChangesAsync(ct);
                return LaneCreateResult.Created(lane);
            }
            catch (DbUpdateException ex) when (IsPositionCollision(ex))
            {
                // Re-resolve from the committed state. An appended position moves to the next free
                // slot; an explicit one now fails the taken check and answers 409 as if it had been
                // taken before the call.
                db.Entry(lane).State = EntityState.Detached;
            }
        }

        return LaneCreateResult.Rejected(ContendedMessage, isConflict: true);
    }

    // An omitted position appends after the board's last lane — what a caller who leaves it
    // out almost certainly wants, and the same rule the web UI applies. The archive lane sits
    // at int.MaxValue and is excluded, so "last lane" means the last visible one.
    private static async Task<(int Position, string? Error, bool IsConflict)> ResolveAsync
    (
        BoardDbContext db,
        Guid boardId,
        int? requested,
        CancellationToken ct
    )
    {
        if (requested == int.MaxValue)
        {
            return (0, ReservedMessage, false);
        }

        if (requested is int position)
        {
            var taken = await db.Lanes.AnyAsync(l => l.BoardId == boardId && l.Position == position, ct);

            return taken ? (0, TakenMessage, true) : (position, null, false);
        }

        var last = await db.Lanes
            .Where(l => l.BoardId == boardId && !l.IsArchiveLane)
                .MaxAsync(l => (int?)l.Position, ct);

        if (last is null)
        {
            return (0, null, false);
        }

        // The slot after the last lane can only be int.MaxValue itself, which belongs to the
        // archive lane.
        return last.Value >= int.MaxValue - 1
            ? (0, NoRoomMessage, false)
            : (last.Value + 1, null, false);
    }

    // SQLITE_CONSTRAINT_UNIQUE. The lane insert's only unique index is (BoardId, Position); a
    // foreign-key failure carries a different extended code and is not retried.
    private static bool IsPositionCollision(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };
}

// IsConflict lets REST answer with 409 Conflict, the same status the lane update endpoint uses
// for a taken position. MCP has no status and reads only Error.
internal sealed record LaneCreateResult(Lane? Lane, string? Error, bool IsConflict)
{
    public static LaneCreateResult Created(Lane lane) => new(lane, null, false);

    public static LaneCreateResult Rejected(string error, bool isConflict) => new(null, error, isConflict);
}
