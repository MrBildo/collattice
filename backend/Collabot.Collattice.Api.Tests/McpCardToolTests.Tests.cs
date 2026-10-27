using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class McpCardToolTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> GetLaneIdByIndexAsync(int index)
    {
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/board");
        response.EnsureSuccessStatusCode();
        var board = await response.Content.ReadFromJsonAsync<JsonElement>();
        return board.GetProperty("lanes")[index].GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task MoveCard_WithoutIndex_PlacesAtTopOfLane()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var sourceLaneId = await GetLaneIdByIndexAsync(0);
        var targetLaneId = await GetLaneIdByIndexAsync(1);

        // Create two cards in the target lane so there's a defined "end"
        var card1Response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Target Lane Card A",
            descriptionMarkdown = "",
            size = "M",
            laneId = targetLaneId,
            position = 0
        });
        card1Response.EnsureSuccessStatusCode();

        var card2Response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Target Lane Card B",
            descriptionMarkdown = "",
            size = "M",
            laneId = targetLaneId,
            position = 10
        });
        card2Response.EnsureSuccessStatusCode();

        // Create the card to move in the source lane
        var moveCardResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card To Prepend",
            descriptionMarkdown = "",
            size = "M",
            laneId = sourceLaneId,
            position = 0
        });
        moveCardResponse.EnsureSuccessStatusCode();
        var moveCard = await moveCardResponse.Content.ReadFromJsonAsync<JsonElement>();
        var moveCardId = moveCard.GetProperty("id").GetGuid();

        // Act — call MoveCardAsync without specifying index (null)
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var cardTools = new CardTools(db, authService, broadcaster);

        var result = await cardTools.MoveCardAsync
        (
            _factory.AdminAuthKey,
            targetLaneId,
            cardId: moveCardId
        );

        // Assert — card should be at the top (index 0, before the two existing cards)
        result.ShouldContain("moved to lane at index 0");

        // Verify position is less than both existing cards
        var cardsResponse = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?laneId={targetLaneId}");
        cardsResponse.EnsureSuccessStatusCode();
        var paged = await cardsResponse.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        paged.ShouldNotBeNull();
        var cards = paged.Items;

        var movedCard = cards.First(c => c.GetProperty("id").GetGuid() == moveCardId);
        var otherCards = cards.Where(c => c.GetProperty("id").GetGuid() != moveCardId).ToArray();

        foreach (var other in otherCards)
        {
            movedCard.GetProperty("position").GetInt32()
                .ShouldBeLessThan(other.GetProperty("position").GetInt32());
        }
    }

    [Fact]
    public async Task MoveCard_WithExplicitIndex_RespectsPosition()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var sourceLaneId = await GetLaneIdByIndexAsync(0);
        var targetLaneId = await GetLaneIdByIndexAsync(1);

        // Create two cards in the target lane
        var card1Response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Explicit Index Card A",
            descriptionMarkdown = "",
            size = "M",
            laneId = targetLaneId,
            position = 100
        });
        card1Response.EnsureSuccessStatusCode();
        var card1 = await card1Response.Content.ReadFromJsonAsync<JsonElement>();
        var card1Id = card1.GetProperty("id").GetGuid();

        var card2Response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Explicit Index Card B",
            descriptionMarkdown = "",
            size = "M",
            laneId = targetLaneId,
            position = 110
        });
        card2Response.EnsureSuccessStatusCode();

        // Create the card to move
        var moveCardResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card To Insert At Start",
            descriptionMarkdown = "",
            size = "M",
            laneId = sourceLaneId,
            position = 0
        });
        moveCardResponse.EnsureSuccessStatusCode();
        var moveCard = await moveCardResponse.Content.ReadFromJsonAsync<JsonElement>();
        var moveCardId = moveCard.GetProperty("id").GetGuid();

        // Act — call MoveCardAsync with explicit index 0 (beginning)
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var cardTools = new CardTools(db, authService, broadcaster);

        var result = await cardTools.MoveCardAsync
        (
            _factory.AdminAuthKey,
            targetLaneId,
            cardId: moveCardId,
            index: 0
        );

        // Assert — card should be at index 0
        result.ShouldContain("moved to lane at index 0");
    }
}
