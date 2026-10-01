using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Mcp;

[McpServerToolType]
public class SearchTools(BoardDbContext db, McpAuthService auth)
{
    [McpServerTool(Name = "search_cards", ReadOnly = true, Destructive = false)]
    [Description("Search cards across ALL boards by free text, card number (prefix with # for exact, e.g. '#42'), name, or description. Mirrors REST GET /search/cards. Results are grouped by board and each card carries the enriched CardSummary shape (labels, sizeName, commentCount, attachmentCount, isArchived, latestComment). Use get_cards when you only need a single board. Archived cards are excluded unless archiveBoardId names their board.")]
    public async Task<string> SearchCardsAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The search query. Prefix with # for exact card-number lookup (e.g. '#42'). Plain numbers match card number or name/description. Text matches name or description.")] string q,
        [Description("Maximum number of cards to return across all boards (default 20, max 50).")] int? limit = null,
        [Description("Board ID (guid) to rank first in the grouped results. Does NOT scope the search — it only orders the matching board ahead of the others.")] Guid? boardId = null,
        [Description("Board ID (guid) whose archived cards should be included in results. Archived cards from all other boards are excluded.")] Guid? archiveBoardId = null,
        CancellationToken ct = default
    )
    {
        var (_, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var effectiveLimit = Math.Clamp(limit ?? 20, 1, 50);
        var results = await SearchHelper.SearchCardsAsync(db, q, effectiveLimit, archiveBoardId, boardId, ct);
        return JsonSerializer.Serialize(results, JsonSerializerOptions.Web);
    }
}
