using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// A draft (a card started in the create dialog and not yet saved) is hidden from every card read,
// so an index in a move means a place among the cards a caller can see. Every test seeds a board
// whose first lane shows A, B, C with a draft D started between B and C, and whose second lane
// shows X, then moves a card through one surface and reads back the lane's saved cards: their
// order, and their numbers at 0, 10, 20, ...
public class DraftLanePlacementTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
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
    public async Task Reorder_WithinTheLaneToTheLastVisibleIndex_LandsLast()
    {
        // Arrange
        var board = await SeedBoardAsync();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{board.Ids["A"]}/reorder", new { laneId = board.Lane1, index = 2 });

        // Assert
        response.EnsureSuccessStatusCode();
        await AssertVisibleLaneAsync(board, board.Lane1, "B", "C", "A");
    }

    [Fact]
    public async Task Reorder_FromAnotherLaneToTheEndIndex_LandsLast()
    {
        // Arrange
        var board = await SeedBoardAsync();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{board.Ids["X"]}/reorder", new { laneId = board.Lane1, index = 3 });

        // Assert
        response.EnsureSuccessStatusCode();
        await AssertVisibleLaneAsync(board, board.Lane1, "A", "B", "C", "X");
        await AssertVisibleLaneAsync(board, board.Lane2);
    }

    [Fact]
    public async Task McpMoveCard_ToTheLastVisibleIndex_LandsLastAndReportsThatIndex()
    {
        // Arrange
        var board = await SeedBoardAsync();

        // Act
        var result = await CreateCardTools().MoveCardAsync(CollatticeApiFactory.TestAdminAuthKey, board.Lane1, cardId: board.Ids["A"], index: 2);

        // Assert
        result.ShouldBe("Card 'A' moved to lane at index 2.");
        await AssertVisibleLaneAsync(board, board.Lane1, "B", "C", "A");
    }

    [Fact]
    public async Task McpMoveCard_PastTheVisibleEnd_ReportsTheIndexItLandedAt()
    {
        // Arrange
        var board = await SeedBoardAsync();

        // Act
        var result = await CreateCardTools().MoveCardAsync(CollatticeApiFactory.TestAdminAuthKey, board.Lane1, cardId: board.Ids["X"], index: 99);

        // Assert
        result.ShouldBe("Card 'X' moved to lane at index 3.");
        await AssertVisibleLaneAsync(board, board.Lane1, "A", "B", "C", "X");
    }

    [Fact]
    public async Task McpUpdateCard_FromAnotherLaneToTheEndIndex_LandsLast()
    {
        // Arrange
        var board = await SeedBoardAsync();

        // Act
        var result = await CreateCardTools().UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: board.Ids["X"], laneId: board.Lane1, index: 3);

        // Assert
        result.ShouldNotStartWith("Error");
        await AssertVisibleLaneAsync(board, board.Lane1, "A", "B", "C", "X");
    }

    [Fact]
    public async Task McpBulkUpdate_WithinTheLaneToTheLastVisibleIndex_LandsLast()
    {
        // Arrange
        var board = await SeedBoardAsync();

        // Act
        var result = await CreateBulkTools().BulkUpdateCardsAsync(CollatticeApiFactory.TestAdminAuthKey, cardIds: board.Ids["A"].ToString(), laneId: board.Lane1, index: 2);

        // Assert
        result.ShouldContain("\"succeeded\":1");
        await AssertVisibleLaneAsync(board, board.Lane1, "B", "C", "A");
    }

    // PATCH places by number rather than by index, and that already put the card in the right
    // order with a draft in the lane. What a draft did change is the numbering: it kept a slot, so
    // the saved cards were left with a gap.
    [Fact]
    public async Task PatchLaneMove_WithNoPosition_LandsLastWithNoGap()
    {
        // Arrange
        var board = await SeedBoardAsync();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{board.Ids["X"]}", new { laneId = board.Lane1 });

        // Assert
        response.EnsureSuccessStatusCode();
        await AssertVisibleLaneAsync(board, board.Lane1, "A", "B", "C", "X");
    }

    [Fact]
    public async Task PatchLaneMove_WithANeighboursPosition_LandsAheadOfItWithNoGap()
    {
        // Arrange — C is at 20, so the card goes ahead of C
        var board = await SeedBoardAsync();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{board.Ids["X"]}", new { laneId = board.Lane1, position = 20 });

        // Assert
        response.EnsureSuccessStatusCode();
        await AssertVisibleLaneAsync(board, board.Lane1, "A", "B", "X", "C");
    }

    [Fact]
    public async Task Finalize_ADraftAheadOfASavedCard_PutsTheCardAtTheEndOfTheLane()
    {
        // Arrange
        var board = await SeedBoardAsync();

        // Act
        var response = await _client.PostAsync($"/api/v1/cards/{board.Ids["D"]}/finalize", null);

        // Assert
        response.EnsureSuccessStatusCode();
        await AssertVisibleLaneAsync(board, board.Lane1, "A", "B", "C", "D");
    }

    [Fact]
    public async Task Create_AfterADraftAtTheEndOfTheLane_TakesTheNextNumberAfterTheSavedCards()
    {
        // Arrange — a second draft at the end of the lane, after C
        var board = await SeedBoardAsync();
        var boardId = (await CardAsync(board.Ids["A"])).BoardId;
        board.Ids["E"] = await PostForIdAsync($"/api/v1/boards/{boardId}/cards/temp", new { name = "E", laneId = board.Lane1, position = 40 });

        // Act
        board.Ids["F"] = await PostForIdAsync($"/api/v1/boards/{boardId}/cards", new { name = "F", laneId = board.Lane1 });

        // Assert
        await AssertVisibleLaneAsync(board, board.Lane1, "A", "B", "C", "F");
    }

    [Fact]
    public async Task Reorder_WithADraftInTheLane_LeavesTheDraftsNumberAlone()
    {
        // Arrange
        var board = await SeedBoardAsync();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{board.Ids["A"]}/reorder", new { laneId = board.Lane1, index = 2 });

        // Assert
        response.EnsureSuccessStatusCode();
        var draft = await CardAsync(board.Ids["D"]);
        (draft.IsTemp, draft.LaneId, draft.Position).ShouldBe((true, board.Lane1, 15));
    }

    private CardTools CreateCardTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        return new CardTools(db, auth, broadcaster);
    }

    private BulkCardTools CreateBulkTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var sink = scope.ServiceProvider.GetRequiredService<IWebhookSink>();
        return new BulkCardTools(db, auth, broadcaster, sink);
    }

    // A fresh board: the first lane holds A, B and C at 0, 10 and 20 with a draft D at 15, between
    // B and C; the second lane holds X. The draft is given its number so that it sits between two
    // saved cards however the API numbers a new card.
    private async Task<SeededBoard> SeedBoardAsync()
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var boardId = await PostForIdAsync("/api/v1/boards", new { name = $"Draft placement {Guid.NewGuid():N}" });
        var lane1 = await PostForIdAsync($"/api/v1/boards/{boardId}/lanes", new { name = "Lane 1" });
        var lane2 = await PostForIdAsync($"/api/v1/boards/{boardId}/lanes", new { name = "Lane 2" });

        Dictionary<string, Guid> ids = [];
        foreach (var name in new[] { "A", "B", "C" })
        {
            ids[name] = await PostForIdAsync($"/api/v1/boards/{boardId}/cards", new { name, laneId = lane1 });
        }

        ids["D"] = await PostForIdAsync($"/api/v1/boards/{boardId}/cards/temp", new { name = "D", laneId = lane1, position = 15 });
        ids["X"] = await PostForIdAsync($"/api/v1/boards/{boardId}/cards", new { name = "X", laneId = lane2 });

        var board = new SeededBoard(lane1, lane2, ids);

        await AssertVisibleLaneAsync(board, lane1, "A", "B", "C");

        var draft = await CardAsync(ids["D"]);
        (draft.IsTemp, draft.Position).ShouldBe((true, 15));

        return board;
    }

    private async Task<Guid> PostForIdAsync(string path, object body)
    {
        var response = await _client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    private async Task<CardItem> CardAsync(Guid cardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        return await db.Cards
            .AsNoTracking()
                .SingleAsync(c => c.Id == cardId);
    }

    // The lane's saved cards are exactly these, in this order, at 0, 10, 20, ...
    private async Task AssertVisibleLaneAsync(SeededBoard board, Guid laneId, params string[] expected)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var lane = await db.Cards
            .Where(c => c.LaneId == laneId && !c.IsTemp)
            .OrderBy(c => c.Position)
                .Select(c => new { c.Id, c.Position })
                    .ToListAsync();

        var names = board.Ids.ToDictionary(pair => pair.Value, pair => pair.Key);

        lane.Select(c => names[c.Id]).ShouldBe(expected);
        lane.Select(c => c.Position).ShouldBe(expected.Select((_, i) => i * 10));
    }

    private sealed record SeededBoard(Guid Lane1, Guid Lane2, Dictionary<string, Guid> Ids);
}
