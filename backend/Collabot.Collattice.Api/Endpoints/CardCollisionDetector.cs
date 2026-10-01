using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// Decides whether a card write landed on top of another user's edit, and who to name for it. Shared
// by the two write paths — the REST PATCH /cards/{id} handler and the MCP update_card tool — so the
// two surfaces answer the question identically. Field-general by construction: it is handed the field
// to check and reads that field's history, so lighting up a second field later is a caller change, not
// a change here. The description is the only field with a trail today, so it is the only field a
// caller can ask about — but nothing in this file says so.
//
// Awareness only. Nothing here refuses or alters a save; it reports what the write overlapped with and
// leaves last-write-wins exactly as it was.
internal static class CardCollisionDetector
{
    public const string ExactKind = "exact";
    public const string ApproximateKind = "approximate";

    // The approximate signal fires only when the prior edit is at least this recent. Deliberately
    // short: it is meant to say "someone was working this card at the same time as you," not to flag
    // ordinary sequential editing where one person picks up where another left off minutes later. Too
    // long and it cries collision on routine hand-offs and the signal stops meaning anything; this
    // errs toward the quiet side, since a caller who wants certainty passes a baseline and gets the
    // exact answer instead. Exact detection carries no window at all — a baseline pins the version the
    // caller read, so an intervening edit is a real overwrite however long ago it happened.
    public static readonly TimeSpan ApproximateWindow = TimeSpan.FromSeconds(10);

    public static Task<CardCollision?> DetectAsync
    (
        BoardDbContext db,
        Guid cardId,
        string field,
        int? baselineRevision,
        Guid priorEditorId,
        DateTimeOffset priorEditedAtUtc,
        Guid actingUserId,
        CancellationToken ct = default
    ) =>
        baselineRevision.HasValue
            ? DetectExactAsync(db, cardId, field, baselineRevision.Value, actingUserId, ct)
            : DetectApproximateAsync(db, priorEditorId, priorEditedAtUtc, actingUserId, ct);

    // The caller passed the revision it had read. If the field has moved past that ordinal, an edit
    // landed between the caller's read and this write — a definite overwrite. The editor of the current
    // head is who this write landed on top of.
    private static async Task<CardCollision?> DetectExactAsync
    (
        BoardDbContext db,
        Guid cardId,
        string field,
        int baselineRevision,
        Guid actingUserId,
        CancellationToken ct
    )
    {
        var head = await CardHistoryHelper.HeadRevisionAsync(db, cardId, field, ct);
        var currentRevision = head?.Revision ?? 0;

        if (currentRevision <= baselineRevision)
        {
            return null;
        }

        // The head is the caller's own earlier edit, not someone else's, so this write overwrote
        // nobody and there is nothing to report. This is reachable, contrary to how it might read: a
        // caller that reads the card once and then makes several edits reusing that first baseline
        // sees the head move past the baseline (the exact check above clears) while the head editor is
        // still itself — a batching bot caching descriptionHistoryCount is exactly that caller. Without
        // this guard the detector would name the caller as the user it overwrote. The approximate path
        // applies the same self-exclusion on priorEditorId.
        if (head?.EditedByUserId == actingUserId)
        {
            return null;
        }

        var actor = await ResolveActorAsync(db, head?.EditedByUserId, ct);

        return actor is null
            ? null
            : new CardCollision(ExactKind, field, actor);
    }

    // No baseline was passed, so there is no version to compare against — only recency. If someone
    // other than the caller edited the card within the window just before this write, report it as a
    // best-effort overlap. Card-level on purpose: without a baseline the most that can be said is that
    // another editor was active on this card, not which field they touched, so the collision names no
    // field.
    private static async Task<CardCollision?> DetectApproximateAsync
    (
        BoardDbContext db,
        Guid priorEditorId,
        DateTimeOffset priorEditedAtUtc,
        Guid actingUserId,
        CancellationToken ct
    )
    {
        if (priorEditorId == actingUserId)
        {
            return null;
        }

        if (DateTimeOffset.UtcNow - priorEditedAtUtc > ApproximateWindow)
        {
            return null;
        }

        var actor = await ResolveActorAsync(db, priorEditorId, ct);

        return actor is null
            ? null
            : new CardCollision(ApproximateKind, null, actor);
    }

    private static async Task<CardCollisionActor?> ResolveActorAsync(BoardDbContext db, Guid? userId, CancellationToken ct)
    {
        if (userId is null)
        {
            return null;
        }

        var name = await db.Users
            .Where(u => u.Id == userId.Value)
                .Select(u => u.Name)
                    .FirstOrDefaultAsync(ct);

        return name is null
            ? null
            : new CardCollisionActor(userId.Value, name);
    }
}
