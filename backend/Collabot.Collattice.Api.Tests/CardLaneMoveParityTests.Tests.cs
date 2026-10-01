using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Collabot.Collattice.Api.Auth;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Mcp;
using Collabot.Collattice.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Moving a card to another lane through REST PATCH /cards/{id} and through MCP update_card is the
// same operation, so it must leave the board numbered the same way. Each test makes the same move
// through both surfaces, each on its own freshly seeded board, and compares the two boards lane by
// lane. The target lane is seeded with a gap (a card deleted from its middle), so a surface that
// renumbered only the source lane, or neither, shows up as a difference.
public class CardLaneMoveParityTests(WebhookTestFactory factory) : IClassFixture<WebhookTestFactory>, IDisposable
{
    private readonly WebhookTestFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly List<IServiceScope> _scopes = [];

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LaneMove_WithoutPosition_RestAppendsAndNumbersBothLanesAsMcpDoes()
    {
        // Arrange
        var rest = await SeedGappedBoardAsync();
        var mcp = await SeedGappedBoardAsync();

        // Act — PATCH with no position appends; the MCP equivalent is the index past the last card
        var response = await PatchAsync(rest.Ids["B"], new { laneId = rest.Lane2 });
        var result = await CreateCardTools().UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: mcp.Ids["B"], laneId: mcp.Lane2, index: 2);

        // Assert
        response.EnsureSuccessStatusCode();
        result.ShouldNotContain("Error");

        await AssertLaneAsync(rest, rest.Lane1, "A", "C", "D");
        await AssertLaneAsync(rest, rest.Lane2, "X", "Z", "B");
        await AssertSameNumberingAsync(rest, mcp);
    }

    // A PATCH position is a stored position number, not an index. Across lanes the card goes before
    // the first card whose number is not below the one sent, which in the target lane X=0, Z=20 is:
    // below 0 → top; 5 or 20 → between X and Z; above 20 → bottom.
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(5, 1)]
    [InlineData(20, 1)]
    [InlineData(25, 2)]
    public async Task LaneMove_WithPosition_RestLandsWhereThatNumberSortsAndMatchesMcp(int position, int expectedIndex)
    {
        // Arrange
        var rest = await SeedGappedBoardAsync();
        var mcp = await SeedGappedBoardAsync();

        // Act
        var response = await PatchAsync(rest.Ids["B"], new { laneId = rest.Lane2, position });
        var result = await CreateCardTools().UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: mcp.Ids["B"], laneId: mcp.Lane2, index: expectedIndex);

        // Assert
        response.EnsureSuccessStatusCode();
        result.ShouldNotContain("Error");

        List<string> expected = ["X", "Z"];
        expected.Insert(expectedIndex, "B");

        await AssertLaneAsync(rest, rest.Lane1, "A", "C", "D");
        await AssertLaneAsync(rest, rest.Lane2, [.. expected]);
        await AssertSameNumberingAsync(rest, mcp);
    }

    [Fact]
    public async Task LaneMove_RestAndMcp_EmitTheSameSingleCardMoved()
    {
        // Arrange
        var rest = await SeedGappedBoardAsync();
        var mcp = await SeedGappedBoardAsync();

        // Act
        _factory.Sink.Clear();
        var response = await PatchAsync(rest.Ids["B"], new { laneId = rest.Lane2 });
        var restEvents = _factory.Sink.Captured.ToList();

        _factory.Sink.Clear();
        var result = await CreateCardTools().UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: mcp.Ids["B"], laneId: mcp.Lane2, index: 2);
        var mcpEvents = _factory.Sink.Captured.ToList();

        // Assert — one card.moved each, from B's old place to where it now sits
        response.EnsureSuccessStatusCode();
        result.ShouldNotContain("Error");

        AssertSingleMove(restEvents, rest.Lane1, rest.Lane2);
        AssertSingleMove(mcpEvents, mcp.Lane1, mcp.Lane2);
    }

    // Re-sending the card's own lane is not a move to another lane, and keeps PATCH's own rules:
    // a position is stored as given, no position re-appends after the lane's highest number, and
    // neither renumbers anything else.
    [Fact]
    public async Task SameLane_WithPosition_StoresTheNumberAsGivenAndRenumbersNothing()
    {
        // Arrange
        var board = await SeedGappedBoardAsync();

        // Act
        var response = await PatchAsync(board.Ids["B"], new { laneId = board.Lane1, position = 15 });

        // Assert
        response.EnsureSuccessStatusCode();

        (await NamedLaneAsync(board, board.Lane1)).ShouldBe(["A=0", "B=15", "C=20", "D=30"]);
        (await NamedLaneAsync(board, board.Lane2)).ShouldBe(["X=0", "Z=20"]);
    }

    [Fact]
    public async Task SameLane_WithoutPosition_ReappendsAfterTheHighestNumberAndRenumbersNothing()
    {
        // Arrange
        var board = await SeedGappedBoardAsync();

        // Act
        var response = await PatchAsync(board.Ids["B"], new { laneId = board.Lane1 });

        // Assert
        response.EnsureSuccessStatusCode();

        (await NamedLaneAsync(board, board.Lane1)).ShouldBe(["A=0", "C=20", "D=30", "B=40"]);
        (await NamedLaneAsync(board, board.Lane2)).ShouldBe(["X=0", "Z=20"]);
    }

    private static void AssertSingleMove(List<BoardEvent> events, Guid fromLaneId, Guid toLaneId)
    {
        events.Select(e => e.EventType).ShouldBe(["card.moved"]);

        var data = JsonSerializer.SerializeToElement(events[0], JsonSerializerOptions.Web).GetProperty("data");

        data.GetProperty("from").GetProperty("laneId").GetGuid().ShouldBe(fromLaneId);
        data.GetProperty("from").GetProperty("position").GetInt32().ShouldBe(10);
        data.GetProperty("to").GetProperty("laneId").GetGuid().ShouldBe(toLaneId);
        data.GetProperty("to").GetProperty("position").GetInt32().ShouldBe(20);
    }

    private CardTools CreateCardTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);

        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var auth = new McpAuthService(new UserResolver(db));

        return new CardTools(db, auth, broadcaster);
    }

    private async Task<HttpResponseMessage> PatchAsync(Guid cardId, object body)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        return await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", body);
    }

    // A fresh board: lane 1 holds A, B, C, D at 0, 10, 20, 30; lane 2 holds X at 0 and Z at 20,
    // the gap left by deleting Y.
    private async Task<SeededBoard> SeedGappedBoardAsync()
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var boardId = await PostForIdAsync("/api/v1/boards", new { name = $"Lane move {Guid.NewGuid():N}" });

        List<Guid> laneIds = [];
        Dictionary<string, Guid> ids = [];
        foreach (var cards in new[] { new[] { "A", "B", "C", "D" }, ["X", "Y", "Z"] })
        {
            var laneId = await PostForIdAsync($"/api/v1/boards/{boardId}/lanes", new { name = $"Lane {(laneIds.Count + 1).ToString(CultureInfo.InvariantCulture)}" });
            laneIds.Add(laneId);

            foreach (var name in cards)
            {
                ids[name] = await PostForIdAsync($"/api/v1/boards/{boardId}/cards", new { name, laneId });
            }
        }

        var deleted = await _client.DeleteAsync($"/api/v1/cards/{ids["Y"]}");
        deleted.EnsureSuccessStatusCode();
        ids.Remove("Y");

        var board = new SeededBoard(laneIds, ids);

        (await LaneAsync(board.Lane1)).Select(c => c.Position).ShouldBe([0, 10, 20, 30]);
        (await LaneAsync(board.Lane2)).Select(c => c.Position).ShouldBe([0, 20]);

        return board;
    }

    private async Task<Guid> PostForIdAsync(string path, object body)
    {
        var response = await _client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);

        return json.GetProperty("id").GetGuid();
    }

    // The lane's saved cards in position order.
    private async Task<List<(Guid Id, int Position)>> LaneAsync(Guid laneId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var cards = await db.Cards
            .Where(c => c.LaneId == laneId)
            .OrderBy(c => c.Position)
                .Select(c => new { c.Id, c.Position })
                    .ToListAsync();

        return [.. cards.Select(c => (c.Id, c.Position))];
    }

    // The lane holds exactly these cards, in this order, at 0, 10, 20, ...
    private async Task AssertLaneAsync(SeededBoard board, Guid laneId, params string[] expected)
    {
        var lane = await LaneAsync(laneId);
        var names = board.Ids.ToDictionary(pair => pair.Value, pair => pair.Key);

        lane.Select(c => names[c.Id]).ShouldBe(expected);
        lane.Select(c => c.Position).ShouldBe(expected.Select((_, i) => i * 10));
    }

    // Both boards hold the same named cards at the same positions, lane by lane.
    private async Task AssertSameNumberingAsync(SeededBoard rest, SeededBoard mcp)
    {
        for (var i = 0; i < rest.Lanes.Count; i++)
        {
            (await NamedLaneAsync(rest, rest.Lanes[i])).ShouldBe(await NamedLaneAsync(mcp, mcp.Lanes[i]));
        }
    }

    private async Task<List<string>> NamedLaneAsync(SeededBoard board, Guid laneId)
    {
        var names = board.Ids.ToDictionary(pair => pair.Value, pair => pair.Key);

        return [.. (await LaneAsync(laneId)).Select(c => $"{names[c.Id]}={c.Position.ToString(CultureInfo.InvariantCulture)}")];
    }

    private sealed record SeededBoard(List<Guid> Lanes, Dictionary<string, Guid> Ids)
    {
        public Guid Lane1 => Lanes[0];

        public Guid Lane2 => Lanes[1];
    }
}
