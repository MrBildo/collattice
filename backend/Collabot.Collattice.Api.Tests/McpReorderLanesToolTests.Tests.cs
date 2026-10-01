using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The reorder_lanes MCP tool. The tool is a thin wrapper over the
// same LaneReorderHelper the REST endpoint uses, so this file focuses on the
// MCP-specific surface (CSV parsing, the admin-level role gate, the "Error: …"
// loud-failure shape) plus the two correctness properties that matter on both
// surfaces: the swap persists under the unique (BoardId, Position) index, and a
// stale/mismatched set is rejected with no mutation. Concurrency reasoning is
// covered as two deterministic cases — a concurrent structural change fails the
// set check loud; a concurrent pure reorder is last-write-wins on order.
public class McpReorderLanesToolTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly List<IServiceScope> _scopes = [];

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private (BoardDbContext Db, LaneTools Lane, BoardTools Board) CreateTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = new McpAuthService(new UserResolver(db));
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        return (db, new LaneTools(db, auth, broadcaster), new BoardTools(db, auth, scope.ServiceProvider.GetRequiredService<IWebhookSink>()));
    }

    private async Task<Guid> CreateBoardAsync(BoardTools board)
    {
        var result = await board.CreateBoardAsync(_factory.AdminAuthKey, $"mcp-reorder-{Guid.NewGuid():N}");
        return JsonSerializer.Deserialize<JsonElement>(result).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateLaneAsync(LaneTools lane, Guid boardId, string name, int position)
    {
        var result = await lane.CreateLaneAsync(_factory.AdminAuthKey, boardId, name, position);
        return JsonSerializer.Deserialize<JsonElement>(result).GetProperty("id").GetGuid();
    }

    // Reads lane positions through a fresh scope so the assertion never sees a
    // stale identity-map instance from the scope that performed the reorder.
    private async Task<List<(Guid Id, int Position)>> ReadLanesAsync(Guid boardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.Lanes
            .Where(l => l.BoardId == boardId && !l.IsArchiveLane)
            .OrderBy(l => l.Position)
            .Select(l => new ValueTuple<Guid, int>(l.Id, l.Position))
                .ToListAsync();
    }

    private static string Csv(params Guid[] ids) =>
        string.Join(',', ids.Select(id => id.ToString()));

    [Fact]
    public async Task ReorderLanes_SwapsTwoAdjacentLanes_PersistsUnderUniqueIndex()
    {
        // Arrange — two lanes at 0 and 1 (the swap that collides naively)
        var (_, lane, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);
        var laneA = await CreateLaneAsync(lane, boardId, "A", 0);
        var laneB = await CreateLaneAsync(lane, boardId, "B", 1);

        // Act — reverse
        var result = await lane.ReorderLanesAsync(_factory.AdminAuthKey, boardId, Csv(laneB, laneA));

        // Assert — not an error, swap persisted dense 0..1
        result.ShouldNotStartWith("Error");

        var lanes = await ReadLanesAsync(boardId);
        lanes.ShouldBe([(laneB, 0), (laneA, 1)]);
    }

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task ReorderLanes_AdminLevel_Succeeds(UserRole role)
    {
        // Arrange
        var (_, lane, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);
        var laneA = await CreateLaneAsync(lane, boardId, "A", 0);
        var laneB = await CreateLaneAsync(lane, boardId, "B", 1);

        var authKey = role == UserRole.Administrator
            ? _factory.AdminAuthKey
            : await AgentAdminKeyAsync();

        // Act
        var result = await lane.ReorderLanesAsync(authKey, boardId, Csv(laneB, laneA));

        // Assert
        result.ShouldNotStartWith("Error");
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task ReorderLanes_NonAdmin_ReturnsErrorAndMutatesNothing(UserRole role)
    {
        // Arrange
        var (_, lane, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);
        var laneA = await CreateLaneAsync(lane, boardId, "A", 0);
        var laneB = await CreateLaneAsync(lane, boardId, "B", 1);

        using var setupClient = _factory.CreateClient();
        var user = await TestAuthHelper.CreateUserAsync(setupClient, _factory, $"reorder-{role}-{Guid.NewGuid():N}", role);

        var before = await ReadLanesAsync(boardId);

        // Act
        var result = await lane.ReorderLanesAsync(user.AuthKey, boardId, Csv(laneB, laneA));

        // Assert
        result.ShouldBe("Error: This operation requires administrator privileges.");
        (await ReadLanesAsync(boardId)).ShouldBe(before);
    }

    [Fact]
    public async Task ReorderLanes_StaleSetAfterConcurrentDelete_FailsLoudAndMutatesNothing()
    {
        // Arrange — three lanes; a "concurrent" writer deletes one out from under
        // a request that still holds the old three-lane set.
        var (_, lane, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);
        var laneA = await CreateLaneAsync(lane, boardId, "A", 0);
        var laneB = await CreateLaneAsync(lane, boardId, "B", 1);
        var laneC = await CreateLaneAsync(lane, boardId, "C", 2);

        // Concurrent structural change: lane C is deleted.
        var deleteResult = await lane.DeleteLaneAsync(_factory.AdminAuthKey, laneC);
        deleteResult.ShouldBe("Lane deleted.");

        var before = await ReadLanesAsync(boardId);

        // Act — the stale request still names all three lanes.
        var result = await lane.ReorderLanesAsync(_factory.AdminAuthKey, boardId, Csv(laneC, laneB, laneA));

        // Assert — the set-equality gate catches the structural drift, no mutation.
        result.ShouldStartWith("Error");
        (await ReadLanesAsync(boardId)).ShouldBe(before);
    }

    [Fact]
    public async Task ReorderLanes_ConcurrentPureReorder_IsLastWriteWins()
    {
        // Arrange — same lane set, two sequential reorders (the deterministic
        // stand-in for two writers racing a pure reorder; SQLite serializes the
        // writes, so the second observed order wins).
        var (_, lane, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);
        var laneA = await CreateLaneAsync(lane, boardId, "A", 0);
        var laneB = await CreateLaneAsync(lane, boardId, "B", 1);
        var laneC = await CreateLaneAsync(lane, boardId, "C", 2);

        // Act — writer 1 then writer 2, both valid full-set reorders
        (await lane.ReorderLanesAsync(_factory.AdminAuthKey, boardId, Csv(laneB, laneC, laneA))).ShouldNotStartWith("Error");
        (await lane.ReorderLanesAsync(_factory.AdminAuthKey, boardId, Csv(laneC, laneA, laneB))).ShouldNotStartWith("Error");

        // Assert — the last write's order is what persists
        var lanes = await ReadLanesAsync(boardId);
        lanes.ShouldBe([(laneC, 0), (laneA, 1), (laneB, 2)]);
    }

    [Fact]
    public async Task ReorderLanes_ArchiveLaneInInput_FailsLoud()
    {
        // Arrange — find the board's archive lane and try to smuggle it in.
        var (db, lane, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);
        var laneA = await CreateLaneAsync(lane, boardId, "A", 0);

        var archiveLaneId = await db.Lanes
            .Where(l => l.BoardId == boardId && l.IsArchiveLane)
            .Select(l => l.Id)
                .SingleAsync();

        // Act
        var result = await lane.ReorderLanesAsync(_factory.AdminAuthKey, boardId, Csv(laneA, archiveLaneId));

        // Assert
        result.ShouldStartWith("Error");
        result.ShouldContain("Archive");
    }

    [Fact]
    public async Task ReorderLanes_DuplicateId_FailsLoud()
    {
        var (_, lane, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);
        var laneA = await CreateLaneAsync(lane, boardId, "A", 0);
        _ = await CreateLaneAsync(lane, boardId, "B", 1);

        var result = await lane.ReorderLanesAsync(_factory.AdminAuthKey, boardId, Csv(laneA, laneA));

        result.ShouldStartWith("Error");
    }

    [Fact]
    public async Task ReorderLanes_MalformedCsv_ReturnsParseError()
    {
        var (_, lane, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);
        _ = await CreateLaneAsync(lane, boardId, "A", 0);

        var result = await lane.ReorderLanesAsync(_factory.AdminAuthKey, boardId, "not-a-guid");

        result.ShouldStartWith("Error: Invalid lane ID format");
    }

    [Fact]
    public async Task ReorderLanes_NonexistentBoard_ReturnsError()
    {
        var (_, lane, _) = CreateTools();

        var result = await lane.ReorderLanesAsync(_factory.AdminAuthKey, Guid.NewGuid(), Csv(Guid.NewGuid()));

        result.ShouldBe("Error: Board not found.");
    }

    private async Task<string> AgentAdminKeyAsync()
    {
        using var setupClient = _factory.CreateClient();
        var user = await TestAuthHelper.CreateUserAsync
        (
            setupClient,
            _factory,
            $"reorder-agentadmin-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}",
            UserRole.AgentAdministrator
        );
        return user.AuthKey;
    }
}
