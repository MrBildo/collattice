using Collabot.Collattice.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api;

internal static class CardNumberHelper
{
    // Attempts before giving up on a board-scoped card-number collision. Both allocation surfaces
    // (insert and finalize) share the count — they contend over the same (BoardId, Number) index.
    // Eight immediate retries, with no pause between them, and the absence of a pause is deliberate:
    // a card number is max+1 per board, so a loser re-reads the max and takes the next free number,
    // and retrying at once keeps a tight pipeline through SQLite's single writer while the max
    // advances continuously as winners commit. A random pause between attempts was measured to make
    // this dramatically worse: while every loser sleeps the max stops advancing, and the narrow pause
    // window wakes them in re-colliding clusters. Measured on the running allocator with writers
    // released together on one board: three immediate retries lost roughly a tenth of creations
    // through thirty-two-way, a five-with-pause shape lost up to two in five, and eight immediate
    // retries lost none through thirty-two-way. The description-history allocator has the same max+1
    // shape, was measured the same way, and retries the same way.
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
    // flag, retrying on unique-constraint collisions (SQLite error 19). The caller is
    // responsible for setting LastUpdatedAtUtc / LastUpdatedByUserId before calling.
    // If the retries are exhausted the collision surfaces to the caller.
    public static async Task FinalizeCardNumberAsync
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
            card.IsTemp = false;
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                await db.Entry(card).ReloadAsync(ct);
            }
        }

        // The last attempt runs outside the catch, as in InsertCardWithAutoNumberAsync.
        card.Number = await NextNumberAsync(db, boardId, ct);
        card.IsTemp = false;

        await db.SaveChangesAsync(ct);
    }

    private static async Task<long> NextNumberAsync(BoardDbContext db, Guid boardId, CancellationToken ct) =>
        (await db.Cards
            .Where(c => c.BoardId == boardId && c.Number > 0)
                .MaxAsync(c => (long?)c.Number, ct) ?? 0) + 1;

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: 19 };
}
