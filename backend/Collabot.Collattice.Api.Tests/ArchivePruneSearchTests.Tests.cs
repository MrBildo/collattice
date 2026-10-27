using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class ArchivePruneSearchTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private static JsonSerializerOptions JsonOptions => TestAuthHelper.JsonOptions;

    // ── Prune: archive action ──

    [Fact]
    public async Task Prune_ArchiveAction_MovesCardsToArchiveLane()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Prune Archive Test", daysOld: 60);

        var olderThan = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune",
            new { olderThan, action = "archive" }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("archivedCount").GetInt32().ShouldBeGreaterThanOrEqualTo(1);

        // Card should still exist but be in archive lane
        var getCard = await _client.GetAsync($"/api/v1/cards/{cardId}");
        getCard.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await getCard.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        detail.GetProperty("isArchived").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Prune_DeleteAction_DeletesCards()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Prune Delete Test", daysOld: 60);

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

        // Card should be gone
        var getCard = await _client.GetAsync($"/api/v1/cards/{cardId}");
        getCard.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Prune_DefaultAction_ArchivesCards()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Prune Default Action Test", daysOld: 60);

        var olderThan = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");

        // Act — no action specified, should default to archive
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune",
            new { olderThan }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("archivedCount").GetInt32().ShouldBeGreaterThanOrEqualTo(1);

        // Card should still exist in archive lane
        var getCard = await _client.GetAsync($"/api/v1/cards/{cardId}");
        getCard.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await getCard.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        detail.GetProperty("isArchived").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Prune_IncludeArchivedTrue_IncludesArchivedCardsInFilter()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Prune IncludeArchived Test");

        // Archive the card first
        await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);

        // Archiving updates LastUpdatedAtUtc, so backdate it again
        await BackdateCardAsync(cardId, 60);

        var olderThan = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");

        // Act — delete with includeArchived=true should find the archived card
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune",
            new { olderThan, action = "delete", includeArchived = true }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("deletedCount").GetInt32().ShouldBeGreaterThanOrEqualTo(1);

        // Card should be gone
        var getCard = await _client.GetAsync($"/api/v1/cards/{cardId}");
        getCard.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Prune_IncludeArchivedFalse_ExcludesArchivedCards()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Prune ExcludeArchived Test");

        // Archive the card first
        await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);

        // Backdate so it matches the olderThan filter
        await BackdateCardAsync(cardId, 60);

        var olderThan = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");

        // Act — preview without includeArchived should NOT find the archived card
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune/preview",
            new { olderThan }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cards = result.GetProperty("cards").EnumerateArray().ToList();
        cards.ShouldNotContain(c => c.GetProperty("id").GetGuid() == cardId);
    }

    [Fact]
    public async Task Prune_InvalidAction_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var olderThan = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune",
            new { olderThan, action = "invalid" }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PrunePreview_ArchivedCardMatchingFilter_RespectsIncludeArchived()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Preview IncludeArchived Test");

        // Archive the card
        await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);

        // Backdate so it matches the olderThan filter
        await BackdateCardAsync(cardId, 60);

        var olderThan = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");

        // Act — preview with includeArchived=true should find the archived card
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune/preview",
            new { olderThan, includeArchived = true }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cards = result.GetProperty("cards").EnumerateArray().ToList();
        cards.ShouldContain(c => c.GetProperty("id").GetGuid() == cardId);
    }

    // ── Search: archive filtering ──

    [Fact]
    public async Task Search_WithoutArchiveBoardId_ExcludesArchivedCards()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "SearchArchiveExclude_UniqueXyz123");

        // Archive the card
        await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);

        // Act — search without archiveBoardId
        var response = await _client.GetAsync("/api/v1/search/cards?q=SearchArchiveExclude_UniqueXyz123");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        results.ShouldNotBeNull();

        // The archived card should NOT appear
        var allCards = results.SelectMany(r => r.GetProperty("cards").EnumerateArray()).ToList();
        allCards.ShouldNotContain(c => c.GetProperty("id").GetGuid() == cardId);
    }

    [Fact]
    public async Task Search_WithArchiveBoardId_IncludesArchivedCardsFromThatBoard()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "SearchArchiveInclude_UniqueAbc456");

        // Archive the card
        await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);

        // Act — search with archiveBoardId
        var response = await _client.GetAsync(
            $"/api/v1/search/cards?q=SearchArchiveInclude_UniqueAbc456&archiveBoardId={_factory.DefaultBoardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        results.ShouldNotBeNull();
        results.Length.ShouldBeGreaterThanOrEqualTo(1);

        var allCards = results.SelectMany(r => r.GetProperty("cards").EnumerateArray()).ToList();
        allCards.ShouldContain(c => c.GetProperty("id").GetGuid() == cardId);
        var archivedCard = allCards.First(c => c.GetProperty("id").GetGuid() == cardId);
        archivedCard.GetProperty("isArchived").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Search_WithArchiveBoardId_ExcludesArchivedCardsFromOtherBoards()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "SearchOtherBoardArchive_UniqueDef789");

        // Archive the card
        await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);

        // Act — search with a different board ID as archiveBoardId
        var otherBoardId = Guid.NewGuid();
        var response = await _client.GetAsync(
            $"/api/v1/search/cards?q=SearchOtherBoardArchive_UniqueDef789&archiveBoardId={otherBoardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        results.ShouldNotBeNull();

        // The archived card from default board should NOT appear (archiveBoardId is a different board)
        var allCards = results.SelectMany(r => r.GetProperty("cards").EnumerateArray()).ToList();
        allCards.ShouldNotContain(c => c.GetProperty("id").GetGuid() == cardId);
    }

    // ── Helpers ──

    private async Task BackdateCardAsync(Guid cardId, int daysOld)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var entity = await db.Cards.FindAsync(cardId);
        entity!.LastUpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-daysOld);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> GetFirstLaneIdAsync()
        => await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

    private async Task<Guid> CreateCardAsync(Guid laneId, string name, int daysOld = 0)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name, laneId, position = Random.Shared.Next(10000, 99999) }
        );
        response.EnsureSuccessStatusCode();
        var card = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
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
}
