using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// A lane create resolves its position and then inserts, so a concurrent create on the same board can
// take the position in between. The interceptor commits that rival lane deterministically, inside the
// real request, on REST and MCP alike: an appended create retries into the next free slot, an explicit
// one answers 409, and an appended create that keeps losing gives up with a clear 409 rather than a 500.
public class LanePositionRaceTests(LanePositionRaceFactory factory) : IClassFixture<LanePositionRaceFactory>, IDisposable
{
    private readonly LanePositionRaceFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly List<IServiceScope> _scopes = [];

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private LaneTools CreateMcpTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);

        return new LaneTools
        (
            scope.ServiceProvider.GetRequiredService<BoardDbContext>(),
            scope.ServiceProvider.GetRequiredService<McpAuthService>(),
            scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>()
        );
    }

    private async Task<Guid> SeedBoardWithLaneAtZeroAsync()
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var boardResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = $"Lane Race {Guid.NewGuid():N}" });
        boardResponse.EnsureSuccessStatusCode();
        var boardId = (await boardResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var laneResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes", new { name = "First", position = 0 });
        laneResponse.EnsureSuccessStatusCode();

        return boardId;
    }

    private async Task<List<int>> LanePositionsAsync(Guid boardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        return await db.Lanes
            .Where(l => l.BoardId == boardId && !l.IsArchiveLane)
            .OrderBy(l => l.Position)
                .Select(l => l.Position)
                    .ToListAsync();
    }

    [Fact]
    public async Task CreateLane_NoPositionLosesTheRace_RetriesIntoTheNextSlotOnBothSurfaces()
    {
        // Arrange — each board holds a lane at 0, so the appended slot is 1 until a rival takes it
        var restBoardId = await SeedBoardWithLaneAtZeroAsync();
        var mcpBoardId = await SeedBoardWithLaneAtZeroAsync();
        var tools = CreateMcpTools();

        // Act
        _factory.Interceptor.Arm(restBoardId);
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/lanes", new { name = "REST appended" });
        var restFired = _factory.Interceptor.FiredCount;

        _factory.Interceptor.Arm(mcpBoardId);
        var mcpResult = await tools.CreateLaneAsync(_factory.AdminAuthKey, mcpBoardId, "MCP appended");
        var mcpFired = _factory.Interceptor.FiredCount;

        // Assert — the rival took 1, the create re-resolved and landed at 2
        restFired.ShouldBe(1);
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await restResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("position").GetInt32().ShouldBe(2);
        (await LanePositionsAsync(restBoardId)).ShouldBe([0, 1, 2]);

        mcpFired.ShouldBe(1);
        JsonSerializer.Deserialize<JsonElement>(mcpResult).GetProperty("position").GetInt32().ShouldBe(2);
        (await LanePositionsAsync(mcpBoardId)).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task CreateLane_ExplicitPositionLosesTheRace_ConflictsOnBothSurfaces()
    {
        // Arrange
        var restBoardId = await SeedBoardWithLaneAtZeroAsync();
        var mcpBoardId = await SeedBoardWithLaneAtZeroAsync();
        var tools = CreateMcpTools();

        // Act — 5 is free when checked, then a rival takes it before the insert
        _factory.Interceptor.Arm(restBoardId);
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/lanes", new { name = "REST explicit", position = 5 });

        _factory.Interceptor.Arm(mcpBoardId);
        var mcpResult = await tools.CreateLaneAsync(_factory.AdminAuthKey, mcpBoardId, "MCP explicit", 5);

        // Assert — the same answer as a position taken before the call
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("Position already taken by another lane");
        (await LanePositionsAsync(restBoardId)).ShouldBe([0, 5]);

        mcpResult.ShouldContain("Error: Position already taken by another lane");
        (await LanePositionsAsync(mcpBoardId)).ShouldBe([0, 5]);
    }

    [Fact]
    public async Task CreateLane_NoPositionLosesEveryAttempt_ConflictsWithAReason()
    {
        // Arrange — losing on all eight attempts exhausts the budget; losing on seven still lands
        var exhaustedBoardId = await SeedBoardWithLaneAtZeroAsync();
        var lastAttemptBoardId = await SeedBoardWithLaneAtZeroAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        _factory.Interceptor.Arm(exhaustedBoardId, collisions: 8);
        var exhausted = await _client.PostAsJsonAsync($"/api/v1/boards/{exhaustedBoardId}/lanes", new { name = "Never lands" });
        var exhaustedFired = _factory.Interceptor.FiredCount;

        _factory.Interceptor.Arm(lastAttemptBoardId, collisions: 7);
        var lastAttempt = await _client.PostAsJsonAsync($"/api/v1/boards/{lastAttemptBoardId}/lanes", new { name = "Lands last" });

        // Assert
        exhaustedFired.ShouldBe(8);
        exhausted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await exhausted.Content.ReadAsStringAsync()).ShouldContain("being created on this board at the same time");

        lastAttempt.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await lastAttempt.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("position").GetInt32().ShouldBe(8);
    }
}
