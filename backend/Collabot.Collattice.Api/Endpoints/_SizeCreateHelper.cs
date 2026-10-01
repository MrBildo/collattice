using Collabot.Collattice.Api.Models;
using Collabot.Collattice.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// Creates a card size and resolves the ordinal it takes, shared by the REST endpoint
// (POST /boards/{boardId}/sizes) and the MCP tool (create_size) so the two front doors
// cannot drift. Every rejection is decided here because the unique (BoardId, Ordinal) and
// (BoardId, Name) indexes would otherwise surface a taken ordinal or name as an unhandled save
// failure (a 500 on REST).
internal static class SizeCreateHelper
{
    // The update paths (PATCH /sizes/{id}, update_size) answer a taken ordinal with this same
    // message, so create and update read alike.
    public const string TakenMessage = "Ordinal already taken by another size.";
    public const string NameTakenMessage = "A size with that name already exists on this board.";
    public const string NoRoomMessage = "No ordinal is free after the board's highest size; pass an explicit ordinal.";
    public const string ContendedMessage = "Other sizes were being created on this board at the same time; try again.";

    // Attempts for an omitted ordinal. The read of the highest ordinal and the insert are separate
    // statements, so concurrent creates on one board resolve the same ordinal and all but one lose the
    // unique index: measured before this helper, 8 simultaneous creates on one board returned 500 on
    // 34 of 40 requests. A loser re-reads and takes the next ordinal. Each collision means a different
    // create committed after this one's read, so among N simultaneous creates one collides at most
    // N - 1 times: eight attempts cover eight at once on one board. Past that a loser can run out and
    // gets ContendedMessage, a 409 it can retry.
    //
    // Measured on this allocator: eight immediate attempts gave no 409 up to 8 simultaneous creates,
    // and 3% to 18% of creates got one at 32 at once, depending on load. Sixteen immediate attempts
    // gave none at 32. Eight with a 2-14 ms pause gave about a quarter as many while connections had
    // no busy timeout, and more rather than fewer once every connection had one (45 and 54 of 480 at
    // 32 at once, against 21 to 47 for eight immediate). Eight immediate is kept to match the
    // lane-position and card-number allocators, since no realistic workload creates more than eight
    // sizes on one board at once. Whether a pause helps here depends on how a held lock is waited on.
    private const int _maxAttempts = 8;

    public static async Task<SizeCreateResult> CreateAsync
    (
        BoardDbContext db,
        Guid boardId,
        string name,
        int? requestedOrdinal,
        CancellationToken ct = default
    )
    {
        using var expected = ExpectedSaveFailure.Expect(IsUniqueCollision);

        for (var attempt = 0; attempt < _maxAttempts; attempt++)
        {
            // Asked on every attempt, not once before the loop: a collision can also be a concurrent
            // create that took this name, and only a fresh read tells the two indexes apart.
            if (await db.CardSizes.AnyAsync(s => s.BoardId == boardId && s.Name == name, ct))
            {
                return SizeCreateResult.Rejected(NameTakenMessage, isConflict: true);
            }

            var (ordinal, error, isConflict) = await ResolveAsync(db, boardId, requestedOrdinal, ct);
            if (error is not null)
            {
                return SizeCreateResult.Rejected(error, isConflict);
            }

            var size = new CardSize { Id = Guid.NewGuid(), BoardId = boardId, Name = name, Ordinal = ordinal };
            db.CardSizes.Add(size);

            try
            {
                await db.SaveChangesAsync(ct);
                return SizeCreateResult.Created(size);
            }
            catch (DbUpdateException ex) when (IsUniqueCollision(ex))
            {
                // Re-resolve from the committed state. An appended ordinal moves to the next free
                // one; an explicit one now fails the taken check, and a taken name fails the name
                // check, so each answers 409 as if it had been taken before the call.
                db.Entry(size).State = EntityState.Detached;
            }
        }

        return SizeCreateResult.Rejected(ContendedMessage, isConflict: true);
    }

    // An omitted ordinal goes one past the board's highest, so a new size sorts last. A board with
    // no sizes starts at 0.
    private static async Task<(int Ordinal, string? Error, bool IsConflict)> ResolveAsync
    (
        BoardDbContext db,
        Guid boardId,
        int? requested,
        CancellationToken ct
    )
    {
        if (requested is int ordinal)
        {
            var taken = await db.CardSizes.AnyAsync(s => s.BoardId == boardId && s.Ordinal == ordinal, ct);

            return taken ? (0, TakenMessage, true) : (ordinal, null, false);
        }

        var highest = await db.CardSizes
            .Where(s => s.BoardId == boardId)
                .MaxAsync(s => (int?)s.Ordinal, ct);

        if (highest is null)
        {
            return (0, null, false);
        }

        // One past int.MaxValue would wrap to int.MinValue and sort the new size first.
        return highest.Value == int.MaxValue
            ? (0, NoRoomMessage, false)
            : (highest.Value + 1, null, false);
    }

    // SQLITE_CONSTRAINT_UNIQUE, from either of the two unique indexes; the re-read on the next
    // attempt decides which one it was. A foreign-key failure carries a different extended code and
    // is not retried.
    private static bool IsUniqueCollision(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };
}

// IsConflict lets REST answer with 409 Conflict, the same status the size update endpoint uses for
// a taken ordinal. MCP has no status and reads only Error.
internal sealed record SizeCreateResult(CardSize? Size, string? Error, bool IsConflict)
{
    public static SizeCreateResult Created(CardSize size) => new(size, null, false);

    public static SizeCreateResult Rejected(string error, bool isConflict) => new(null, error, isConflict);
}
