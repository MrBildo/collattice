using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// Resolves the position a new lane takes, shared by the REST endpoint
// (POST /boards/{boardId}/lanes) and the MCP tool (create_lane) so the two front
// doors cannot drift. Every rejection is decided here, before the save, because the
// unique (BoardId, Position) index would otherwise surface a taken position as an
// unhandled save failure (a 500 on REST).
internal static class LanePositionHelper
{
    public const string ReservedMessage = "Position value is reserved.";
    public const string TakenMessage = "Position already taken by another lane.";
    public const string NoRoomMessage = "No position is free after the board's last lane; pass an explicit position.";

    // An omitted position appends after the board's last lane — what a caller who
    // leaves it out almost certainly wants, and the same rule the web UI applies.
    // The archive lane sits at int.MaxValue and is excluded, so "last lane" means
    // the last visible one.
    public static async Task<LanePositionResult> ResolveForCreateAsync
    (
        BoardDbContext db,
        Guid boardId,
        int? requested,
        CancellationToken ct = default
    )
    {
        if (requested == int.MaxValue)
        {
            return LanePositionResult.Rejected(ReservedMessage, isConflict: false);
        }

        if (requested is int position)
        {
            var taken = await db.Lanes.AnyAsync(l => l.BoardId == boardId && l.Position == position, ct);

            return taken
                ? LanePositionResult.Rejected(TakenMessage, isConflict: true)
                : LanePositionResult.Resolved(position);
        }

        var last = await db.Lanes
            .Where(l => l.BoardId == boardId && !l.IsArchiveLane)
                .MaxAsync(l => (int?)l.Position, ct);

        if (last is null)
        {
            return LanePositionResult.Resolved(0);
        }

        // The slot after the last lane can only be int.MaxValue itself, which belongs
        // to the archive lane.
        return last.Value >= int.MaxValue - 1
            ? LanePositionResult.Rejected(NoRoomMessage, isConflict: false)
            : LanePositionResult.Resolved(last.Value + 1);
    }
}

// IsConflict lets REST answer a taken position with 409 Conflict, the same status
// the lane update endpoint uses. MCP has no status and reads only Error.
internal sealed record LanePositionResult(int Position, string? Error, bool IsConflict)
{
    public static LanePositionResult Resolved(int position) => new(position, null, false);

    public static LanePositionResult Rejected(string error, bool isConflict) => new(0, error, isConflict);
}
