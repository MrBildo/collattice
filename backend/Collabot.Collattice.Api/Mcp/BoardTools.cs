using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Mcp;

// Board create/update mirror the REST surface in BoardEndpoints.cs (board delete is intentionally
// absent from MCP). board.created / board.renamed are WEBHOOK-ONLY: board CRUD has no SSE broadcast,
// so they enqueue straight to IWebhookSink with no board bell, keeping the SSE wire byte-for-byte
// unchanged.
[McpServerToolType]
public class BoardTools(BoardDbContext db, McpAuthService auth, IWebhookSink webhookSink)
{
    [McpServerTool(Name = "get_boards", ReadOnly = true, Destructive = false)]
    [Description("List all boards. Use this to discover board IDs for scoping other tools.")]
    public async Task<string> GetBoardsAsync
    (
        [Description("Your auth key (X-User-Key)")] string authKey,
        CancellationToken ct = default
    )
    {
        var (_, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var boards = await db.Boards.OrderBy(b => b.Name).ToListAsync(ct);
        return JsonSerializer.Serialize(boards, JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "get_lanes", ReadOnly = true, Destructive = false)]
    [Description("Get all lanes (columns) for a board, ordered by position. Each lane includes a cardCount with the number of cards in that lane.")]
    public async Task<string> GetLanesAsync
    (
        [Description("Your auth key (X-User-Key)")] string authKey,
        [Description("Board ID to scope results")] Guid boardId,
        CancellationToken ct = default
    )
    {
        var (_, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var cardCounts = await db.Cards
            .Where(c => c.BoardId == boardId && !c.IsTemp)
                .GroupBy(c => c.LaneId)
                    .Select(g => new { LaneId = g.Key, Count = g.Count() })
                        .ToDictionaryAsync(x => x.LaneId, x => x.Count, ct);

        var lanes = await db.Lanes
            .Where(l => l.BoardId == boardId && !l.IsArchiveLane)
            .OrderBy(l => l.Position)
                .ToListAsync(ct);

        var result = lanes
            .Select(l => new
            {
                l.Id,
                l.BoardId,
                l.Name,
                l.Position,
                CardCount = cardCounts.GetValueOrDefault(l.Id, 0),
            })
                .ToList();

        return JsonSerializer.Serialize(result, JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "get_sizes", ReadOnly = true, Destructive = false)]
    [Description("Get all card sizes for a board, ordered by ordinal. Use this to discover valid size IDs/names when creating or updating cards.")]
    public async Task<string> GetSizesAsync
    (
        [Description("Your auth key (X-User-Key)")] string authKey,
        [Description("Board ID to scope results")] Guid boardId,
        CancellationToken ct = default
    )
    {
        var (_, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var sizes = await db.CardSizes
            .Where(s => s.BoardId == boardId)
            .OrderBy(s => s.Ordinal)
                .ToListAsync(ct);

        return JsonSerializer.Serialize(sizes, JsonSerializerOptions.Web);
    }

    // Admin-level board create/update. Mirrors the REST
    // surface in BoardEndpoints.cs (POST /boards, PATCH /boards/{id}). Both gate
    // via RequireAdminLevelAsync. Board delete is intentionally absent from MCP.
    [McpServerTool(Name = "create_board", Destructive = false)]
    [Description("Create a board. Requires Administrator or AgentAdministrator role. The slug is auto-derived from the name. The board is seeded with an archive lane and the default card sizes (S, M, L, XL).")]
    public async Task<string> CreateBoardAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The board name")] string name,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return "Error: Name is required.";
        }

        var slug = Board.GenerateSlug(name);

        if (await db.Boards.AnyAsync(b => b.Slug == slug, ct))
        {
            return "Error: A board with that slug already exists.";
        }

        var board = new Board
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Boards.Add(board);

        BoardSeeder.Seed(db, board);

        await db.SaveChangesAsync(ct);

        // board.created — WEBHOOK-ONLY (no board bell); REST/MCP enqueue the identical event.
        WebhookEventFactory.PublishBoardCreated(webhookSink, board, user!);
        return JsonSerializer.Serialize(board, JsonSerializerOptions.Web);
    }

    // Note: `name` is a required non-nullable parameter here (blank is rejected), whereas the
    // REST PATCH /boards/{id} treats `Name` as optional (allows a no-op PATCH with no body).
    // This is deliberate: MCP tools operate on explicit intent — a rename call without a name
    // is always a mistake; REST allows partial-update no-ops as a general contract. Do not
    // "fix" the asymmetry — the two surfaces serve different callers and the difference is
    // intentional.
    [McpServerTool(Name = "update_board", Destructive = false)]
    [Description("Rename a board. Requires Administrator or AgentAdministrator role. Only the name can be changed; the slug is immutable. Name is required — a blank name is rejected.")]
    public async Task<string> UpdateBoardAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the board to rename")] Guid boardId,
        [Description("The new board name")] string name,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var board = await db.Boards.FindAsync([boardId], ct);
        if (board is null)
        {
            return "Error: Board not found.";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return "Error: Name cannot be empty.";
        }

        var oldName = board.Name;
        board.Name = name;
        await db.SaveChangesAsync(ct);

        // board.renamed — WEBHOOK-ONLY (no board bell), only on an actual name change.
        if (name != oldName)
        {
            WebhookEventFactory.PublishBoardRenamed(webhookSink, board, user!);
        }

        return JsonSerializer.Serialize(board, JsonSerializerOptions.Web);
    }
}
