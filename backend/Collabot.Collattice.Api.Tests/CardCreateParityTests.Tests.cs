using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Cross-surface parity tests for the card-create write path.
// Both front doors — REST POST /cards and the MCP create_card
// tool — now route through the shared CardCreateHelper + SizeResolver, so the same
// invalid input must be rejected identically on both surfaces. These tests prove the
// dedup preserved behavior and pin the formerly-divergent cases closed:
//   - whitespace-only name (MCP used to accept "   "; now rejects, matching REST),
//   - size-not-on-board,
//   - label-not-on-board,
//   - the ratified REST sizeName addition (REST gains size-by-name via SizeResolver).
public class CardCreateParityTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly List<IServiceScope> _scopes = [];

    private (BoardDbContext Db, CardTools Tools, string AuthKey) CreateMcpTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var auth = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        var tools = new CardTools(db, auth, broadcaster);
        return (db, tools, _factory.AdminAuthKey);
    }

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async Task<Guid> GetFirstLaneIdAsync() =>
        await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

    // Creates a second board and returns a (sizeId, labelId) pair that genuinely
    // exists but belongs to the OTHER board — the precise "not on this board" case.
    private async Task<(Guid SizeId, Guid LabelId)> CreateOffBoardSizeAndLabelAsync()
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var boardResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = $"Other Board {Guid.NewGuid():N}" });
        boardResponse.EnsureSuccessStatusCode();
        var otherBoard = await boardResponse.Content.ReadFromJsonAsync<JsonElement>();
        var otherBoardId = otherBoard.GetProperty("id").GetGuid();

        var sizeId = await TestDataHelper.GetSizeIdByNameAsync(_client, otherBoardId, "M");

        var labelResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{otherBoardId}/labels", new { name = "OffBoardLabel", color = "#ff0000" });
        labelResponse.EnsureSuccessStatusCode();
        var label = await labelResponse.Content.ReadFromJsonAsync<JsonElement>();
        var labelId = label.GetProperty("id").GetGuid();

        return (sizeId, labelId);
    }

    // ── Parity: whitespace-only name rejected on both surfaces ────────────────

    [Fact]
    public async Task CreateCard_WhitespaceName_RejectedOnBothSurfaces()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var (db, tools, authKey) = CreateMcpTools();

        // Act — REST
        var restResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name = "   ", laneId }
        );

        // Act — MCP
        var mcpResult = await tools.CreateCardAsync(authKey, "   ", laneId);

        // Assert — both reject; MCP persists nothing (the formerly-divergent gap)
        restResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        mcpResult.ShouldContain("Error");
        mcpResult.ShouldContain("Name is required");

        // Assert — the 400 BODY carries each surface's own idiom: REST returns the bare
        // message (no "Error: " prefix — the prior REST contract); MCP keeps its
        // "Error: ..." form. Pins the contract so the shared helper's MCP idiom can't
        // re-bleed into the REST body.
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("Name is required");
        restBody.ShouldNotContain("Error:");
        mcpResult.ShouldContain("Error: Name is required");

        var whitespaceNamed = await db.Cards.AnyAsync(c => c.Name == "   " && c.BoardId == _factory.DefaultBoardId);
        whitespaceNamed.ShouldBeFalse();
    }

    // ── Parity: size not on board rejected on both surfaces ───────────────────

    [Fact]
    public async Task CreateCard_SizeNotOnBoard_RejectedOnBothSurfaces()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var (offBoardSizeId, _) = await CreateOffBoardSizeAndLabelAsync();
        var (_, tools, authKey) = CreateMcpTools();

        // Act — REST
        var restResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name = "Off-board size REST", laneId, sizeId = offBoardSizeId }
        );

        // Act — MCP
        var mcpResult = await tools.CreateCardAsync(authKey, "Off-board size MCP", laneId, sizeId: offBoardSizeId);

        // Assert — both reject
        restResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        mcpResult.ShouldContain("Error");
        mcpResult.ShouldContain("Size");
    }

    // ── Parity: label not on board rejected on both surfaces ──────────────────

    [Fact]
    public async Task CreateCard_LabelNotOnBoard_RejectedOnBothSurfaces()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var (_, offBoardLabelId) = await CreateOffBoardSizeAndLabelAsync();
        var (_, tools, authKey) = CreateMcpTools();

        // Act — REST (labelIds is a typed GUID array on the REST contract)
        var restResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name = "Off-board label REST", laneId, labelIds = new[] { offBoardLabelId } }
        );

        // Act — MCP (labelIds is a CSV string on the MCP contract)
        var mcpResult = await tools.CreateCardAsync(authKey, "Off-board label MCP", laneId, labelIds: offBoardLabelId.ToString());

        // Assert — both reject
        restResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        mcpResult.ShouldContain("Error");
    }

    // ── Parity: size-by-name accepted on both surfaces (ratified REST sizeName) ─

    [Fact]
    public async Task CreateCard_SizeByName_AcceptedOnBothSurfaces()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var (_, tools, authKey) = CreateMcpTools();

        // Act — REST now exposes sizeName on the create contract
        var restResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name = "Size-by-name REST", laneId, sizeName = "L" }
        );

        // Act — MCP
        var mcpResult = await tools.CreateCardAsync(authKey, "Size-by-name MCP", laneId, sizeName: "L");

        // Assert — both create a card whose size resolved to "L"
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var restCard = await restResponse.Content.ReadFromJsonAsync<JsonElement>();
        restCard.GetProperty("sizeName").GetString().ShouldBe("L");

        mcpResult.ShouldNotContain("Error");
        var mcpCard = JsonDocument.Parse(mcpResult);
        mcpCard.RootElement.GetProperty("sizeName").GetString().ShouldBe("L");
    }
}
