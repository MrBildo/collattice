using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class ArchiveEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private static JsonSerializerOptions JsonOptions => TestAuthHelper.JsonOptions;

    [Fact]
    public async Task ArchiveCard_ExcludedFromDefaultListing_VisibleWithIncludeArchived()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Archive Listing Test");

        // Act — archive the card
        var archiveResponse = await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);
        archiveResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Assert — default listing excludes archived cards
        var defaultResponse = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards");
        defaultResponse.EnsureSuccessStatusCode();
        var defaultPaged = await defaultResponse.Content.ReadFromJsonAsync<PagedResult<JsonElement>>(JsonOptions);
        defaultPaged.ShouldNotBeNull();
        defaultPaged.Items.ShouldAllBe(c => c.GetProperty("id").GetGuid() != cardId);

        // Assert — includeArchived=true shows the card
        var archivedResponse = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?includeArchived=true");
        archivedResponse.EnsureSuccessStatusCode();
        var archivedPaged = await archivedResponse.Content.ReadFromJsonAsync<PagedResult<JsonElement>>(JsonOptions);
        archivedPaged.ShouldNotBeNull();
        archivedPaged.Items.ShouldContain(c => c.GetProperty("id").GetGuid() == cardId);
    }

    [Fact]
    public async Task RestoreCard_ArchivedCard_LandsInTargetLaneAtPosition0()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Restore Test");

        await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);

        // Act — restore to original lane
        var restoreResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/restore", new { laneId });
        restoreResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Assert — card is back in the target lane at position 0
        var detailResponse = await _client.GetAsync($"/api/v1/cards/{cardId}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        detail.GetProperty("card").GetProperty("laneId").GetGuid().ShouldBe(laneId);
        detail.GetProperty("card").GetProperty("position").GetInt32().ShouldBe(0);
        detail.GetProperty("isArchived").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ArchiveCard_AlreadyArchived_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Double Archive Test");
        await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);

        // Act
        var response = await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RestoreCard_NotArchived_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Restore Non-Archived Test");

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/restore", new { laneId });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RestoreCard_ToArchiveLane_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Restore To Archive Test");
        await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);

        var archiveLaneId = await GetArchiveLaneIdAsync(_factory.DefaultBoardId);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/restore", new { laneId = archiveLaneId });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetCardDetail_ShowsIsArchivedTrue_WhenArchived()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Detail IsArchived Test");
        await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}");

        // Assert
        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        detail.GetProperty("isArchived").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task GetCards_IsArchivedFieldAppears_InListingResponse()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Listing IsArchived Test");

        // Act — non-archived card in listing
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards");
        response.EnsureSuccessStatusCode();
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>(JsonOptions);
        paged.ShouldNotBeNull();

        var card = paged.Items.FirstOrDefault(c => c.GetProperty("id").GetGuid() == cardId);
        card.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
        card.GetProperty("isArchived").GetBoolean().ShouldBeFalse();

        // Archive it, then check includeArchived listing
        await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);

        var archivedResponse = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?includeArchived=true");
        archivedResponse.EnsureSuccessStatusCode();
        var archivedPaged = await archivedResponse.Content.ReadFromJsonAsync<PagedResult<JsonElement>>(JsonOptions);
        archivedPaged.ShouldNotBeNull();

        var archivedCard = archivedPaged.Items.First(c => c.GetProperty("id").GetGuid() == cardId);
        archivedCard.GetProperty("isArchived").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task RestoreCard_ToNonexistentLane_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Restore Nonexistent Lane Test");
        await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/restore", new { laneId = Guid.NewGuid() });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<Guid> GetFirstLaneIdAsync()
        => await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

    private async Task<Guid> CreateCardAsync(Guid laneId, string name)
    {
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name, laneId }
        );
        response.EnsureSuccessStatusCode();
        var card = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return card.GetProperty("id").GetGuid();
    }

    private async Task<Guid> GetArchiveLaneIdAsync(Guid boardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var archiveLane = await db.Lanes.FirstAsync(l => l.BoardId == boardId && l.IsArchiveLane);
        return archiveLane.Id;
    }
}
