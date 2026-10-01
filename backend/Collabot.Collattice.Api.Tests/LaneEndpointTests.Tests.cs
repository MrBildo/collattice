using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class LaneEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetLanes_ReturnsOrderedList()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var lanes = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        lanes.ShouldNotBeNull();
        lanes.ShouldNotBeEmpty();

        // Verify ordering by position
        for (var i = 1; i < lanes.Length; i++)
        {
            lanes[i].GetProperty("position").GetInt32()
                .ShouldBeGreaterThanOrEqualTo(lanes[i - 1].GetProperty("position").GetInt32());
        }
    }

    [Fact]
    public async Task GetLaneById_Returns200()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", new { name = "GetById Lane", position = 200 });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var laneId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/lanes/{laneId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var lane = await response.Content.ReadFromJsonAsync<JsonElement>();
        lane.GetProperty("id").GetGuid().ShouldBe(laneId);
        lane.GetProperty("name").GetString().ShouldBe("GetById Lane");
    }

    [Fact]
    public async Task GetLaneById_NonexistentLane_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var bogusId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/lanes/{bogusId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchLane_UpdatesName_Returns200()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", new { name = "PatchNameBefore", position = 201 });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var laneId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/lanes/{laneId}", new { name = "PatchNameAfter" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var lane = await response.Content.ReadFromJsonAsync<JsonElement>();
        lane.GetProperty("name").GetString().ShouldBe("PatchNameAfter");
    }

    [Fact]
    public async Task PatchLane_UpdatesPosition_Returns200()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", new { name = "PatchPosLane", position = 202 });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var laneId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/lanes/{laneId}", new { position = 999 });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var lane = await response.Content.ReadFromJsonAsync<JsonElement>();
        lane.GetProperty("position").GetInt32().ShouldBe(999);
    }

    [Fact]
    public async Task PatchLane_ConflictingPosition_Returns409()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var lane1Response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", new { name = "ConflictLane1", position = 300 });
        lane1Response.EnsureSuccessStatusCode();

        var lane2Response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", new { name = "ConflictLane2", position = 301 });
        lane2Response.EnsureSuccessStatusCode();
        var lane2 = await lane2Response.Content.ReadFromJsonAsync<JsonElement>();
        var lane2Id = lane2.GetProperty("id").GetGuid();

        // Act — try to move lane2 to position 300, which is taken by lane1
        var response = await _client.PatchAsJsonAsync($"/api/v1/lanes/{lane2Id}", new { position = 300 });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PostLane_AsAdmin_Returns201WithCorrectFields()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var request = new { name = "Review", position = 3 };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var lane = await response.Content.ReadFromJsonAsync<JsonElement>();
        lane.GetProperty("name").GetString().ShouldBe("Review");
        lane.GetProperty("position").GetInt32().ShouldBe(3);
        lane.GetProperty("id").GetGuid().ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task PostLane_AsHumanUser_Returns403()
    {
        // Arrange
        var user = await TestAuthHelper.CreateUserAsync(_client, _factory, "HumanLaneTester", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, user.AuthKey);
        var request = new { name = "Blocked", position = 4 };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteLane_EmptyLane_Returns204()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", new { name = "Disposable", position = 99 });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var laneId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/lanes/{laneId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteLane_LaneWithCards_Returns409()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var laneResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", new { name = "HasCards", position = 100 });
        laneResponse.EnsureSuccessStatusCode();
        var lane = await laneResponse.Content.ReadFromJsonAsync<JsonElement>();
        var laneId = lane.GetProperty("id").GetGuid();

        var cardResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Blocker Card",
            descriptionMarkdown = "Prevents deletion",
            laneId,
            position = 0,
            size = "M"
        });
        cardResponse.EnsureSuccessStatusCode();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/lanes/{laneId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DeleteLane_NonexistentLane_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var bogusId = Guid.NewGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/lanes/{bogusId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteLane_AsHumanUser_Returns403()
    {
        // Arrange
        var user = await TestAuthHelper.CreateUserAsync(_client, _factory, "HumanDeleter", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, user.AuthKey);

        var boardResponse = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/board");
        var board = await boardResponse.Content.ReadFromJsonAsync<JsonElement>();
        var firstLaneId = board.GetProperty("lanes")[0].GetProperty("id").GetGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/lanes/{firstLaneId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PostLane_WithEmptyName_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", new { name = "  ", position = 500 });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchLane_WithEmptyName_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", new { name = "PatchEmptyTest", position = 501 });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var laneId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/lanes/{laneId}", new { name = "" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
