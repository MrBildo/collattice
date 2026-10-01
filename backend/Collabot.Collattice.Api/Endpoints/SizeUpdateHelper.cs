using Collabot.Collattice.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// Applies a size's new name and ordinal, shared by the REST endpoint (PATCH /sizes/{id}) and the MCP
// tool (update_size) so the two front doors cannot drift. Both values sit under a unique index per
// board, so a name or ordinal another size holds is answered here as a conflict instead of
// surfacing as an unhandled save failure (a 500 on REST).
internal static class SizeUpdateHelper
{
    public const string ContendedMessage = "Another change to this board's sizes landed at the same time; try again.";

    // Null Error means the size was saved. IsConflict marks the answers REST gives as 409.
    public static async Task<(string? Error, bool IsConflict)> UpdateAsync
    (
        BoardDbContext db,
        CardSize size,
        string? name,
        int? ordinal,
        CancellationToken ct = default
    )
    {
        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            return ("Name cannot be empty.", false);
        }

        if (await FindTakenAsync(db, size, name, ordinal, ct) is string taken)
        {
            return (taken, true);
        }

        if (name is not null)
        {
            size.Name = name;
        }

        if (ordinal is int newOrdinal)
        {
            size.Ordinal = newOrdinal;
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return (null, false);
        }
        catch (DbUpdateException ex) when (IsUniqueCollision(ex))
        {
            // The check and the save are separate statements, so a concurrent create or update can take
            // the name or ordinal in between: measured before this helper, 2 to 8 simultaneous updates
            // of different sizes to one name or one ordinal returned 500 on all but one. There is
            // nothing to allocate on an update, so the loser is not retried. It is answered as if the
            // value had been taken before the call, and if the value has moved on again by the re-read,
            // it is told to try again.
            var entry = db.Entry(size);
            entry.CurrentValues.SetValues(entry.OriginalValues);
            entry.State = EntityState.Unchanged;

            return (await FindTakenAsync(db, size, name, ordinal, ct) ?? ContendedMessage, true);
        }
    }

    // The name is asked first, matching the create path.
    private static async Task<string?> FindTakenAsync
    (
        BoardDbContext db,
        CardSize size,
        string? name,
        int? ordinal,
        CancellationToken ct
    )
    {
        if (name is not null && await db.CardSizes.AnyAsync(s => s.BoardId == size.BoardId && s.Name == name && s.Id != size.Id, ct))
        {
            return SizeCreateHelper.NameTakenMessage;
        }

        var ordinalTaken = ordinal is int newOrdinal
            && await db.CardSizes.AnyAsync(s => s.BoardId == size.BoardId && s.Ordinal == newOrdinal && s.Id != size.Id, ct);

        return ordinalTaken ? SizeCreateHelper.TakenMessage : null;
    }

    // SQLITE_CONSTRAINT_UNIQUE, from either the (BoardId, Name) or the (BoardId, Ordinal) index; the
    // re-read decides which.
    private static bool IsUniqueCollision(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };
}
