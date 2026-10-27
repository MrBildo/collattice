using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Mcp;

// Admin-level MCP tools for prune. Mirrors the REST surface
// in PruneEndpoints.cs (POST /boards/{boardId}/prune/preview, POST .../prune).
// Both gate via RequireAdminLevelAsync and share PruneFilter with REST so the
// match semantics (archive exclusion, the olderThan TEXT comparison, lane/label
// filters) cannot drift between the two surfaces.
//
// Security-critical: the prune tool archives only. There is no delete action and
// no prune_delete tool — bulk delete is deliberately excluded
// (archive is reversible, delete is not). The tool does not expose an `action`
// parameter at all, so "delete" is not a value the MCP surface can ever carry.
[McpServerToolType]
public class PruneTools(BoardDbContext db, McpAuthService auth, BoardEventBroadcaster broadcaster, IWebhookSink webhookSink)
{
    [McpServerTool(Name = "prune_preview", ReadOnly = true, Destructive = false)]
    [Description("Preview which cards a prune would match, without changing anything. Requires Administrator or AgentAdministrator role. At least one filter (olderThan, laneIds, or labelIds) is required. laneIds and labelIds accept comma-separated GUIDs ('guid1,guid2') or a JSON array string ('[\"guid1\",\"guid2\"]'). Archived cards are excluded unless includeArchived is true. Returns { matchCount, cards }.")]
    public async Task<string> PrunePreviewAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The board ID to prune within")] Guid boardId,
        [Description("Match cards last updated before this timestamp (ISO-8601, optional)")] DateTimeOffset? olderThan = null,
        [Description("Match cards in these lanes (optional). Comma-separated GUIDs or a JSON array string.")] string? laneIds = null,
        [Description("Match cards carrying any of these labels (optional). Comma-separated GUIDs or a JSON array string.")] string? labelIds = null,
        [Description("Include archived cards in the match (optional, default false)")] bool? includeArchived = null,
        CancellationToken ct = default
    )
    {
        var (_, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var (request, parseError) = await BuildRequestAsync(boardId, olderThan, laneIds, labelIds, includeArchived, ct);
        if (parseError is not null)
        {
            return parseError;
        }

        var query = PruneFilter.BuildFilteredQuery(db, boardId, request);
        var cards = await query.ToListAsync(ct);

        var laneIdSet = cards.Select(c => c.LaneId).Distinct().ToList();
        var laneNames = await db.Lanes
            .Where(l => laneIdSet.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, l => l.Name, ct);

        var cardSummaries = cards
            .Select(c => new
            {
                c.Id,
                c.Number,
                c.Name,
                laneName = laneNames.GetValueOrDefault(c.LaneId, "?"),
                c.LastUpdatedAtUtc,
            })
                .ToList();

        return JsonSerializer.Serialize(new { matchCount = cards.Count, cards = cardSummaries }, JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "prune", Destructive = false)]
    [Description("Archive every card matching the filters in a single call. Requires Administrator or AgentAdministrator role. At least one filter (olderThan, laneIds, or labelIds) is required. laneIds and labelIds accept comma-separated GUIDs ('guid1,guid2') or a JSON array string ('[\"guid1\",\"guid2\"]'). Archived cards are excluded unless includeArchived is true. This tool archives only — archived cards remain restorable; there is no bulk-delete on the MCP surface. Returns { archivedCount }.")]
    public async Task<string> PruneAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The board ID to prune within")] Guid boardId,
        [Description("Match cards last updated before this timestamp (ISO-8601, optional)")] DateTimeOffset? olderThan = null,
        [Description("Match cards in these lanes (optional). Comma-separated GUIDs or a JSON array string.")] string? laneIds = null,
        [Description("Match cards carrying any of these labels (optional). Comma-separated GUIDs or a JSON array string.")] string? labelIds = null,
        [Description("Include archived cards in the match (optional, default false)")] bool? includeArchived = null,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var (request, parseError) = await BuildRequestAsync(boardId, olderThan, laneIds, labelIds, includeArchived, ct);
        if (parseError is not null)
        {
            return parseError;
        }

        var query = PruneFilter.BuildFilteredQuery(db, boardId, request);
        var (archivedCards, archiveError) = await PruneArchiveHelper.ArchiveMatchedAsync(db, boardId, query, ct);
        if (archiveError is not null)
        {
            return $"Error: {archiveError}";
        }

        // card.archived per pruned card — N webhook events, one SSE bell.
        foreach (var archived in await WebhookEventFactory.BuildCardArchivedBatchAsync(db, archivedCards, user!, ct))
        {
            webhookSink.Enqueue(archived);
        }

        broadcaster.PublishBoardUpdated(boardId);

        return JsonSerializer.Serialize(new { archivedCount = archivedCards.Count }, JsonSerializerOptions.Web);
    }

    // Validates the board exists, parses the CSV/JSON-array GUID filters into a
    // PruneRequest, and runs the shared filter validation. Returns (request, null)
    // on success, or (default, "Error: ...") on the first failure. Action is fixed
    // to archive — the request's Action stays null, which PruneFilter treats as the
    // archive path; the MCP surface never carries a delete action.
    private async Task<(PruneRequest Request, string? Error)> BuildRequestAsync
    (
        Guid boardId,
        DateTimeOffset? olderThan,
        string? laneIds,
        string? labelIds,
        bool? includeArchived,
        CancellationToken ct
    )
    {
        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct))
        {
            return (default!, "Error: Board not found.");
        }

        if (!TryParseGuidCsv(laneIds, out var parsedLaneIds, out var laneError))
        {
            return (default!, laneError);
        }

        if (!TryParseGuidCsv(labelIds, out var parsedLabelIds, out var labelError))
        {
            return (default!, labelError);
        }

        var request = new PruneRequest(olderThan, parsedLaneIds, parsedLabelIds, Action: null, includeArchived);

        if (!PruneFilter.ValidateFilters(request, out var filterError))
        {
            return (default!, $"Error: {filterError}");
        }

        return (request, null);
    }

    // Accepts the same two shapes as create_card's labelIds: comma-separated
    // GUIDs ("guid1,guid2") or a JSON-string array ('["guid1","guid2"]'). MCP tool
    // params don't bind native arrays cleanly via the SDK, so CSV is the convention.
    // Null/blank input yields a null array (no filter on that axis). A malformed
    // GUID is rejected loud with the offending token.
    private static bool TryParseGuidCsv(string? value, out Guid[]? result, out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var parts = McpGuidCsv.SplitTokens(value);

        List<Guid> parsed = [];
        foreach (var part in parts)
        {
            if (!Guid.TryParse(part, out var id))
            {
                error = $"Error: Invalid ID format: '{part}'. Expected a GUID.";
                return false;
            }

            parsed.Add(id);
        }

        result = parsed.Count > 0 ? [.. parsed] : null;
        return true;
    }
}
