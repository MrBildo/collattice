using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Cross-surface parity tests for lane CRUD.
// The REST/MCP parity audit named Lane/Label/Size CRUD the HIGHEST
// drift-risk surface: the rules are typed twice — once in LaneEndpoints.cs, once
// in LaneTools.cs — with no shared service and no test asserting the copies agree.
// These tests feed the same invalid input to both front doors and assert both
// reject by the same rule with the same diagnostic message.
//
// Parity claim shape: the two surfaces speak different idioms — REST
// returns an HTTP status + a bare message body; MCP returns an "Error: ..." string
// with no status. The HTTP status is REST's own internal categorization (400 for
// validation, 409 Conflict for conflict-class rejections); MCP has no status. So the
// load-bearing assertion is "same rule → both reject, same diagnosis message." Each
// REST case still pins its actual status so the test guards the real contract.
public class LaneCrudParityTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly List<IServiceScope> _scopes = [];

    private (BoardDbContext Db, LaneTools Tools, string AuthKey) CreateMcpTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        return (db, new LaneTools(db, auth, broadcaster), _factory.AdminAuthKey);
    }

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // Spins up a fresh board so each test owns its lanes/cards in isolation. Returns
    // the board id and the id of the seeded archive lane (needed for the archive-guard
    // cases, which the public lane-list endpoint deliberately hides).
    private async Task<(Guid BoardId, Guid ArchiveLaneId)> SeedBoardAsync()
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var boardResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = $"Lane Parity {Guid.NewGuid():N}" });
        boardResponse.EnsureSuccessStatusCode();
        var board = await boardResponse.Content.ReadFromJsonAsync<JsonElement>();
        var boardId = board.GetProperty("id").GetGuid();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var archiveLaneId = await db.Lanes
            .Where(l => l.BoardId == boardId && l.IsArchiveLane)
                .Select(l => l.Id)
                    .FirstAsync();

        return (boardId, archiveLaneId);
    }

    private async Task<Guid> CreateLaneAsync(Guid boardId, string name, int position)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes", new { name, position });
        response.EnsureSuccessStatusCode();
        var lane = await response.Content.ReadFromJsonAsync<JsonElement>();
        return lane.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateCardInLaneAsync(Guid boardId, Guid laneId)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/cards", new { name = "Occupant", laneId });
        response.EnsureSuccessStatusCode();
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        return card.GetProperty("id").GetGuid();
    }

    // ── create_lane: empty name rejected on both surfaces ─────────────────────

    [Fact]
    public async Task CreateLane_EmptyName_RejectedOnBothSurfaces()
    {
        // Arrange
        var (boardId, _) = await SeedBoardAsync();
        var (_, tools, authKey) = CreateMcpTools();

        // Act — REST
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes", new { name = "   ", position = 1 });

        // Act — MCP
        var mcpResult = await tools.CreateLaneAsync(authKey, boardId, "   ", 1);

        // Assert — both reject with the same diagnosis, each in its own idiom
        restResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("Name is required");
        restBody.ShouldNotContain("Error:");

        mcpResult.ShouldContain("Error: Name is required");
    }

    // ── create_lane: reserved int.MaxValue position rejected on both surfaces ──

    [Fact]
    public async Task CreateLane_ReservedMaxPosition_RejectedOnBothSurfaces()
    {
        // Arrange
        var (boardId, _) = await SeedBoardAsync();
        var (_, tools, authKey) = CreateMcpTools();

        // Act — REST
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{boardId}/lanes",
            new { name = "Reserved REST", position = int.MaxValue }
        );

        // Act — MCP
        var mcpResult = await tools.CreateLaneAsync(authKey, boardId, "Reserved MCP", int.MaxValue);

        // Assert — both reject the reserved archive-lane position
        restResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("Position value is reserved");
        restBody.ShouldNotContain("Error:");

        mcpResult.ShouldContain("Error: Position value is reserved");
    }

    // create_lane with no position appends after the board's last visible lane. The
    // gap (0, 5) proves "after the last", not "count of lanes"; the archive lane at
    // int.MaxValue must not be the lane it appends after.

    [Fact]
    public async Task CreateLane_NoPosition_AppendsAfterLastLaneOnBothSurfaces()
    {
        // Arrange
        var (restBoardId, _) = await SeedBoardAsync();
        var (mcpBoardId, _) = await SeedBoardAsync();
        await CreateLaneAsync(restBoardId, "REST zero", 0);
        await CreateLaneAsync(restBoardId, "REST five", 5);
        await CreateLaneAsync(mcpBoardId, "MCP zero", 0);
        await CreateLaneAsync(mcpBoardId, "MCP five", 5);
        var (_, tools, authKey) = CreateMcpTools();

        // Act
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/lanes", new { name = "REST appended" });

        var mcpResult = await tools.CreateLaneAsync(authKey, mcpBoardId, "MCP appended");

        // Assert
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var restLane = await restResponse.Content.ReadFromJsonAsync<JsonElement>();
        restLane.GetProperty("position").GetInt32().ShouldBe(6);

        JsonSerializer.Deserialize<JsonElement>(mcpResult).GetProperty("position").GetInt32().ShouldBe(6);
    }

    [Fact]
    public async Task CreateLane_NoPositionOnEmptyBoard_StartsAtZeroOnBothSurfaces()
    {
        // Arrange — a fresh board holds only its archive lane
        var (restBoardId, _) = await SeedBoardAsync();
        var (mcpBoardId, _) = await SeedBoardAsync();
        var (_, tools, authKey) = CreateMcpTools();

        // Act
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/lanes", new { name = "REST first" });

        var mcpResult = await tools.CreateLaneAsync(authKey, mcpBoardId, "MCP first");

        // Assert
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var restLane = await restResponse.Content.ReadFromJsonAsync<JsonElement>();
        restLane.GetProperty("position").GetInt32().ShouldBe(0);

        JsonSerializer.Deserialize<JsonElement>(mcpResult).GetProperty("position").GetInt32().ShouldBe(0);
    }

    // create_lane at a taken position is a conflict on both surfaces, the same rule
    // and message as update_lane, and REST pins 409 to match PATCH /lanes/{id}.

    [Fact]
    public async Task CreateLane_TakenPosition_RejectedOnBothSurfaces()
    {
        // Arrange
        var (restBoardId, _) = await SeedBoardAsync();
        var (mcpBoardId, _) = await SeedBoardAsync();
        await CreateLaneAsync(restBoardId, "REST occupant", 3);
        await CreateLaneAsync(mcpBoardId, "MCP occupant", 3);
        var (db, tools, authKey) = CreateMcpTools();

        // Act
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/lanes", new { name = "REST intruder", position = 3 });

        var mcpResult = await tools.CreateLaneAsync(authKey, mcpBoardId, "MCP intruder", 3);

        // Assert
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("Position already taken by another lane");
        restBody.ShouldNotContain("Error:");

        mcpResult.ShouldContain("Error: Position already taken by another lane");

        (await db.Lanes.CountAsync(l => l.BoardId == restBoardId && !l.IsArchiveLane)).ShouldBe(1);
        (await db.Lanes.CountAsync(l => l.BoardId == mcpBoardId && !l.IsArchiveLane)).ShouldBe(1);
    }

    // When the last lane already sits just below the archive lane's reserved slot,
    // there is nowhere to append, so both surfaces refuse with a reason instead of
    // colliding with the archive lane.

    [Fact]
    public async Task CreateLane_NoPositionAndNoRoom_RejectedOnBothSurfaces()
    {
        // Arrange
        var (restBoardId, _) = await SeedBoardAsync();
        var (mcpBoardId, _) = await SeedBoardAsync();
        await CreateLaneAsync(restBoardId, "REST ceiling", int.MaxValue - 1);
        await CreateLaneAsync(mcpBoardId, "MCP ceiling", int.MaxValue - 1);
        var (_, tools, authKey) = CreateMcpTools();

        // Act
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/lanes", new { name = "REST overflow" });

        var mcpResult = await tools.CreateLaneAsync(authKey, mcpBoardId, "MCP overflow");

        // Assert
        restResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("No position is free after the board's last lane");
        restBody.ShouldNotContain("Error:");

        mcpResult.ShouldContain("Error: No position is free after the board's last lane");
    }

    // ── update_lane: reserved int.MaxValue position rejected on both surfaces ──

    [Fact]
    public async Task UpdateLane_ReservedMaxPosition_RejectedOnBothSurfaces()
    {
        // Arrange — one lane on each of two boards to move to the reserved position
        var (restBoardId, _) = await SeedBoardAsync();
        var (mcpBoardId, _) = await SeedBoardAsync();
        var restLaneId = await CreateLaneAsync(restBoardId, "REST lane", 1);
        var mcpLaneId = await CreateLaneAsync(mcpBoardId, "MCP lane", 1);
        var (_, tools, authKey) = CreateMcpTools();

        // Act — REST
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PatchAsJsonAsync($"/api/v1/lanes/{restLaneId}", new { position = int.MaxValue });

        // Act — MCP
        var mcpResult = await tools.UpdateLaneAsync(authKey, mcpLaneId, position: int.MaxValue);

        // Assert
        restResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("Position value is reserved");
        restBody.ShouldNotContain("Error:");

        mcpResult.ShouldContain("Error: Position value is reserved");
    }

    // ── update_lane: position collision rejected on both surfaces ─────────────
    // REST categorizes this as 409 Conflict; MCP has no status. The rule and the
    // message are what must match — the test pins REST's 409 as its real contract.

    [Fact]
    public async Task UpdateLane_PositionCollision_RejectedOnBothSurfaces()
    {
        // Arrange — two lanes per board (positions 1 and 2); move the second onto
        // the first's position to collide
        var (restBoardId, _) = await SeedBoardAsync();
        var (mcpBoardId, _) = await SeedBoardAsync();
        await CreateLaneAsync(restBoardId, "REST first", 1);
        var restSecondLaneId = await CreateLaneAsync(restBoardId, "REST second", 2);
        await CreateLaneAsync(mcpBoardId, "MCP first", 1);
        var mcpSecondLaneId = await CreateLaneAsync(mcpBoardId, "MCP second", 2);
        var (_, tools, authKey) = CreateMcpTools();

        // Act — REST
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PatchAsJsonAsync($"/api/v1/lanes/{restSecondLaneId}", new { position = 1 });

        // Act — MCP
        var mcpResult = await tools.UpdateLaneAsync(authKey, mcpSecondLaneId, position: 1);

        // Assert — REST 409 Conflict, MCP "Error: ..." — same rule, same message
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("Position already taken by another lane");
        restBody.ShouldNotContain("Error:");

        mcpResult.ShouldContain("Error: Position already taken by another lane");
    }

    // ── update_lane: archive-lane guard rejects on both surfaces ──────────────

    [Fact]
    public async Task UpdateLane_ArchiveLane_RejectedOnBothSurfaces()
    {
        // Arrange — each board's own seeded archive lane is the target
        var (restBoardId, restArchiveLaneId) = await SeedBoardAsync();
        var (_, mcpArchiveLaneId) = await SeedBoardAsync();
        var (_, tools, authKey) = CreateMcpTools();

        // Act — REST
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PatchAsJsonAsync($"/api/v1/lanes/{restArchiveLaneId}", new { name = "Renamed archive" });

        // Act — MCP
        var mcpResult = await tools.UpdateLaneAsync(authKey, mcpArchiveLaneId, name: "Renamed archive");

        // Assert
        restResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("Archive lanes cannot be modified");
        restBody.ShouldNotContain("Error:");

        mcpResult.ShouldContain("Error: Archive lanes cannot be modified");
    }

    // ── delete_lane: archive-lane guard rejects on both surfaces ──────────────

    [Fact]
    public async Task DeleteLane_ArchiveLane_RejectedOnBothSurfaces()
    {
        // Arrange
        var (restBoardId, restArchiveLaneId) = await SeedBoardAsync();
        var (_, mcpArchiveLaneId) = await SeedBoardAsync();
        var (_, tools, authKey) = CreateMcpTools();

        // Act — REST
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.DeleteAsync($"/api/v1/lanes/{restArchiveLaneId}");

        // Act — MCP
        var mcpResult = await tools.DeleteLaneAsync(authKey, mcpArchiveLaneId);

        // Assert
        restResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("Archive lanes cannot be deleted");
        restBody.ShouldNotContain("Error:");

        mcpResult.ShouldContain("Error: Archive lanes cannot be deleted");
    }

    // ── delete_lane: non-empty lane rejected on both surfaces ─────────────────
    // REST categorizes this as 409 Conflict; MCP returns the "Error: ..." string.

    [Fact]
    public async Task DeleteLane_NonEmpty_RejectedOnBothSurfaces()
    {
        // Arrange — a lane with one card on each board
        var (restBoardId, _) = await SeedBoardAsync();
        var (mcpBoardId, _) = await SeedBoardAsync();
        var restLaneId = await CreateLaneAsync(restBoardId, "REST occupied", 1);
        var mcpLaneId = await CreateLaneAsync(mcpBoardId, "MCP occupied", 1);
        await CreateCardInLaneAsync(restBoardId, restLaneId);
        await CreateCardInLaneAsync(mcpBoardId, mcpLaneId);
        var (db, tools, authKey) = CreateMcpTools();

        // Act — REST
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.DeleteAsync($"/api/v1/lanes/{restLaneId}");

        // Act — MCP
        var mcpResult = await tools.DeleteLaneAsync(authKey, mcpLaneId);

        // Assert — REST 409 Conflict, MCP "Error: ..." — same rule, same message
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("Lane must be empty");
        restBody.ShouldNotContain("Error:");

        mcpResult.ShouldContain("Error: Lane must be empty");

        // Neither surface deleted its occupied lane
        (await db.Lanes.AnyAsync(l => l.Id == restLaneId)).ShouldBeTrue();
        (await db.Lanes.AnyAsync(l => l.Id == mcpLaneId)).ShouldBeTrue();
    }
}
