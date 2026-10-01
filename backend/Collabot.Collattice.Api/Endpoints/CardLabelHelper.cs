using Collabot.Collattice.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// Writes a card's label assignment, shared by the REST card-label endpoints and the MCP
// add_label_to_card / remove_label_from_card tools so the two front doors answer a lost race alike.
// The "already assigned?" read and the write are separate statements, so two callers adding (or
// removing) the same label at once both pass the read and the second write fails: a duplicate key
// on add, no row left to delete on remove. Measured before this helper, two simultaneous adds or
// removes of one label on one card returned a 500 on every second request. A caller that loses is
// answered exactly as if it had arrived after the winner: already assigned, or not assigned.
internal static class CardLabelHelper
{
    // Null when the label was already on the card, before the call or by the time it saved.
    public static async Task<CardLabel?> AssignAsync(BoardDbContext db, Guid cardId, Guid labelId, CancellationToken ct = default)
    {
        if (await db.CardLabels.AnyAsync(x => x.CardId == cardId && x.LabelId == labelId, ct))
        {
            return null;
        }

        var cardLabel = new CardLabel { CardId = cardId, LabelId = labelId };
        db.CardLabels.Add(cardLabel);

        try
        {
            await db.SaveChangesAsync(ct);
            return cardLabel;
        }
        catch (DbUpdateException ex) when (IsDuplicateAssignment(ex))
        {
            db.Entry(cardLabel).State = EntityState.Detached;
            return null;
        }
    }

    // False when the assignment was already gone by the time it saved.
    public static async Task<bool> UnassignAsync(BoardDbContext db, CardLabel cardLabel, CancellationToken ct = default)
    {
        db.CardLabels.Remove(cardLabel);

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The delete matched no row: a concurrent remove got there first.
            db.Entry(cardLabel).State = EntityState.Detached;
            return false;
        }
    }

    // SQLITE_CONSTRAINT_PRIMARYKEY: the assignment's key is (CardId, LabelId). A foreign-key failure,
    // such as the card being deleted meanwhile, carries a different code and is not treated as a
    // duplicate.
    private static bool IsDuplicateAssignment(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 };
}
