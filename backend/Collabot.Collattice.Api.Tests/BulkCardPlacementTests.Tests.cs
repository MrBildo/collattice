using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Where the bulk tools put cards. Each tool stages every move and saves once, so these tests
// read the saved lanes back after the call: the batch sits together in the order it was given,
// no two cards share a position, and every lane it touched holds its cards at 0, 10, 20, ...
// Each test seeds its own board through the REST API, so the lanes hold exactly the cards the
// test names, at the positions the API gives them.
public class BulkCardPlacementTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
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
    public async Task BulkUpdate_TwoCardsToTopOfTheirOwnLane_LandAboveTheRestInBatchOrder()
    {
        // Arrange
        var board = await SeedBoardAsync(["A", "B", "C", "D"]);

        // Act
        var result = await CreateBulkTools().BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: board.Csv("C", "D"),
            laneId: board.Lane1
        );

        // Assert
        result.ShouldContain("\"succeeded\":2");
        await AssertLaneAsync(board, board.Lane1, "C", "D", "A", "B");
    }

    [Fact]
    public async Task BulkUpdate_TwoCardsToAnEmptyLane_GetDistinctPositionsInBatchOrder()
    {
        // Arrange
        var board = await SeedBoardAsync(["A", "B", "C", "D"], []);

        // Act
        var result = await CreateBulkTools().BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: board.Csv("A", "B"),
            laneId: board.Lane2
        );

        // Assert
        result.ShouldContain("\"succeeded\":2");
        await AssertLaneAsync(board, board.Lane2, "A", "B");
        await AssertLaneAsync(board, board.Lane1, "C", "D");
    }

    [Fact]
    public async Task BulkUpdate_LastTwoCardsOfALaneToAnEmptyLane_KeepBatchOrder()
    {
        // Arrange
        var board = await SeedBoardAsync(["A", "B", "C"], []);

        // Act
        var result = await CreateBulkTools().BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: board.Csv("B", "C"),
            laneId: board.Lane2
        );

        // Assert
        result.ShouldContain("\"succeeded\":2");
        await AssertLaneAsync(board, board.Lane2, "B", "C");
        await AssertLaneAsync(board, board.Lane1, "A");
    }

    [Fact]
    public async Task BulkUpdate_TwoCardsAtAnIndexInAnOccupiedLane_LandTogetherAtThatIndex()
    {
        // Arrange
        var board = await SeedBoardAsync(["A", "B", "C", "D"], ["X", "Y", "Z"]);

        // Act
        var result = await CreateBulkTools().BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: board.Csv("A", "B"),
            laneId: board.Lane2,
            index: 1
        );

        // Assert
        result.ShouldContain("\"succeeded\":2");
        await AssertLaneAsync(board, board.Lane2, "X", "A", "B", "Y", "Z");
        await AssertLaneAsync(board, board.Lane1, "C", "D");
    }

    [Fact]
    public async Task BulkUpdate_SameLaneBatchOnBothSidesOfTheIndex_CountsTheIndexAmongTheOtherCards()
    {
        // Arrange — D sits after index 1 and A before it; the other cards are B and C
        var board = await SeedBoardAsync(["A", "B", "C", "D"]);

        // Act
        var result = await CreateBulkTools().BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: board.Csv("D", "A"),
            laneId: board.Lane1,
            index: 1
        );

        // Assert
        result.ShouldContain("\"succeeded\":2");
        await AssertLaneAsync(board, board.Lane1, "B", "D", "A", "C");
    }

    [Fact]
    public async Task BulkUpdate_BatchFromTwoLanesWithIndexPastTheEnd_AppendsInBatchOrder()
    {
        // Arrange — X is already in the target lane and is part of the batch
        var board = await SeedBoardAsync(["A", "B", "C", "D"], ["X"]);

        // Act
        var result = await CreateBulkTools().BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: board.Csv("B", "X", "D"),
            laneId: board.Lane2,
            index: 99
        );

        // Assert
        result.ShouldContain("\"succeeded\":3");
        await AssertLaneAsync(board, board.Lane2, "B", "X", "D");
        await AssertLaneAsync(board, board.Lane1, "A", "C");
    }

    [Fact]
    public async Task BulkUpdate_SameCardListedTwice_PlacesItOnce()
    {
        // Arrange
        var board = await SeedBoardAsync(["A", "B", "C"], []);

        // Act
        var result = await CreateBulkTools().BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: board.Csv("A", "A", "B"),
            laneId: board.Lane2
        );

        // Assert
        result.ShouldContain("\"succeeded\":3");
        await AssertLaneAsync(board, board.Lane2, "A", "B");
    }

    [Fact]
    public async Task BulkUpdate_BatchWithAnArchivedCard_PlacesOnlyTheCardsThatSucceeded()
    {
        // Arrange
        var board = await SeedBoardAsync(["A", "B", "C"], ["X"]);
        await ArchiveViaRestAsync(board.Ids["B"]);

        // Act
        var result = await CreateBulkTools().BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: board.Csv("A", "B", "C"),
            laneId: board.Lane2
        );

        // Assert — B is reported as an error and stays archived
        result.ShouldContain("\"succeeded\":2");
        await AssertLaneAsync(board, board.Lane2, "A", "C", "X");
        (await PlacementAsync(board.Ids["B"])).LaneId.ShouldBe(board.ArchiveLane);
    }

    [Fact]
    public async Task BulkRestore_TwoCardsIntoAnOccupiedLane_LandAtTheTopInBatchOrder()
    {
        // Arrange
        var board = await SeedBoardAsync(["A", "B", "C"], ["X"]);
        await ArchiveViaRestAsync(board.Ids["A"]);
        await ArchiveViaRestAsync(board.Ids["B"]);
        await ArchiveViaRestAsync(board.Ids["C"]);

        // Act
        var result = await CreateBulkTools().BulkRestoreCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            board.Lane2,
            cardIds: board.Csv("A", "B")
        );

        // Assert
        result.ShouldContain("\"succeeded\":2");
        await AssertLaneAsync(board, board.Lane2, "A", "B", "X");
        await AssertLaneAsync(board, board.ArchiveLane, "C");
    }

    [Fact]
    public async Task BulkArchive_TwoCards_GetDistinctArchivePositionsInBatchOrder()
    {
        // Arrange
        var board = await SeedBoardAsync(["A", "B", "C", "D"]);

        // Act
        var result = await CreateBulkTools().BulkArchiveCardsAsync(CollatticeApiFactory.TestAdminAuthKey, cardIds: board.Csv("A", "B"));

        // Assert
        result.ShouldContain("\"succeeded\":2");
        await AssertLaneAsync(board, board.ArchiveLane, "A", "B");
        await AssertLaneAsync(board, board.Lane1, "C", "D");
    }

    [Fact]
    public async Task BulkArchive_CardsFromTwoBoards_EachLandInTheirOwnBoardsArchiveLane()
    {
        // Arrange
        var first = await SeedBoardAsync(["A", "B"]);
        var second = await SeedBoardAsync(["P", "Q"]);

        // Act
        var result = await CreateBulkTools().BulkArchiveCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: $"{first.Csv("A")},{second.Csv("P", "Q")},{first.Csv("B")}"
        );

        // Assert
        result.ShouldContain("\"succeeded\":4");
        await AssertLaneAsync(first, first.ArchiveLane, "A", "B");
        await AssertLaneAsync(second, second.ArchiveLane, "P", "Q");
    }

    [Fact]
    public async Task Prune_ArchivingAWholeLane_GivesEachCardItsOwnArchivePosition()
    {
        // Arrange
        var board = await SeedBoardAsync(["A", "B", "C"], ["X"]);
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{board.BoardId}/prune", new { laneIds = new[] { board.Lane1 } });

        // Assert — prune's match query has no order, so only the set and the positions are pinned
        response.EnsureSuccessStatusCode();
        var archived = await LaneAsync(board.ArchiveLane);
        archived.Select(c => c.Id).ShouldBe([board.Ids["A"], board.Ids["B"], board.Ids["C"]], ignoreOrder: true);
        archived.Select(c => c.Position).ShouldBe([0, 10, 20]);
        await AssertLaneAsync(board, board.Lane2, "X");
    }

    private BulkCardTools CreateBulkTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var sink = scope.ServiceProvider.GetRequiredService<IWebhookSink>();
        var auth = new McpAuthService(new UserResolver(db));
        return new BulkCardTools(db, auth, broadcaster, sink);
    }

    // A fresh board with one lane per card list, the cards created in order so each lane holds
    // them at 0, 10, 20, ...
    private async Task<SeededBoard> SeedBoardAsync(params string[][] lanes)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var boardId = await PostForIdAsync("/api/v1/boards", new { name = $"Placement {Guid.NewGuid():N}" });

        List<Guid> laneIds = [];
        Dictionary<string, Guid> ids = [];
        foreach (var cards in lanes)
        {
            var laneId = await PostForIdAsync($"/api/v1/boards/{boardId}/lanes", new { name = $"Lane {(laneIds.Count + 1).ToString(CultureInfo.InvariantCulture)}" });
            laneIds.Add(laneId);

            foreach (var name in cards)
            {
                ids[name] = await PostForIdAsync($"/api/v1/boards/{boardId}/cards", new { name, laneId });
            }
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var archiveLaneId = await db.Lanes
            .Where(l => l.BoardId == boardId && l.IsArchiveLane)
                .Select(l => l.Id)
                    .SingleAsync();

        var board = new SeededBoard(boardId, laneIds, archiveLaneId, ids);
        for (var i = 0; i < lanes.Length; i++)
        {
            await AssertLaneAsync(board, laneIds[i], lanes[i]);
        }

        return board;
    }

    private async Task<Guid> PostForIdAsync(string path, object body)
    {
        var response = await _client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    private async Task ArchiveViaRestAsync(Guid cardId)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);
        response.EnsureSuccessStatusCode();
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

    private async Task<(Guid LaneId, int Position)> PlacementAsync(Guid cardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var card = await db.Cards.SingleAsync(c => c.Id == cardId);
        return (card.LaneId, card.Position);
    }

    // The lane holds exactly these cards, in this order, at 0, 10, 20, ...
    private async Task AssertLaneAsync(SeededBoard board, Guid laneId, params string[] expected)
    {
        var lane = await LaneAsync(laneId);
        var names = board.Ids.ToDictionary(pair => pair.Value, pair => pair.Key);

        lane.Select(c => names[c.Id]).ShouldBe(expected);
        lane.Select(c => c.Position).ShouldBe(expected.Select((_, i) => i * 10));
    }

    private sealed record SeededBoard(Guid BoardId, List<Guid> Lanes, Guid ArchiveLane, Dictionary<string, Guid> Ids)
    {
        public Guid Lane1 => Lanes[0];

        public Guid Lane2 => Lanes[1];

        public string Csv(params string[] names) => string.Join(',', names.Select(name => Ids[name]));
    }
}
