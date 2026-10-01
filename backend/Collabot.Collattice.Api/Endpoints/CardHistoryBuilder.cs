using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// Read projection for card field history, shared by REST GET /cards/{id}/history and the MCP
// get_card_history tool so the two surfaces return the identical shape by construction.
//
// Diffs are computed here on read, never stored. Rows hold whole values, so any pair of revisions
// can be compared (which the from/to read needs) and there is no cached representation to fall out
// of step with the values it describes. Card descriptions are small and the trail is short.
internal static class CardHistoryBuilder
{
    public const string FormatError = "format must be one of: diff, full, both.";

    private static readonly FrozenDictionary<string, CardHistoryFormat> _formatNames =
        new Dictionary<string, CardHistoryFormat>(StringComparer.OrdinalIgnoreCase)
        {
            ["diff"] = CardHistoryFormat.Diff,
            ["full"] = CardHistoryFormat.Full,
            ["both"] = CardHistoryFormat.Both,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Paged newest-first, because that is the order a reader walks: page 0 is the most recent
    // revisions. A null limit returns the whole trail from the offset down, which is what a caller
    // that passes no paging parameters gets.
    public static async Task<CardHistoryResult> BuildTrailAsync
    (
        BoardDbContext db,
        Guid cardId,
        string field,
        CardHistoryFormat format,
        int offset,
        int? limit,
        CancellationToken ct = default
    )
    {
        var totalCount = await CardHistoryHelper.CountRevisionsAsync(db, cardId, field, ct);

        var pageQuery = db.CardFieldHistories
            .Where(h => h.CardId == cardId && h.Field == field)
            .OrderByDescending(h => h.Revision)
            .Skip(offset);

        // One row past the page when a limit truncates the tail: the oldest entry on the page
        // needs its predecessor's value to render a diff. Without it that entry would come back
        // with an empty diff and masquerade as the trail's genuinely un-diffable first revision.
        if (limit.HasValue)
        {
            pageQuery = pageQuery.Take(limit.Value + 1);
        }

        var rows = await pageQuery.ToListAsync(ct);

        CardFieldHistory? predecessor = null;
        if (limit.HasValue && rows.Count > limit.Value)
        {
            predecessor = rows[limit.Value];
            rows.RemoveAt(limit.Value);
        }

        var editorNames = await ResolveEditorNamesAsync(db, rows, ct);

        var inferredEditor = await ResolveInferredEditorAsync(db, cardId, rows, ct);

        List<CardHistoryEntry> entries = [];

        for (var index = 0; index < rows.Count; index++)
        {
            // The row one step older than this one: the next in newest-first order, or the extra
            // row fetched past the page boundary. Null only for the trail's true first revision,
            // which has nothing older to diff against.
            var older = index + 1 < rows.Count ? rows[index + 1] : predecessor;

            entries.Add(BuildEntry(rows[index], older?.Value, editorNames, inferredEditor, format));
        }

        return new CardHistoryResult(cardId, field, entries, totalCount, offset, limit);
    }

    public static async Task<(CardHistoryPairResult? Result, string? Error)> BuildPairAsync
    (
        BoardDbContext db,
        Guid cardId,
        string field,
        CardHistoryFormat format,
        int from,
        int to,
        CancellationToken ct = default
    )
    {
        var rows = await db.CardFieldHistories
            .Where(h => h.CardId == cardId && h.Field == field && (h.Revision == from || h.Revision == to))
                .ToListAsync(ct);

        var fromRow = rows.FirstOrDefault(r => r.Revision == from);
        if (fromRow is null)
        {
            return (null, MissingRevisionError(from, field));
        }

        var toRow = rows.FirstOrDefault(r => r.Revision == to);
        if (toRow is null)
        {
            return (null, MissingRevisionError(to, field));
        }

        // from and to are compared in the order given, so asking for a later-to-earlier pair
        // yields the diff that would undo the change rather than an error.
        var diff = IncludesDiff(format) ? UnifiedDiff.Render(fromRow.Value, toRow.Value) : null;
        var fromValue = IncludesValue(format) ? fromRow.Value : null;
        var toValue = IncludesValue(format) ? toRow.Value : null;

        return (new CardHistoryPairResult(cardId, field, from, to, diff, fromValue, toValue), null);
    }

    public static bool TryParseFormat(string? requested, CardHistoryFormat fallback, out CardHistoryFormat format)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            format = fallback;
            return true;
        }

        if (_formatNames.TryGetValue(requested.Trim(), out var parsed))
        {
            format = parsed;
            return true;
        }

        format = fallback;
        return false;
    }

    private static CardHistoryEntry BuildEntry
    (
        CardFieldHistory row,
        string? previousValue,
        IReadOnlyDictionary<Guid, string> editorNames,
        CardHistoryInferredEditor? inferredEditor,
        CardHistoryFormat format
    )
    {
        string? editorName = null;
        if (row.EditedByUserId is not null)
        {
            editorName = editorNames.GetValueOrDefault(row.EditedByUserId.Value);
        }

        // The inference belongs only to a revision nobody observed being written. An observed
        // revision keeps its own editor and carries no inferred one, so its entry reads exactly as
        // it did before the inference existed.
        var entryInferredEditor = row.EditedByUserId is null ? inferredEditor : null;

        string? value = null;
        if (IncludesValue(format))
        {
            value = row.Value;
        }

        string? diff = null;
        if (IncludesDiff(format))
        {
            diff = previousValue is null ? string.Empty : UnifiedDiff.Render(previousValue, row.Value);
        }

        return new CardHistoryEntry
        (
            row.Revision,
            row.EditedByUserId,
            editorName,
            row.EditedAtUtc,
            entryInferredEditor,
            value,
            diff
        );
    }

    private static async Task<Dictionary<Guid, string>> ResolveEditorNamesAsync
    (
        BoardDbContext db,
        List<CardFieldHistory> rows,
        CancellationToken ct
    )
    {
        var editorIds = rows
            .Where(r => r.EditedByUserId is not null)
                .Select(r => r.EditedByUserId!.Value)
                .Distinct()
                    .ToList();

        // A trail whose only row is the un-attributed oldest revision needs no user lookup at all.
        return editorIds.Count == 0
            ? []
            : await db.Users
                .Where(u => editorIds.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, u => u.Name, ct);
    }

    // The trail's oldest revision holds the text as it stood when recording began, and its stored
    // author stays null because nobody observed that text being written. Readers still want a name
    // there, and the card's creator is the best one available — but only as an inference: a card
    // created before history was recorded may have been edited by someone else before recording
    // began. So the creator is resolved here, on read, into a field of its own that says it is
    // inferred and on what basis, and the stored row is never written to. The record keeps saying
    // only what was observed, and an attribution rule that is later refined changes one read
    // rather than rewriting history rows.
    //
    // No inferred timestamp is offered. The card's creation time is exactly right for a card
    // created after recording began and wrong for an older card edited before recording began,
    // which are the cards the inference exists for. editedAtUtc stays null, and the card's own
    // creation time is on the card for anyone who wants it.
    private static async Task<CardHistoryInferredEditor?> ResolveInferredEditorAsync
    (
        BoardDbContext db,
        Guid cardId,
        List<CardFieldHistory> rows,
        CancellationToken ct
    )
    {
        // Only the oldest revision is ever un-attributed, so a page that stops short of it needs
        // no lookup.
        if (!rows.Any(r => r.EditedByUserId is null))
        {
            return null;
        }

        var creator = await db.Cards
            .Where(c => c.Id == cardId)
            .Join(db.Users, c => c.CreatedByUserId, u => u.Id, (c, u) => new { u.Id, u.Name })
                .SingleOrDefaultAsync(ct);

        return creator is null
            ? null
            : new CardHistoryInferredEditor(creator.Id, creator.Name, CardHistoryInferredEditor.CreatorBasis);
    }

    private static string MissingRevisionError(int revision, string field) =>
        string.Create(CultureInfo.InvariantCulture, $"Revision {revision} not found in this card's {field} history.");

    private static bool IncludesValue(CardHistoryFormat format) =>
        format is CardHistoryFormat.Full or CardHistoryFormat.Both;

    private static bool IncludesDiff(CardHistoryFormat format) =>
        format is CardHistoryFormat.Diff or CardHistoryFormat.Both;
}

internal enum CardHistoryFormat
{
    Diff,
    Full,
    Both,
}

// One revision in a card field's trail. Value and Diff drop off the wire entirely when the caller's
// format did not ask for them, so format=diff (the MCP default) carries no null padding. The
// observed attribution fields stay on the wire even when null — "nobody saw who wrote this" is
// information a reader needs, and only the trail's oldest revision carries it. InferredEditor rides
// beside them on that revision alone and is absent from every other entry, so an observed author
// and an inferred one can never be mistaken for each other by a reader that looks at the shape.
internal record CardHistoryEntry
(
    int Revision,
    Guid? EditedByUserId,
    string? EditedByName,
    DateTimeOffset? EditedAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CardHistoryInferredEditor? InferredEditor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Diff
);

// The best available attribution for a revision nobody observed being written. Basis names how it
// was inferred, so a reader can say so ("card creator") rather than present the name as observed.
internal record CardHistoryInferredEditor(Guid UserId, string Name, string Basis)
{
    public const string CreatorBasis = "creator";
}

// Entries carries the requested page; TotalCount is the whole trail's length regardless of paging,
// so a consumer can tell "this is all of it" from "there is more above or below". The collection
// keeps the name it shipped with rather than the paged envelope's "items" — the paging vocabulary
// is what the rest of the API shares, and renaming the collection would break every reader for it.
internal record CardHistoryResult
(
    Guid CardId,
    string Field,
    List<CardHistoryEntry> Entries,
    int TotalCount,
    int Offset,
    int? Limit
);

internal record CardHistoryPairResult
(
    Guid CardId,
    string Field,
    int From,
    int To,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Diff,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FromValue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ToValue
);
