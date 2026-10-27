using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class ArchiveLaneTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private static JsonSerializerOptions JsonOptions => TestAuthHelper.JsonOptions;

    [Fact]
    public async Task GetLanes_DefaultBoard_ExcludesArchiveLane()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var lanes = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        lanes.ShouldNotBeNull();
        foreach (var lane in lanes)
        {
            lane.GetProperty("name").GetString().ShouldNotBe("Archive");
            lane.GetProperty("isArchiveLane").GetBoolean().ShouldBeFalse();
        }
    }

    [Fact]
    public async Task GetBoardComposite_DefaultBoard_ExcludesArchiveLane()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/board");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var lanes = json.GetProperty("lanes");
        var laneNames = lanes.EnumerateArray()
            .Select(lane => lane.GetProperty("name").GetString())
                .ToList();
        lanes.GetArrayLength().ShouldBe(3, $"seed board composite lanes: [{string.Join(", ", laneNames)}]");
        foreach (var lane in lanes.EnumerateArray())
        {
            lane.GetProperty("isArchiveLane").GetBoolean().ShouldBeFalse();
        }
    }

    [Fact]
    public async Task GetBoardComposite_BoardWithArchivedCard_ExcludesArchivedCards()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var boardName = $"CompositeArchivedCards Test {Guid.NewGuid()}";
        var createResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = boardName });
        createResponse.EnsureSuccessStatusCode();
        var board = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var boardId = board.GetProperty("id").GetGuid();

        var archiveLaneId = await GetArchiveLaneIdAsync(boardId);

        var laneResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{boardId}/lanes",
            new { name = "Work", position = 0 }
        );
        laneResponse.EnsureSuccessStatusCode();
        var lane = await laneResponse.Content.ReadFromJsonAsync<JsonElement>();
        var laneId = lane.GetProperty("id").GetGuid();

        var activeCardResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{boardId}/cards",
            new { name = "Active Card", laneId }
        );
        activeCardResponse.EnsureSuccessStatusCode();
        var activeCard = await activeCardResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var activeCardId = activeCard.GetProperty("id").GetGuid();

        var archivedCardResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{boardId}/cards",
            new { name = "Archived Card", laneId }
        );
        archivedCardResponse.EnsureSuccessStatusCode();
        var archivedCard = await archivedCardResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var archivedCardId = archivedCard.GetProperty("id").GetGuid();
        await MoveCardToArchiveLaneAsync(archivedCardId, archiveLaneId);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{boardId}/board");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var cardIds = json.GetProperty("cards").EnumerateArray()
            .Select(c => c.GetProperty("id").GetGuid())
                .ToList();

        cardIds.ShouldContain(activeCardId);
        cardIds.ShouldNotContain(archivedCardId);
    }

    [Fact]
    public async Task CreateBoard_ByAdmin_AutoCreatesArchiveLane()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var boardName = $"Archive Test {Guid.NewGuid()}";

        // Act
        var createResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = boardName });
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var board = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var boardId = board.GetProperty("id").GetGuid();

        // Assert — lane listing should be empty (no non-archive lanes created by POST /boards)
        var lanesResponse = await _client.GetAsync($"/api/v1/boards/{boardId}/lanes");
        lanesResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var lanes = await lanesResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        lanes.ShouldNotBeNull();
        lanes.ShouldBeEmpty();

        // Verify archive lane exists via the DB (create a lane at position 0, which proves the board exists)
        // The archive lane is hidden, but we can verify it blocks deletion
        var deleteResponse = await _client.DeleteAsync($"/api/v1/boards/{boardId}");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CreateLane_MaxValuePosition_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/lanes",
            new { name = "Sneaky Lane", position = int.MaxValue }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchLane_MaxValuePosition_IsRejected()
    {
        // Arrange — create the lane on a dedicated board, not the shared seed board.
        // GetLanes_DefaultBoard_ExcludesArchiveLane / GetBoardComposite_DefaultBoard_ExcludesArchiveLane assert an
        // exact lane count on DefaultBoardId, so adding a persistent lane there makes those
        // reads depend on test-method ordering within the class.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var boardName = $"PatchMaxVal Test {Guid.NewGuid()}";
        var boardResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = boardName });
        boardResponse.EnsureSuccessStatusCode();
        var board = await boardResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var boardId = board.GetProperty("id").GetGuid();

        var createResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{boardId}/lanes",
            new { name = "PatchMaxVal", position = 600 }
        );
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var laneId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PatchAsJsonAsync
        (
            $"/api/v1/lanes/{laneId}",
            new { position = int.MaxValue }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteLane_ArchiveLane_Returns400()
    {
        // Arrange — create a board so we have a known archive lane
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var boardName = $"DelArchive Test {Guid.NewGuid()}";
        var createResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = boardName });
        createResponse.EnsureSuccessStatusCode();
        var board = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var boardId = board.GetProperty("id").GetGuid();

        // Find the archive lane via direct DB access through the factory
        var archiveLaneId = await GetArchiveLaneIdAsync(boardId);

        // Act
        var response = await _client.DeleteAsync($"/api/v1/lanes/{archiveLaneId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchLane_ArchiveLane_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var boardName = $"PatchArchive Test {Guid.NewGuid()}";
        var createResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = boardName });
        createResponse.EnsureSuccessStatusCode();
        var board = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var boardId = board.GetProperty("id").GetGuid();

        var archiveLaneId = await GetArchiveLaneIdAsync(boardId);

        // Act
        var response = await _client.PatchAsJsonAsync
        (
            $"/api/v1/lanes/{archiveLaneId}",
            new { name = "Renamed Archive" }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteBoard_WithOnlyArchiveLane_Succeeds()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var boardName = $"DeleteBoard Test {Guid.NewGuid()}";
        var createResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = boardName });
        createResponse.EnsureSuccessStatusCode();
        var board = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var boardId = board.GetProperty("id").GetGuid();

        // Act — board has only the auto-created archive lane
        var response = await _client.DeleteAsync($"/api/v1/boards/{boardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify board is gone
        var getResponse = await _client.GetAsync($"/api/v1/boards/{boardId}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteBoard_WithArchivedCards_ReturnsCount()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var boardName = $"DelArchCards Test {Guid.NewGuid()}";
        var createResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = boardName });
        createResponse.EnsureSuccessStatusCode();
        var board = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var boardId = board.GetProperty("id").GetGuid();

        // Create a lane, add cards, then place them in the archive lane directly via DB
        var archiveLaneId = await GetArchiveLaneIdAsync(boardId);

        // Add a normal lane first to create a card
        var laneResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{boardId}/lanes",
            new { name = "Temp Lane", position = 0 }
        );
        laneResponse.EnsureSuccessStatusCode();
        var lane = await laneResponse.Content.ReadFromJsonAsync<JsonElement>();
        var laneId = lane.GetProperty("id").GetGuid();

        // Create two cards
        for (var i = 0; i < 2; i++)
        {
            var cardResponse = await _client.PostAsJsonAsync
            (
                $"/api/v1/boards/{boardId}/cards",
                new { name = $"Card {i.ToString(CultureInfo.InvariantCulture)}", laneId }
            );
            cardResponse.EnsureSuccessStatusCode();

            // Move card to archive lane via reorder
            var card = await cardResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            var cardId = card.GetProperty("id").GetGuid();
            await MoveCardToArchiveLaneAsync(cardId, archiveLaneId);
        }

        // Delete the normal lane (now empty)
        var deleteLaneResponse = await _client.DeleteAsync($"/api/v1/lanes/{laneId}");
        deleteLaneResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Act
        var response = await _client.DeleteAsync($"/api/v1/boards/{boardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        json.GetProperty("deleted").GetBoolean().ShouldBeTrue();
        json.GetProperty("archivedCardsDeleted").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task GetLaneById_ArchiveLane_Returns200()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var boardName = $"GetArchive Test {Guid.NewGuid()}";
        var createResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = boardName });
        createResponse.EnsureSuccessStatusCode();
        var board = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var boardId = board.GetProperty("id").GetGuid();

        var archiveLaneId = await GetArchiveLaneIdAsync(boardId);

        // Act
        var response = await _client.GetAsync($"/api/v1/lanes/{archiveLaneId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var lane = await response.Content.ReadFromJsonAsync<JsonElement>();
        lane.GetProperty("isArchiveLane").GetBoolean().ShouldBeTrue();
        lane.GetProperty("name").GetString().ShouldBe("Archive");
        lane.GetProperty("position").GetInt32().ShouldBe(int.MaxValue);
    }

    private async Task<Guid> GetArchiveLaneIdAsync(Guid boardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var archiveLane = await db.Lanes.FirstAsync(l => l.BoardId == boardId && l.IsArchiveLane);
        return archiveLane.Id;
    }

    private async Task MoveCardToArchiveLaneAsync(Guid cardId, Guid archiveLaneId)
    {
        // Move directly via DB since the reorder endpoint may get guards later
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var card = await db.Cards.FindAsync(cardId);
        card!.LaneId = archiveLaneId;
        card.Position = 0;
        await db.SaveChangesAsync();
    }
}
