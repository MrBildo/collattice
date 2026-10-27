using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Cross-surface parity tests for the prune archive-loop.
// REST POST /boards/{id}/prune (archive action) and the MCP prune tool
// now route through the shared PruneArchiveHelper, so the same filter must archive
// the same cards on both surfaces, and the no-archive-lane failure must be reported
// the same way (REST 400, MCP "Error: ..." string).
public class PruneArchiveParityTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly List<IServiceScope> _scopes = [];

    private (BoardDbContext Db, PruneTools Prune) CreateMcpTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var webhookSink = scope.ServiceProvider.GetRequiredService<IWebhookSink>();
        return (db, new PruneTools(db, auth, broadcaster, webhookSink));
    }

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // Spins up a fresh board with one non-archive lane carrying `cardCount` cards.
    // Returns the board id and the lane id so a prune can target the lane.
    private async Task<(Guid BoardId, Guid LaneId)> SeedBoardWithCardsAsync(int cardCount)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var boardResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = $"Prune Parity {Guid.NewGuid():N}" });
        boardResponse.EnsureSuccessStatusCode();
        var board = await boardResponse.Content.ReadFromJsonAsync<JsonElement>();
        var boardId = board.GetProperty("id").GetGuid();

        var laneResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes", new { name = "Work", position = 0 });
        laneResponse.EnsureSuccessStatusCode();
        var lane = await laneResponse.Content.ReadFromJsonAsync<JsonElement>();
        var laneId = lane.GetProperty("id").GetGuid();

        for (var i = 0; i < cardCount; i++)
        {
            var cardName = $"Card {i.ToString(CultureInfo.InvariantCulture)}";
            var cardResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/cards", new { name = cardName, laneId });
            cardResponse.EnsureSuccessStatusCode();
        }

        return (boardId, laneId);
    }

    // Deletes the board's auto-seeded archive lane so a prune-archive hits the
    // no-archive-lane failure path on both surfaces.
    private async Task RemoveArchiveLaneAsync(Guid boardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var archiveLanes = await db.Lanes
            .Where(l => l.BoardId == boardId && l.IsArchiveLane)
                .ToListAsync();

        db.Lanes.RemoveRange(archiveLanes);
        await db.SaveChangesAsync();
    }

    private async Task<int> ArchivedCountAsync(Guid boardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var archiveLaneIds = await db.Lanes
            .Where(l => l.BoardId == boardId && l.IsArchiveLane)
                .Select(l => l.Id)
                    .ToListAsync();

        return await db.Cards.CountAsync(c => c.BoardId == boardId && archiveLaneIds.Contains(c.LaneId));
    }

    [Fact]
    public async Task PruneArchive_SameFilter_ArchivesSameOnBothSurfaces()
    {
        // Arrange — two equivalent boards, three cards each in one lane
        var (restBoardId, restLaneId) = await SeedBoardWithCardsAsync(3);
        var (mcpBoardId, mcpLaneId) = await SeedBoardWithCardsAsync(3);
        var (_, prune) = CreateMcpTools();

        // Act — REST prune-archive by lane
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{restBoardId}/prune",
            new { laneIds = new[] { restLaneId } }
        );

        // Act — MCP prune by lane
        var mcpResult = await prune.PruneAsync(_factory.AdminAuthKey, mcpBoardId, laneIds: mcpLaneId.ToString());

        // Assert — both report 3 archived, and both boards have 3 cards in the archive lane
        restResponse.EnsureSuccessStatusCode();
        var restBody = await restResponse.Content.ReadFromJsonAsync<JsonElement>();
        restBody.GetProperty("archivedCount").GetInt32().ShouldBe(3);

        JsonSerializer.Deserialize<JsonElement>(mcpResult).GetProperty("archivedCount").GetInt32().ShouldBe(3);

        (await ArchivedCountAsync(restBoardId)).ShouldBe(3);
        (await ArchivedCountAsync(mcpBoardId)).ShouldBe(3);
    }

    [Fact]
    public async Task PruneArchive_NoArchiveLane_FailsSameWayOnBothSurfaces()
    {
        // Arrange — two equivalent boards, archive lane removed from each so the
        // archive-loop cannot find a destination lane
        var (restBoardId, restLaneId) = await SeedBoardWithCardsAsync(1);
        var (mcpBoardId, mcpLaneId) = await SeedBoardWithCardsAsync(1);
        await RemoveArchiveLaneAsync(restBoardId);
        await RemoveArchiveLaneAsync(mcpBoardId);
        var (_, prune) = CreateMcpTools();

        // Act — REST prune-archive by lane
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{restBoardId}/prune",
            new { laneIds = new[] { restLaneId } }
        );

        // Act — MCP prune by lane
        var mcpResult = await prune.PruneAsync(_factory.AdminAuthKey, mcpBoardId, laneIds: mcpLaneId.ToString());

        // Assert — both fail with the same diagnosis, each in its own idiom: REST 400
        // with the bare message, MCP the "Error: ..." string
        restResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("Board has no archive lane");
        restBody.ShouldNotContain("Error:");

        mcpResult.ShouldContain("Error: Board has no archive lane");

        // Neither surface archived anything
        (await ArchivedCountAsync(restBoardId)).ShouldBe(0);
        (await ArchivedCountAsync(mcpBoardId)).ShouldBe(0);
    }
}
