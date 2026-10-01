using Collabot.Collattice.Api.Models;
using Collabot.Collattice.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Collabot.Collattice.Api.Endpoints;

// The write-path capture seam for card field history. Both description write paths — the REST
// PATCH /cards/{id} handler and the MCP update_card tool — stage through here, so neither surface
// can drift from the other on what gets recorded (the parallel-surface drift this codebase's
// REST-and-MCP shape is most prone to).
//
// Staging only: rows are added to the change tracker and committed by TrySaveWithRevisionRetryAsync,
// which replaces the caller's SaveChangesAsync so the card mutation and its history entry land in
// one transaction. A card can never be left holding a new description with no record of the old one.
internal static class CardHistoryHelper
{
    public const string DescriptionField = "description";

    // Both description write paths answer a false return from TrySaveWithRevisionRetryAsync with
    // this, as a 409 on REST and an error on MCP: the same "try again" the card-number, lane and size
    // allocators give. Losing every attempt is an expected outcome of many edits to one description
    // at once, not a fault, and the caller's retry starts from the trail as it then stands.
    public const string ContendedMessage = "Other edits to this card's description were being saved at the same time; try again.";

    // The number of attempts TrySaveWithRevisionRetryAsync makes before giving up on a revision-ordinal
    // collision. The tests that drive the loop to its last attempt hold this number as their own
    // literal rather than reading it from here, so that lowering it reds them instead of quietly
    // moving them with it.
    private const int _maxRetryAttempts = 8;

    // The store is field-general, but only description is captured today. An unrecognised field is
    // rejected rather than answered with an empty trail: on an audit surface, a typo that reads as
    // "this card has no history" is worse than an error.
    private static readonly string[] _supportedFields = [DescriptionField];

    public static (string? Field, string? Error) ResolveField(string? requestedField)
    {
        if (string.IsNullOrWhiteSpace(requestedField))
        {
            return (DescriptionField, null);
        }

        var match = _supportedFields.FirstOrDefault(f => string.Equals(f, requestedField, StringComparison.OrdinalIgnoreCase));

        return match is not null
            ? (match, null)
            : (null, $"Unknown field '{requestedField}'. Supported fields: {string.Join(", ", _supportedFields)}.");
    }

    // One definition of "how many revisions does this card's field hold". The card detail's count
    // and the trail read's totalCount are the same number reported on two surfaces, and a reader
    // who sees them disagree has no way to tell which one lied — so they resolve through here
    // rather than each carrying its own copy of the predicate.
    public static Task<int> CountRevisionsAsync
    (
        BoardDbContext db,
        Guid cardId,
        string field,
        CancellationToken ct = default
    ) =>
        db.CardFieldHistories
            .Where(h => h.CardId == cardId && h.Field == field)
                .CountAsync(ct);

    // The field's newest recorded revision — its ordinal and who wrote it — or null when the field
    // has no trail yet. Revisions are dense from one, so the ordinal is also the trail's length: a
    // caller that read the card's descriptionHistoryCount and passes it back can be told, by comparing
    // it against this ordinal, whether the field moved on since. Kept apart from the recording path's
    // own head read (which wants the head value, not its author) so the collision check adds no
    // coupling to the retry logic. The head of a non-empty trail is always an attributed revision —
    // a trail holds at least the seed and the first edit — so EditedByUserId is non-null whenever a
    // head exists.
    public static Task<HeadRevision?> HeadRevisionAsync
    (
        BoardDbContext db,
        Guid cardId,
        string field,
        CancellationToken ct = default
    ) =>
        db.CardFieldHistories
            .Where(h => h.CardId == cardId && h.Field == field)
            .OrderByDescending(h => h.Revision)
                .Select(h => new HeadRevision(h.Revision, h.EditedByUserId))
                    .FirstOrDefaultAsync(ct);

    // Returns the staged change so the caller can hand it to TrySaveWithRevisionRetryAsync, which
    // needs it to rebuild the rows if another editor wins the race for the revision number. Null
    // means nothing was staged and there is no revision to race for.
    public static async Task<StagedDescriptionChange?> StageDescriptionChangeAsync
    (
        BoardDbContext db,
        Guid cardId,
        string oldValue,
        string newValue,
        Guid editedByUserId,
        CancellationToken ct = default
    )
    {
        // A save that leaves the description exactly as it was is not an edit and records nothing —
        // otherwise a lane move or label change carrying an unchanged description in the same PATCH
        // would pad the trail with revisions that changed nothing. Answered here, before any query,
        // because most card saves are lane moves and this keeps them off the history tables
        // entirely. It is a question about the request, not about the trail: whether the row that
        // would be written differs from the one the trail already ends with is decided in
        // StageRowsAsync, which is the only place that can still be asked it correctly after a
        // retry.
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
        {
            return null;
        }

        var change = new StagedDescriptionChange(cardId, oldValue, newValue, editedByUserId);
        await StageRowsAsync(db, change, ct);

        return change;
    }

    // Replaces the caller's SaveChangesAsync on the description write paths. The revision ordinal
    // is allocated as max+1 and enforced unique, so two edits of one card that read the same max
    // before either saves collide on insert. The window is brief in wall-clock terms and easy to
    // underestimate for exactly that reason — measured under sustained eight-way concurrent editing
    // of one description with the writers released together, seven of every eight requests hit it.
    // Before history existed those same saves all simply succeeded, with the last one winning.
    //
    // The loser retries rather than being told to reload: the revision number is internal
    // bookkeeping, the caller's intent (set this description) is still fully satisfiable, and the
    // resulting trail records both edits in the order they committed. Reporting a conflict instead
    // would be lost-update protection this product has never had, fired only when two edits land
    // within microseconds of each other while two edits seconds apart still overwrite silently.
    //
    // Returns false, with nothing saved, when every attempt lost the race for a revision.
    public static async Task<bool> TrySaveWithRevisionRetryAsync
    (
        BoardDbContext db,
        StagedDescriptionChange? change,
        CancellationToken ct = default
    )
    {
        if (change is null)
        {
            await db.SaveChangesAsync(ct);
            return true;
        }

        using var expected = ExpectedSaveFailure.Expect(IsRevisionCollision);

        // Eight attempts, retried at once with no pause between them: the card-number allocator's
        // shape. Measured on this helper with writers released together on one card, 8- to
        // 128-way, a fresh card per round, 9,600 edits per shape: five attempts with a random 2 to
        // 14 ms pause lost about one edit in forty, five immediate attempts about one in six
        // hundred, and eight immediate attempts none. Only at 128-way did an edit ever need all
        // eight; at eight-way none needed more than four. Re-measured with SQLite's busy timeout set
        // on every connection, 16- to 64-way, 1,344 edits per shape: eight immediate attempts and
        // five immediate attempts still lost none, and the pause still lost 18. A held write lock is
        // then waited on inside SQLite rather than in the provider's fixed 150 ms sleep, so that
        // sleep, the earlier explanation, is not why a pause hurts here; why it does is not
        // established. Each allocator's retry shape follows its own measurement.
        for (var attempt = 1; attempt <= _maxRetryAttempts; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException ex) when (IsRevisionCollision(ex))
            {
                // After the last attempt there is nothing to rebuild for; the caller answers with
                // ContendedMessage.
                if (attempt == _maxRetryAttempts)
                {
                    break;
                }

                // Rebuild the rows against the trail's new head rather than renumbering the ones
                // already staged: the winning edit has by now written the seed row holding the
                // pre-history value, and re-adding a second copy of it at a later revision would
                // make the trail read as though the description had reverted. Rebuilding also
                // re-asks whether there is anything left to record at all, which is the question
                // the winner's commit may just have changed the answer to.
                DetachStagedRows(db, change);
                await StageRowsAsync(db, change, ct);
            }
        }

        return false;
    }

    private static async Task StageRowsAsync(BoardDbContext db, StagedDescriptionChange change, CancellationToken ct)
    {
        var head = await db.CardFieldHistories
            .Where(h => h.CardId == change.CardId && h.Field == DescriptionField)
            .OrderByDescending(h => h.Revision)
                .Select(h => new { h.Revision, h.Value })
                    .FirstOrDefaultAsync(ct);

        // Stamped from the same moment the ordinal is derived from, which is what makes revision
        // order and time order agree instead of merely tend to. A row numbered above another was
        // staged by a read that had already seen that other one committed, so its clock reading is
        // necessarily the later of the two. Taking the stamp at the start of the request instead
        // breaks that: a request can arrive early, wait out a collision, and land a high revision
        // carrying an early instant — or arrive late, sail through uncontended, and land a high
        // revision carrying an instant earlier than the retried row beneath it. Both were measured.
        // Timestamps can still tie at clock resolution, so revision order stays the authority.
        var recordedAtUtc = DateTimeOffset.UtcNow;

        // First capture on this card seeds the value that was already in place. It is a real
        // revision (the trail's oldest), but nobody observed it being written — history is not
        // back-filled — so its author and time stay null rather than being attributed to the card's
        // creator or to whoever happened to trigger this first capture.
        if (head is null)
        {
            AddRevision(db, change.CardId, 1, change.OldValue, null, null);
            AddRevision(db, change.CardId, 2, change.NewValue, change.EditedByUserId, recordedAtUtc);

            return;
        }

        // Whether this row is worth writing is decided against the value the trail currently ends
        // with, and it is re-decided every time the rows are staged. On a first attempt that is the
        // same answer the caller's own unchanged-description check gave. On a retry it is not: a
        // rival edit committed in between, and if it set the description to the text this request
        // is also setting, then nothing is changing by the time this row would land. Writing it
        // anyway appends a revision whose diff is empty — and an empty diff on this trail is how a
        // reader is told "this is the oldest revision, there is nothing before it". The invariant
        // that keeps both readings true: a race leaves the same trail the same two edits would
        // leave arriving one after the other.
        if (string.Equals(head.Value, change.NewValue, StringComparison.Ordinal))
        {
            return;
        }

        AddRevision(db, change.CardId, head.Revision + 1, change.NewValue, change.EditedByUserId, recordedAtUtc);
    }

    private static void AddRevision
    (
        BoardDbContext db,
        Guid cardId,
        int revision,
        string value,
        Guid? editedByUserId,
        DateTimeOffset? editedAtUtc
    ) =>
        db.CardFieldHistories.Add(new CardFieldHistory
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            Field = DescriptionField,
            Revision = revision,
            Value = value,
            EditedByUserId = editedByUserId,
            EditedAtUtc = editedAtUtc,
        });

    // Only one failure is ours to retry: this request and another one both allocating the same
    // description revision. A unique-constraint violation anywhere else in the same save — a label
    // name, a card number, a lane position — has to reach the caller as itself rather than be
    // retried and rethrown as a revision problem it never was. The discrimination is on the rows
    // the failed statement was actually writing, which the exception carries; asking instead what
    // this request had staged answers yes every time, because a staged revision is the only reason
    // this method is running. The match is on the unique-constraint code itself, not SQLite's
    // primary constraint code it shares with a foreign-key failure: a card deleted under the edit
    // fails the history rows' foreign key, and retrying that only repeats the same failure eight
    // times before the caller hears it.
    private static bool IsRevisionCollision(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 }
        && ex.Entries.Any(e => e.Entity is CardFieldHistory);

    private static void DetachStagedRows(BoardDbContext db, StagedDescriptionChange change)
    {
        // Materialized before mutating: setting an entry's state edits the collection being walked.
        var staged = StagedRows(db, change)
            .ToList();

        foreach (var entry in staged)
        {
            entry.State = EntityState.Detached;
        }
    }

    private static IEnumerable<EntityEntry<CardFieldHistory>> StagedRows(BoardDbContext db, StagedDescriptionChange change) =>
        db.ChangeTracker
            .Entries<CardFieldHistory>()
            .Where(e => e.State == EntityState.Added
                        && e.Entity.CardId == change.CardId
                        && e.Entity.Field == DescriptionField);
}

// The description edit a request has staged but not yet committed. Carried from staging to save so
// the rows can be rebuilt from the trail's current head if the save loses a revision race. It holds
// no timestamp on purpose: when the revision was recorded is decided where the revision number is,
// so that the two cannot disagree about their order.
internal record StagedDescriptionChange
(
    Guid CardId,
    string OldValue,
    string NewValue,
    Guid EditedByUserId
);

// The newest revision recorded for a field: its ordinal — which, revisions being dense from one, is
// also the trail's length — and the user who wrote it. The oldest revision of a trail is
// un-attributed, but a head is never the oldest (a trail holds the seed and at least one edit), so a
// head that exists carries a non-null editor.
internal record HeadRevision(int Revision, Guid? EditedByUserId);
