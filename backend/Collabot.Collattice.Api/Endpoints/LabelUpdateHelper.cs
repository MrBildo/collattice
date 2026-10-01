using Collabot.Collattice.Api.Models;
using Collabot.Collattice.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// Applies a label's new name and color, shared by the REST endpoint (PATCH
// /boards/{boardId}/labels/{id}) and the MCP tool (update_label) so the two front doors cannot drift.
// Label names are unique per board, so a name another label holds is answered here as a conflict
// instead of surfacing as an unhandled save failure (a 500 on REST).
internal static class LabelUpdateHelper
{
    // The create paths (POST /boards/{boardId}/labels, create_label) answer a taken name with this
    // same message, so create and rename read alike.
    public const string NameTakenMessage = "A label with that name already exists on this board.";
    public const string ContendedMessage = "Another change to this board's labels landed at the same time; try again.";

    // Null Error means the label was saved. IsConflict marks the answers REST gives as 409.
    public static async Task<(string? Error, bool IsConflict)> UpdateAsync
    (
        BoardDbContext db,
        Label label,
        string? name,
        string? color,
        CancellationToken ct = default
    )
    {
        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            return ("Name cannot be empty.", false);
        }

        if (await IsNameTakenAsync(db, label, name, ct))
        {
            return (NameTakenMessage, true);
        }

        if (name is not null)
        {
            label.Name = name;
        }

        if (color is not null)
        {
            label.Color = color;
        }

        using var expected = ExpectedSaveFailure.Expect(IsUniqueCollision);

        try
        {
            await db.SaveChangesAsync(ct);
            return (null, false);
        }
        catch (DbUpdateException ex) when (IsUniqueCollision(ex))
        {
            // The check and the save are separate statements, so a concurrent create or rename can take
            // the name in between: measured before this helper, 2 to 8 simultaneous renames of different
            // labels to one name returned 500 on all but one. The loser is answered as if the name had
            // been taken before the call, and if it has moved on again by the re-read, it is told to try
            // again.
            var entry = db.Entry(label);
            entry.CurrentValues.SetValues(entry.OriginalValues);
            entry.State = EntityState.Unchanged;

            return (await IsNameTakenAsync(db, label, name, ct) ? NameTakenMessage : ContendedMessage, true);
        }
    }

    private static async Task<bool> IsNameTakenAsync(BoardDbContext db, Label label, string? name, CancellationToken ct) =>
        name is not null && await db.Labels.AnyAsync(l => l.BoardId == label.BoardId && l.Name == name && l.Id != label.Id, ct);

    // SQLITE_CONSTRAINT_UNIQUE; the label's only unique index is (BoardId, Name).
    private static bool IsUniqueCollision(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };
}
