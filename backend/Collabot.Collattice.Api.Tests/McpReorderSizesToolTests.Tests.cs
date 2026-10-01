using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The reorder_sizes MCP tool. The tool is a thin wrapper over the
// same SizeReorderHelper the REST endpoint uses, so this file focuses on
// the MCP-specific surface (CSV parsing, the admin-level role gate, the
// "Error: …" loud-failure shape) plus the two correctness properties that
// matter on both surfaces: the swap persists under the unique (BoardId, Ordinal)
// index, and a stale/mismatched set is rejected with no mutation. Mirrors
// McpReorderLanesToolTests exactly, adapted for sizes.
public class McpReorderSizesToolTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
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

    private (BoardDbContext Db, SizeTools Size, BoardTools Board) CreateTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = new McpAuthService(new UserResolver(db));
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        return (db, new SizeTools(db, auth, broadcaster), new BoardTools(db, auth, scope.ServiceProvider.GetRequiredService<IWebhookSink>()));
    }

    private async Task<Guid> CreateBoardAsync(BoardTools board)
    {
        var result = await board.CreateBoardAsync(_factory.AdminAuthKey, $"mcp-size-reorder-{Guid.NewGuid():N}");
        return JsonSerializer.Deserialize<JsonElement>(result).GetProperty("id").GetGuid();
    }

    // Reads size ordinals through a fresh scope so the assertion never sees a
    // stale identity-map instance from the scope that performed the reorder.
    private async Task<List<(Guid Id, int Ordinal)>> ReadSizesAsync(Guid boardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.CardSizes
            .Where(s => s.BoardId == boardId)
            .OrderBy(s => s.Ordinal)
            .Select(s => new ValueTuple<Guid, int>(s.Id, s.Ordinal))
                .ToListAsync();
    }

    private static string Csv(params Guid[] ids) =>
        string.Join(',', ids.Select(id => id.ToString()));

    [Fact]
    public async Task ReorderSizes_SwapsTwoAdjacentSizes_PersistsUnderUniqueIndex()
    {
        // Arrange — fresh board has the default S/M/L/XL set; swap the first two
        // (the case that collides naively under the unique (BoardId, Ordinal) index).
        var (_, size, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);
        var before = await ReadSizesAsync(boardId);
        before.Count.ShouldBe(4);
        var (firstId, _) = before[0];
        var (secondId, _) = before[1];

        // Act — reverse the first two, keep the rest
        var requested = new[] { secondId, firstId, before[2].Id, before[3].Id };
        var result = await size.ReorderSizesAsync(_factory.AdminAuthKey, boardId, Csv(requested));

        // Assert — not an error, swap persisted dense 0..3
        result.ShouldNotStartWith("Error");

        var after = await ReadSizesAsync(boardId);
        after.Select(s => s.Id).ShouldBe(requested);
        after.Select(s => s.Ordinal).ShouldBe([0, 1, 2, 3]);
    }

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task ReorderSizes_AdminLevel_Succeeds(UserRole role)
    {
        // Arrange
        var (_, size, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);
        var before = await ReadSizesAsync(boardId);
        var reversed = before.Select(s => s.Id).Reverse().ToArray();

        var authKey = role == UserRole.Administrator
            ? _factory.AdminAuthKey
            : await AgentAdminKeyAsync();

        // Act
        var result = await size.ReorderSizesAsync(authKey, boardId, Csv(reversed));

        // Assert
        result.ShouldNotStartWith("Error");
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task ReorderSizes_NonAdmin_ReturnsErrorAndMutatesNothing(UserRole role)
    {
        // Arrange
        var (_, size, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);
        var before = await ReadSizesAsync(boardId);
        var reversed = before.Select(s => s.Id).Reverse().ToArray();

        using var setupClient = _factory.CreateClient();
        var user = await TestAuthHelper.CreateUserAsync(setupClient, _factory, $"size-reorder-{role}-{Guid.NewGuid():N}", role);

        var snap = await ReadSizesAsync(boardId);

        // Act
        var result = await size.ReorderSizesAsync(user.AuthKey, boardId, Csv(reversed));

        // Assert
        result.ShouldBe("Error: This operation requires administrator privileges.");
        (await ReadSizesAsync(boardId)).ShouldBe(snap);
    }

    [Fact]
    public async Task ReorderSizes_StaleSetAfterConcurrentDelete_FailsLoudAndMutatesNothing()
    {
        // Arrange — delete one size out from under a request that still names it.
        var (_, size, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);
        var before = await ReadSizesAsync(boardId);

        // Capture the extra size id before deleting it.
        var extraSizeId = before[^1].Id;

        // Concurrent structural change: delete the last size.
        var deleteResult = await size.DeleteSizeAsync(_factory.AdminAuthKey, extraSizeId);
        deleteResult.ShouldBe("Size deleted.");

        var snap = await ReadSizesAsync(boardId);

        // Act — the stale request still names the deleted size.
        var staleOrder = before.Select(s => s.Id).ToArray();
        var result = await size.ReorderSizesAsync(_factory.AdminAuthKey, boardId, Csv(staleOrder));

        // Assert — the set-equality gate catches the structural drift, no mutation.
        result.ShouldStartWith("Error");
        (await ReadSizesAsync(boardId)).ShouldBe(snap);
    }

    [Fact]
    public async Task ReorderSizes_ConcurrentPureReorder_IsLastWriteWins()
    {
        // Arrange — two sequential reorders (the deterministic stand-in for two
        // writers racing a pure reorder; SQLite serializes writes, so the second
        // observed order wins).
        var (_, size, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);
        var before = await ReadSizesAsync(boardId);
        before.Count.ShouldBe(4);

        var (idA, _) = before[0];
        var (idB, _) = before[1];
        var (idC, _) = before[2];
        var (idD, _) = before[3];

        // Act — writer 1 then writer 2, both valid full-set reorders
        (await size.ReorderSizesAsync(_factory.AdminAuthKey, boardId, Csv(idB, idC, idA, idD))).ShouldNotStartWith("Error");
        (await size.ReorderSizesAsync(_factory.AdminAuthKey, boardId, Csv(idC, idA, idD, idB))).ShouldNotStartWith("Error");

        // Assert — the last write's order is what persists
        var after = await ReadSizesAsync(boardId);
        after.ShouldBe([(idC, 0), (idA, 1), (idD, 2), (idB, 3)]);
    }

    [Fact]
    public async Task ReorderSizes_DuplicateId_FailsLoud()
    {
        var (_, size, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);
        var before = await ReadSizesAsync(boardId);

        var result = await size.ReorderSizesAsync(_factory.AdminAuthKey, boardId, Csv(before[0].Id, before[0].Id));

        result.ShouldStartWith("Error");
    }

    [Fact]
    public async Task ReorderSizes_MalformedCsv_ReturnsParseError()
    {
        var (_, size, board) = CreateTools();
        var boardId = await CreateBoardAsync(board);

        var result = await size.ReorderSizesAsync(_factory.AdminAuthKey, boardId, "not-a-guid");

        result.ShouldStartWith("Error: Invalid size ID format");
    }

    [Fact]
    public async Task ReorderSizes_NonexistentBoard_ReturnsError()
    {
        var (_, size, _) = CreateTools();

        var result = await size.ReorderSizesAsync(_factory.AdminAuthKey, Guid.NewGuid(), Csv(Guid.NewGuid()));

        result.ShouldBe("Error: Board not found.");
    }

    private async Task<string> AgentAdminKeyAsync()
    {
        using var setupClient = _factory.CreateClient();
        var user = await TestAuthHelper.CreateUserAsync
        (
            setupClient,
            _factory,
            $"size-reorder-agentadmin-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}",
            UserRole.AgentAdministrator
        );
        return user.AuthKey;
    }
}
