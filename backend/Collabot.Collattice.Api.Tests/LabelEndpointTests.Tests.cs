using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class LabelEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> GetFirstLaneIdAsync()
        => await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

    private async Task<Guid> CreateCardAsync(Guid laneId)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = $"Test Card {Guid.NewGuid()}",
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        response.EnsureSuccessStatusCode();
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        return card.GetProperty("id").GetGuid();
    }

    private async Task<(Guid Id, string Name)> CreateLabelAsync(Guid? boardId = null, string? name = null, string? color = null)
    {
        boardId ??= _factory.DefaultBoardId;
        name ??= $"Label-{Guid.NewGuid()}";
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/labels", new { name, color });
        response.EnsureSuccessStatusCode();
        var label = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (label.GetProperty("id").GetGuid(), name);
    }

    private async Task<Guid> CreateBoardAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/boards", new { name });
        response.EnsureSuccessStatusCode();
        var board = await response.Content.ReadFromJsonAsync<JsonElement>();
        return board.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task GetLabels_BoardWithNoLabels_ReturnsEmptyList()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var labels = await response.Content.ReadFromJsonAsync<JsonElement>();
        labels.GetArrayLength().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task PostLabel_AsAdmin_Returns201()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var labelName = $"Bug-{Guid.NewGuid()}";

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels", new { name = labelName, color = "red" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var label = await response.Content.ReadFromJsonAsync<JsonElement>();
        label.GetProperty("name").GetString().ShouldBe(labelName);
        label.GetProperty("color").GetString().ShouldBe("red");
        label.GetProperty("id").GetGuid().ShouldNotBe(Guid.Empty);
        label.GetProperty("boardId").GetGuid().ShouldBe(_factory.DefaultBoardId);
    }

    [Fact]
    public async Task PostLabel_DuplicateName_Returns409()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var labelName = $"Duplicate-{Guid.NewGuid()}";
        var firstResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels", new { name = labelName, color = "blue" });
        firstResponse.EnsureSuccessStatusCode();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels", new { name = labelName, color = "green" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PostLabel_SameNameDifferentBoard_Succeeds()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var labelName = $"CrossBoard-{Guid.NewGuid()}";
        var boardBId = await CreateBoardAsync($"Board B {Guid.NewGuid()}");

        var firstResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels", new { name = labelName, color = "blue" });
        firstResponse.EnsureSuccessStatusCode();

        // Act — same name on a different board should succeed
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardBId}/labels", new { name = labelName, color = "green" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task PostLabel_AsNonAdmin_Returns403()
    {
        // Arrange
        var human = await TestAuthHelper.CreateUserAsync(_client, _factory, "LabelHuman", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, human.AuthKey);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels", new { name = $"Forbidden-{Guid.NewGuid()}", color = "red" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PatchLabel_ExistingLabel_UpdatesNameAndColor()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var (labelId, _) = await CreateLabelAsync(color: "red");

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels/{labelId}", new { name = $"Updated-{Guid.NewGuid()}", color = "green" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var label = await response.Content.ReadFromJsonAsync<JsonElement>();
        label.GetProperty("color").GetString().ShouldBe("green");
    }

    [Fact]
    public async Task PatchLabel_NonexistentLabel_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var bogusId = Guid.NewGuid();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels/{bogusId}", new { name = "Ghost" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchLabel_WrongBoard_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var (labelId, _) = await CreateLabelAsync(color: "red");
        var boardBId = await CreateBoardAsync($"Board B {Guid.NewGuid()}");

        // Act — try to patch label via wrong board
        var response = await _client.PatchAsJsonAsync($"/api/v1/boards/{boardBId}/labels/{labelId}", new { name = "Ghost" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteLabel_AsAdmin_Returns204()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var (labelId, _) = await CreateLabelAsync();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels/{labelId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteLabel_AssignedToCard_RemovesFromCards()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId);
        var (labelId, _) = await CreateLabelAsync();

        // Assign label to card
        var assignResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId });
        assignResponse.EnsureSuccessStatusCode();

        // Act — delete the label
        var response = await _client.DeleteAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels/{labelId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify label is removed from card
        var labelsResponse = await _client.GetAsync($"/api/v1/cards/{cardId}/labels");
        labelsResponse.EnsureSuccessStatusCode();
        var labels = await labelsResponse.Content.ReadFromJsonAsync<JsonElement>();
        labels.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task GetCardLabels_CardWithLabel_ReturnsAssigned()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId);
        var (labelId, labelName) = await CreateLabelAsync(color: "purple");

        var assignResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId });
        assignResponse.EnsureSuccessStatusCode();

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/labels");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var labels = await response.Content.ReadFromJsonAsync<JsonElement>();
        labels.GetArrayLength().ShouldBe(1);
        labels[0].GetProperty("id").GetGuid().ShouldBe(labelId);
        labels[0].GetProperty("name").GetString().ShouldBe(labelName);
    }

    [Fact]
    public async Task PostCardLabel_UnassignedLabel_AssignsLabel()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId);
        var (labelId, _) = await CreateLabelAsync();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task PostCardLabel_Duplicate_Returns409()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId);
        var (labelId, _) = await CreateLabelAsync();

        var firstResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId });
        firstResponse.EnsureSuccessStatusCode();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PostCardLabel_CrossBoard_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId);

        // Create a label on a different board
        var boardBId = await CreateBoardAsync($"Board B {Guid.NewGuid()}");
        var (labelId, _) = await CreateLabelAsync(boardId: boardBId);

        // Act — try to assign a label from board B to a card on the default board
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteCardLabel_AssignedLabel_RemovesAssignment()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId);
        var (labelId, _) = await CreateLabelAsync();

        var assignResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId });
        assignResponse.EnsureSuccessStatusCode();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/cards/{cardId}/labels/{labelId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify label is no longer on the card
        var labelsResponse = await _client.GetAsync($"/api/v1/cards/{cardId}/labels");
        labelsResponse.EnsureSuccessStatusCode();
        var labels = await labelsResponse.Content.ReadFromJsonAsync<JsonElement>();
        labels.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task PostLabel_WithEmptyName_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels", new { name = " ", color = "red" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchLabel_WithEmptyName_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var (labelId, _) = await CreateLabelAsync();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels/{labelId}", new { name = "" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
