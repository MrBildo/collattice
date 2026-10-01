using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class BoardEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private static JsonSerializerOptions JsonOptions => TestAuthHelper.JsonOptions;

    [Fact]
    public async Task GetBoard_AsAdmin_Returns200WithLanesAndEmptyCards()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/board");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        json.TryGetProperty("lanes", out var lanes).ShouldBeTrue();
        lanes.GetArrayLength().ShouldBe(3);
        json.TryGetProperty("cards", out var cards).ShouldBeTrue();
        cards.ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetBoard_AsHumanUser_Returns200()
    {
        // Arrange
        var human = await TestAuthHelper.CreateUserAsync(_client, _factory, "Board Human", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, human.AuthKey);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/board");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetBoard_AsAgentUser_Returns200()
    {
        // Arrange
        var agent = await TestAuthHelper.CreateUserAsync(_client, _factory, "Board Agent", UserRole.AgentUser);
        TestAuthHelper.SetAuth(_client, agent.AuthKey);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/board");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetBoard_Unauthenticated_Returns401()
    {
        // Arrange
        _client.DefaultRequestHeaders.Remove("X-User-Key");

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/board");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetBoard_LanesAreOrderedByPosition()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/board");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var lanes = json.GetProperty("lanes");
        lanes.GetArrayLength().ShouldBe(3);

        var lane0 = lanes[0];
        var lane1 = lanes[1];
        var lane2 = lanes[2];

        lane0.GetProperty("position").GetInt32().ShouldBe(0);
        lane0.GetProperty("name").GetString().ShouldBe("Backlog");
        lane1.GetProperty("position").GetInt32().ShouldBe(1);
        lane1.GetProperty("name").GetString().ShouldBe("In Progress");
        lane2.GetProperty("position").GetInt32().ShouldBe(2);
        lane2.GetProperty("name").GetString().ShouldBe("Done");
    }

    [Fact]
    public async Task GetBoard_AfterCreatingCard_CardAppearsInResponse()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var boardResponse = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/board");
        var boardJson = await boardResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var firstLaneId = boardJson.GetProperty("lanes")[0].GetProperty("id").GetGuid();

        var cardPayload = new
        {
            name = "Test Card",
            descriptionMarkdown = "A card for testing",
            laneId = firstLaneId,
            position = 0,
            size = "M",
        };

        // Act
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", cardPayload);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var refreshedBoard = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/board");

        // Assert
        refreshedBoard.StatusCode.ShouldBe(HttpStatusCode.OK);

        var refreshedJson = await refreshedBoard.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var cards = refreshedJson.GetProperty("cards");
        cards.GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);

        var found = false;
        foreach (var card in cards.EnumerateArray())
        {
            if (card.GetProperty("name").GetString() == "Test Card")
            {
                card.GetProperty("descriptionMarkdown").GetString().ShouldBe("A card for testing");
                card.GetProperty("laneId").GetGuid().ShouldBe(firstLaneId);
                found = true;
                break;
            }
        }

        found.ShouldBeTrue("Created card was not found in the board response.");
    }

    [Fact]
    public async Task ListBoards_AsAdmin_ReturnsAtLeastOne()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync("/api/v1/boards");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var boards = await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        boards.ShouldNotBeNull();
        boards.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetBoardById_Returns200()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var board = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        board.GetProperty("id").GetGuid().ShouldBe(_factory.DefaultBoardId);
        board.GetProperty("name").GetString().ShouldBe("Default");
    }

    [Fact]
    public async Task GetBoardBySlug_Returns200()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync("/api/v1/boards/default");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var board = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        board.GetProperty("slug").GetString().ShouldBe("default");
    }

    [Fact]
    public async Task CreateBoard_AsAdmin_Returns201()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var boardName = $"Board {Guid.NewGuid()}";

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/boards", new { name = boardName });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var board = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        board.GetProperty("name").GetString().ShouldBe(boardName);
        board.GetProperty("id").GetGuid().ShouldNotBe(Guid.Empty);
        board.GetProperty("slug").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateBoard_AsHumanUser_Returns403()
    {
        // Arrange
        var human = await TestAuthHelper.CreateUserAsync(_client, _factory, "Board Human Creator", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, human.AuthKey);

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/boards", new { name = "Forbidden Board" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PatchBoard_UpdatesName_SlugUnchanged()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var createResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = $"Patch Board {Guid.NewGuid()}" });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var boardId = created.GetProperty("id").GetGuid();
        var originalSlug = created.GetProperty("slug").GetString();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/boards/{boardId}", new { name = "Renamed Board" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        updated.GetProperty("name").GetString().ShouldBe("Renamed Board");
        updated.GetProperty("slug").GetString().ShouldBe(originalSlug);
    }

    [Fact]
    public async Task DeleteBoard_EmptyBoard_Returns204()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var createResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = $"Delete Me {Guid.NewGuid()}" });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var boardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/boards/{boardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteBoard_BoardWithLanes_Returns400()
    {
        // Arrange — the default board has 3 lanes
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.DeleteAsync($"/api/v1/boards/{_factory.DefaultBoardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
