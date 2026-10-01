using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api;

internal static class CardNumberHelper
{
    // Every entry point that numbers a card answers a false return with this, as a 409 on REST and
    // an error on MCP, the same "try again" the lane-position and size-create allocators give. Losing
    // the race for a number is an expected outcome of many creates on one board at once, not a fault.
    public const string ContendedMessage = "Other cards were being created on this board at the same time; try again.";

    // Attempts before giving up on a board-scoped card-number collision. Both allocation surfaces
    // (insert and finalize) share the count — they contend over the same (BoardId, Number) index.
    // Eight immediate retries, with no pause between them, and the absence of a pause is deliberate:
    // a card number is max+1 per board, so a loser re-reads the max and takes the next free number.
    // A random pause between attempts was measured to make this dramatically worse.
    //
    // The number is read before the save takes the write lock, so an attempt whose save has to wait
    // for the lock almost always collides: whoever held it was committing a card. Measured through
    // the running app with 32 creates on one board at once: an attempt whose save waited 10 ms or
    // more collided 2,018 times in 2,023, one that did not wait collided 77 times in 663.
    // So under heavy contention a create succeeds when it happens to reach the lock while nobody
    // holds it, and eight attempts is a budget, not a guarantee. Eight ran out for 15 to 24 of 320
    // creates at 32 at once through the create endpoint and for 8 to 37 of 320 calling this
    // allocator directly in the same app, for 6 of 320 at 64 at once, and for none of 160 at 16.
    // Sixteen attempts still ran out for 3 of 320 at 64. A create that runs out gets
    // ContendedMessage and can retry.
    private const int _maxAttempts = 8;

    // Returns false, with nothing saved, when every attempt lost the race for a number.
    public static async Task<bool> TryInsertCardWithAutoNumberAsync
    (
        BoardDbContext db,
        CardItem card,
        Guid boardId,
        CancellationToken ct = default
    )
    {
        using var expected = ExpectedSaveFailure.Expect(IsUniqueConstraintViolation);

        for (var attempt = 0; attempt < _maxAttempts; attempt++)
        {
            card.Number = await NextNumberAsync(db, boardId, ct);

            db.Cards.Add(card);
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                db.Entry(card).State = EntityState.Detached;
            }
        }

        return false;
    }

    // Assigns a board-scoped card number to an existing temp card and clears the IsTemp
    // flag, retrying on unique-constraint collisions. The caller sets
    // LastUpdatedAtUtc / LastUpdatedByUserId before calling, and every attempt saves them
    // along with the number. Returns false, with nothing saved and the draft still a draft, when
    // every attempt lost the race for a number.
    public static async Task<bool> TryFinalizeCardNumberAsync
    (
        BoardDbContext db,
        CardItem card,
        Guid boardId,
        CancellationToken ct = default
    )
    {
        card.IsTemp = false;

        using var expected = ExpectedSaveFailure.Expect(IsUniqueConstraintViolation);

        for (var attempt = 0; attempt < _maxAttempts; attempt++)
        {
            card.Number = await NextNumberAsync(db, boardId, ct);
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Nothing to undo before the next attempt: a failed save leaves the card's pending
                // changes tracked, so the retry only claims a new number. Reloading the card here
                // would also discard the caller's last-updated stamp, leaving a draft finalized under
                // contention dated from when it was started and invisible to an activity poll.
            }
        }

        return false;
    }

    private static async Task<long> NextNumberAsync(BoardDbContext db, Guid boardId, CancellationToken ct) =>
        (await db.Cards
            .Where(c => c.BoardId == boardId && c.Number > 0)
                .MaxAsync(c => (long?)c.Number, ct) ?? 0) + 1;

    // SQLITE_CONSTRAINT_UNIQUE only. A foreign-key failure shares SQLite's primary code 19, but it
    // means the lane or size the card refers to was deleted under the save: retrying cannot fix that,
    // and answering it as contention would tell the caller the wrong thing. It reaches the caller as
    // itself, where the shared concurrent-delete answer handles it.
    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };
}
