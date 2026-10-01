using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Mcp;

internal static class McpCardResolver
{
    // Bulk card-ref resolution for the bulk tools.
    // Accepts the same CSV-XOR shape the bulk-tool contract specifies:
    //   cardIds (CSV of GUIDs)  XOR  cardNumbers (CSV) + boardId/boardSlug.
    // This is the Phase-1 pre-validation entry point — it fails loud with a
    // single error string (no per-card envelope) when any ref is malformed,
    // when the disjunction is violated, or when any referenced card does not
    // exist. On success it returns the cards in input order so the caller's
    // per-card result envelope aligns 1:1 with the requested order. Another
    // user's draft does not exist as far as the caller is concerned.
    public static async Task<(List<CardItem>? Cards, string? Error)> ResolveCardRefsAsync
    (
        BoardDbContext db,
        BoardUser caller,
        string? cardIds,
        string? cardNumbers,
        Guid? boardId,
        string? boardSlug,
        CancellationToken ct = default
    )
    {
        var hasIds = !string.IsNullOrWhiteSpace(cardIds);
        var hasNumbers = !string.IsNullOrWhiteSpace(cardNumbers);

        return (hasIds, hasNumbers) switch
        {
            (true, true) => (null, "Error: provide cardIds OR cardNumbers, not both."),
            (false, false) => (null, "Error: no card refs provided."),
            (true, _) => await ResolveByIdsAsync(db, caller, cardIds!, ct),
            _ => await ResolveByNumbersAsync(db, cardNumbers!, boardId, boardSlug, ct),
        };
    }

    private static async Task<(List<CardItem>? Cards, string? Error)> ResolveByIdsAsync
    (
        BoardDbContext db,
        BoardUser caller,
        string cardIds,
        CancellationToken ct
    )
    {
        var parts = cardIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        List<Guid> requestedIds = [];
        foreach (var part in parts)
        {
            if (!Guid.TryParse(part, out var parsed))
            {
                return (null, $"Error: Invalid card ID format: '{part}'. Expected a GUID.");
            }

            requestedIds.Add(parsed);
        }

        if (requestedIds.Count == 0)
        {
            return (null, "Error: no card refs provided.");
        }

        var found = await db.Cards
            .Where(c => requestedIds.Contains(c.Id))
                .ToListAsync(ct);

        var foundById = found
            .Where(c => !DraftVisibility.IsHiddenFrom(c, caller.Id))
                .ToDictionary(c => c.Id);
        var missing = requestedIds.Where(id => !foundById.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            return (null, $"Error: Cards not found: {string.Join(", ", missing)}");
        }

        // Preserve input order so per-card results align 1:1 with the request.
        var ordered = requestedIds.Select(id => foundById[id]).ToList();
        return (ordered, null);
    }

    private static async Task<(List<CardItem>? Cards, string? Error)> ResolveByNumbersAsync
    (
        BoardDbContext db,
        string cardNumbers,
        Guid? boardId,
        string? boardSlug,
        CancellationToken ct
    )
    {
        var (resolvedBoardId, boardError) = await ResolveBoardIdAsync(db, boardId, boardSlug, ct);
        if (boardError is not null)
        {
            return (null, boardError);
        }

        var parts = cardNumbers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        List<long> requestedNumbers = [];
        foreach (var part in parts)
        {
            if (!long.TryParse(part, out var parsed) || parsed <= 0)
            {
                return (null, $"Error: Invalid card number: '{part}'. Expected a positive integer.");
            }

            requestedNumbers.Add(parsed);
        }

        if (requestedNumbers.Count == 0)
        {
            return (null, "Error: no card refs provided.");
        }

        var found = await db.Cards
            .Where(c => c.BoardId == resolvedBoardId!.Value && requestedNumbers.Contains(c.Number))
                .ToListAsync(ct);

        var foundByNumber = found.ToDictionary(c => c.Number);
        var missing = requestedNumbers.Where(n => !foundByNumber.ContainsKey(n)).ToList();
        if (missing.Count > 0)
        {
            return (null, $"Error: Cards not found on this board: {string.Join(", ", missing.Select(n => $"#{n.ToString(CultureInfo.InvariantCulture)}"))}");
        }

        var ordered = requestedNumbers.Select(n => foundByNumber[n]).ToList();
        return (ordered, null);
    }

    // Where each single-card MCP tool turns its card reference into a card. The card is loaded
    // into the tool's context here, so the tool's own lookup of it afterwards costs nothing more.
    // Another user's draft resolves as not found. Every draft carries number 0, so a card number
    // below 1 names no card at all, for anyone, the draft's creator included.
    public static async Task<(Guid? CardId, string? Error)> ResolveCardIdAsync
    (
        BoardDbContext db,
        BoardUser caller,
        Guid? cardId,
        long? cardNumber,
        Guid? boardId = null,
        string? boardSlug = null,
        CancellationToken ct = default
    )
    {
        if (cardId.HasValue && cardNumber.HasValue)
        {
            return (null, "Error: Provide either cardId or cardNumber, not both.");
        }

        if (!cardId.HasValue && !cardNumber.HasValue)
        {
            return (null, "Error: Provide either cardId or cardNumber.");
        }

        if (cardId.HasValue)
        {
            var byId = await db.Cards.FindAsync([cardId.Value], ct);
            return byId is null || DraftVisibility.IsHiddenFrom(byId, caller.Id)
                ? (null, "Error: Card not found.")
                : (byId.Id, null);
        }

        // cardNumber requires board context
        var (resolvedBoardId, boardError) = await ResolveBoardIdAsync(db, boardId, boardSlug, ct);
        if (boardError is not null)
        {
            return (null, boardError);
        }

        var number = cardNumber!.Value;
        var card = number < 1
            ? null
            : await db.Cards.SingleOrDefaultAsync(c => c.BoardId == resolvedBoardId && c.Number == number, ct);

        return card is not null
            ? (card.Id, null)
            : (null, $"Error: Card #{number.ToString(CultureInfo.InvariantCulture)} not found on this board.");
    }

    // A comment or an attachment on another user's draft is answered as one that does not exist,
    // the same as the draft itself.
    public static async Task<CardComment?> FindVisibleCommentAsync(BoardDbContext db, BoardUser caller, Guid commentId, CancellationToken ct)
    {
        var comment = await db.Comments.FindAsync([commentId], ct);
        return comment is null || await DraftVisibility.IsCardHiddenFromAsync(db, comment.CardId, caller.Id, ct)
            ? null
            : comment;
    }

    public static async Task<CardAttachment?> FindVisibleAttachmentAsync(BoardDbContext db, BoardUser caller, Guid attachmentId, CancellationToken ct)
    {
        var attachment = await db.Attachments.FindAsync([attachmentId], ct);
        return attachment is null || await DraftVisibility.IsCardHiddenFromAsync(db, attachment.CardId, caller.Id, ct)
            ? null
            : attachment;
    }

    private static async Task<(Guid? BoardId, string? Error)> ResolveBoardIdAsync
    (
        BoardDbContext db,
        Guid? boardId,
        string? boardSlug,
        CancellationToken ct
    )
    {
        if (boardId.HasValue && !string.IsNullOrWhiteSpace(boardSlug))
        {
            return (null, "Error: Provide either boardId or boardSlug, not both.");
        }

        if (boardId.HasValue)
        {
            if (!await db.Boards.AnyAsync(b => b.Id == boardId.Value, ct))
            {
                return (null, "Error: Board not found.");
            }

            return (boardId.Value, null);
        }

        if (!string.IsNullOrWhiteSpace(boardSlug))
        {
            var board = await db.Boards.FirstOrDefaultAsync(b => b.Slug == boardSlug, ct);
            return board is not null
                ? (board.Id, null)
                : (null, $"Error: Board with slug '{boardSlug}' not found.");
        }

        return (null, "Error: boardId or boardSlug is required when using cardNumber.");
    }
}
