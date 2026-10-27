using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class PruneEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> GetFirstLaneIdAsync()
        => await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

    private async Task<Guid> GetLaneIdByIndexAsync(int index)
        => await TestDataHelper.GetLaneIdByIndexAsync(_client, _factory.DefaultBoardId, index);

    private async Task<Guid> CreateCardAsync(Guid? laneId = null, int daysOld = 0)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var actualLaneId = laneId ?? await GetFirstLaneIdAsync();

        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = $"Prune Test Card {Guid.NewGuid():N}",
            descriptionMarkdown = "",
            laneId = actualLaneId,
            position = Random.Shared.Next(10000, 99999),
        });
        response.EnsureSuccessStatusCode();

        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = card.GetProperty("id").GetGuid();

        if (daysOld > 0)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
            var entity = await db.Cards.FindAsync(cardId);
            entity!.LastUpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-daysOld);
            await db.SaveChangesAsync();
        }

        return cardId;
    }

    private async Task<Guid> CreateLabelAsync()
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels", new
        {
            name = $"Prune Label {Guid.NewGuid():N}",
            color = "#ff0000",
        });
        response.EnsureSuccessStatusCode();

        var label = await response.Content.ReadFromJsonAsync<JsonElement>();
        return label.GetProperty("id").GetGuid();
    }

    private async Task AssignLabelAsync(Guid cardId, Guid labelId)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Preview_WithOlderThanFilter_ReturnsMatchingCards()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var oldCardId = await CreateCardAsync(daysOld: 60);
        var recentCardId = await CreateCardAsync(daysOld: 0);

        var olderThan = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune/preview",
            new { olderThan }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var matchCount = result.GetProperty("matchCount").GetInt32();
        matchCount.ShouldBeGreaterThanOrEqualTo(1);

        var cards = result.GetProperty("cards").EnumerateArray().ToList();
        cards.ShouldContain(c => c.GetProperty("id").GetGuid() == oldCardId);
        cards.ShouldNotContain(c => c.GetProperty("id").GetGuid() == recentCardId);
    }

    [Fact]
    public async Task Preview_WithLaneFilter_ReturnsCardsInSpecifiedLanes()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var lane0Id = await GetLaneIdByIndexAsync(0);
        var lane1Id = await GetLaneIdByIndexAsync(1);

        var cardInLane0 = await CreateCardAsync(laneId: lane0Id);
        var cardInLane1 = await CreateCardAsync(laneId: lane1Id);

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune/preview",
            new { laneIds = new[] { lane1Id } }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cards = result.GetProperty("cards").EnumerateArray().ToList();
        cards.ShouldContain(c => c.GetProperty("id").GetGuid() == cardInLane1);
        cards.ShouldNotContain(c => c.GetProperty("id").GetGuid() == cardInLane0);
    }

    [Fact]
    public async Task Preview_WithLabelFilter_ReturnsCardsWithSpecifiedLabels()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var labelId = await CreateLabelAsync();

        var cardWithLabel = await CreateCardAsync();
        await AssignLabelAsync(cardWithLabel, labelId);

        var cardWithoutLabel = await CreateCardAsync();

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune/preview",
            new { labelIds = new[] { labelId } }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cards = result.GetProperty("cards").EnumerateArray().ToList();
        cards.ShouldContain(c => c.GetProperty("id").GetGuid() == cardWithLabel);
        cards.ShouldNotContain(c => c.GetProperty("id").GetGuid() == cardWithoutLabel);
    }

    [Fact]
    public async Task Preview_WithCombinedFilters_ReturnsCardsMatchingAll()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var lane1Id = await GetLaneIdByIndexAsync(1);
        var lane0Id = await GetLaneIdByIndexAsync(0);

        // Old card in lane1 — matches both filters
        var oldCardInLane1 = await CreateCardAsync(laneId: lane1Id, daysOld: 60);

        // Recent card in lane1 — matches lane but not olderThan
        var recentCardInLane1 = await CreateCardAsync(laneId: lane1Id, daysOld: 0);

        // Old card in lane0 — matches olderThan but not lane
        var oldCardInLane0 = await CreateCardAsync(laneId: lane0Id, daysOld: 60);

        var olderThan = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune/preview",
            new { olderThan, laneIds = new[] { lane1Id } }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cards = result.GetProperty("cards").EnumerateArray().ToList();
        cards.ShouldContain(c => c.GetProperty("id").GetGuid() == oldCardInLane1);
        cards.ShouldNotContain(c => c.GetProperty("id").GetGuid() == recentCardInLane1);
        cards.ShouldNotContain(c => c.GetProperty("id").GetGuid() == oldCardInLane0);
    }

    [Fact]
    public async Task Preview_NoFilters_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune/preview",
            new { }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Preview_NonAdmin_Returns403()
    {
        // Arrange
        var human = await TestAuthHelper.CreateUserAsync(_client, _factory, "Prune Non-Admin", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, human.AuthKey);

        var olderThan = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune/preview",
            new { olderThan }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Prune_OldAndRecentCards_DeletesMatchingCards()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var oldCardId = await CreateCardAsync(daysOld: 60);
        var recentCardId = await CreateCardAsync(daysOld: 0);

        var olderThan = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune",
            new { olderThan, action = "delete" }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("deletedCount").GetInt32().ShouldBeGreaterThanOrEqualTo(1);

        // Verify old card is gone
        var getOldCard = await _client.GetAsync($"/api/v1/cards/{oldCardId}");
        getOldCard.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Verify recent card still exists
        var getRecentCard = await _client.GetAsync($"/api/v1/cards/{recentCardId}");
        getRecentCard.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Prune_CardWithComment_CascadesDeleteToComments()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync(daysOld: 60);

        // Add a comment to the card
        var commentResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/cards/{cardId}/comments",
            new { contentMarkdown = "Comment on card to be pruned" }
        );
        commentResponse.EnsureSuccessStatusCode();
        var comment = await commentResponse.Content.ReadFromJsonAsync<JsonElement>();
        var commentId = comment.GetProperty("id").GetGuid();

        var olderThan = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune",
            new { olderThan, action = "delete" }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("deletedCount").GetInt32().ShouldBeGreaterThanOrEqualTo(1);

        // Verify card is gone
        var getCard = await _client.GetAsync($"/api/v1/cards/{cardId}");
        getCard.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Verify the comment row cascade-deleted with its card. There is no
        // GET /comments/{id} endpoint, so assert the row is gone via the DbContext —
        // same pattern as DeleteLane_WithCardAndChildren_CascadesToCardsAndTheirChildren. (An earlier
        // GET /api/v1/comments/{id} assertion silently fell through to the SPA
        // fallback, which 404s in CI's frontend-less checkout but 200s locally once
        // wwwroot/index.html exists — masking the real cascade signal either way.)
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var commentEntity = await db.Comments.FindAsync(commentId);
        commentEntity.ShouldBeNull();
    }

    [Fact]
    public async Task Prune_NoMatchingCards_ReturnsZero()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var fakeLaneId = Guid.NewGuid();

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune",
            new { laneIds = new[] { fakeLaneId }, action = "delete" }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("deletedCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Preview_BoardNotFound_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var fakeBoardId = Guid.NewGuid();
        var olderThan = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{fakeBoardId}/prune/preview",
            new { olderThan }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
