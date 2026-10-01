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
    // per-card result envelope aligns 1:1 with the requested order.
    public static async Task<(List<CardItem>? Cards, string? Error)> ResolveCardRefsAsync
    (
        BoardDbContext db,
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
            (true, _) => await ResolveByIdsAsync(db, cardIds!, ct),
            _ => await ResolveByNumbersAsync(db, cardNumbers!, boardId, boardSlug, ct),
        };
    }

    private static async Task<(List<CardItem>? Cards, string? Error)> ResolveByIdsAsync
    (
        BoardDbContext db,
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

        var foundById = found.ToDictionary(c => c.Id);
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

    public static async Task<(Guid? CardId, string? Error)> ResolveCardIdAsync
    (
        BoardDbContext db,
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
            return (cardId.Value, null);
        }

        // cardNumber requires board context
        var (resolvedBoardId, boardError) = await ResolveBoardIdAsync(db, boardId, boardSlug, ct);
        if (boardError is not null)
        {
            return (null, boardError);
        }

        var card = await db.Cards.FirstOrDefaultAsync
        (
            c => c.BoardId == resolvedBoardId && c.Number == cardNumber,
            ct
        );
        return card is not null
            ? (card.Id, null)
            : (null, $"Error: Card #{cardNumber!.Value.ToString(CultureInfo.InvariantCulture)} not found on this board.");
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
