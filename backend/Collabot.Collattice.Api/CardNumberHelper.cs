using Collabot.Collattice.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api;

internal static class CardNumberHelper
{
    // Attempts before giving up on a board-scoped card-number collision. Both allocation surfaces
    // (insert and finalize) share the count — they contend over the same (BoardId, Number) index.
    // Eight immediate retries, with no pause between them, and the absence of a pause is deliberate:
    // a card number is max+1 per board, so a loser re-reads the max and takes the next free number.
    // A random pause between attempts was measured to make this dramatically worse. Measured on the
    // running allocator with writers released together on one board: three immediate retries lost
    // roughly a tenth of creations through thirty-two-way, a five-with-pause shape lost up to two in
    // five, and eight immediate retries lost none through thirty-two-way. Through the full create
    // endpoint, with the rest of a request around each attempt, a few still run out: 5 of 320 at
    // thirty-two at once with SQLite's busy timeout set on every connection. The likely reason a
    // pause hurts is that while every loser sleeps the max stops advancing, and the narrow pause
    // window wakes them in re-colliding clusters. That is an explanation, not a rule: the
    // size-create allocator has the same max+1 shape and measured a pause helping or hurting
    // depending on how a held lock is waited on, so each allocator's shape follows its own
    // measurement. The description-history allocator was measured the same way and also retries
    // immediately.
    private const int _maxRetries = 8;

    public static async Task InsertCardWithAutoNumberAsync
    (
        BoardDbContext db,
        CardItem card,
        Guid boardId,
        CancellationToken ct = default
    )
    {
        for (var attempt = 1; attempt < _maxRetries; attempt++)
        {
            card.Number = await NextNumberAsync(db, boardId, ct);

            db.Cards.Add(card);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                db.Entry(card).State = EntityState.Detached;
            }
        }

        // The last attempt runs outside the catch, so exhausted retries surface the collision
        // itself to the caller: the database's own error, naming the index that was contended.
        card.Number = await NextNumberAsync(db, boardId, ct);

        db.Cards.Add(card);
        await db.SaveChangesAsync(ct);
    }

    // Assigns a board-scoped card number to an existing temp card and clears the IsTemp
    // flag, retrying on unique-constraint collisions (SQLite error 19). The caller sets
    // LastUpdatedAtUtc / LastUpdatedByUserId before calling, and every attempt saves them
    // along with the number. If the retries are exhausted the collision surfaces to the caller.
    public static async Task FinalizeCardNumberAsync
    (
        BoardDbContext db,
        CardItem card,
        Guid boardId,
        CancellationToken ct = default
    )
    {
        card.IsTemp = false;

        for (var attempt = 1; attempt < _maxRetries; attempt++)
        {
            card.Number = await NextNumberAsync(db, boardId, ct);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Nothing to undo before the next attempt: a failed save leaves the card's pending
                // changes tracked, so the retry only claims a new number. Reloading the card here
                // would also discard the caller's last-updated stamp, leaving a draft finalized under
                // contention dated from when it was started and invisible to an activity poll.
            }
        }

        // The last attempt runs outside the catch, as in InsertCardWithAutoNumberAsync.
        card.Number = await NextNumberAsync(db, boardId, ct);

        await db.SaveChangesAsync(ct);
    }

    private static async Task<long> NextNumberAsync(BoardDbContext db, Guid boardId, CancellationToken ct) =>
        (await db.Cards
            .Where(c => c.BoardId == boardId && c.Number > 0)
                .MaxAsync(c => (long?)c.Number, ct) ?? 0) + 1;

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: 19 };
}
