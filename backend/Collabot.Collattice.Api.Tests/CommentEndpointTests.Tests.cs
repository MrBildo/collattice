using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class CommentEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> CreateCardAsync()
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

        var cardPayload = new
        {
            name = "Test Card",
            descriptionMarkdown = "Card for comment tests",
            laneId,
            position = Random.Shared.Next(10000, 99999),
        };

        var cardResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", cardPayload);
        cardResponse.EnsureSuccessStatusCode();
        var card = await cardResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return card.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task GetComments_ReturnsCommentsForCard()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "First comment" });
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Second comment" });

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/comments");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var comments = await response.Content.ReadFromJsonAsync<JsonElement[]>(TestAuthHelper.JsonOptions);
        comments.ShouldNotBeNull();
        comments.Length.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task GetComments_NonexistentCard_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var fakeCardId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{fakeCardId}/comments");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchComment_OwnComment_UpdatesContent()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        var human = await TestAuthHelper.CreateUserAsync(_client, _factory, "Patch Comment Owner", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, human.AuthKey);

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Original content" });
        createResponse.EnsureSuccessStatusCode();
        var comment = await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var commentId = comment.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/comments/{commentId}", new { contentMarkdown = "Updated content" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        updated.GetProperty("contentMarkdown").GetString().ShouldBe("Updated content");
    }

    [Fact]
    public async Task PatchComment_OtherUser_AsAdmin_Returns200()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Patch Comment Author", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, author.AuthKey);

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Author's comment" });
        createResponse.EnsureSuccessStatusCode();
        var comment = await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var commentId = comment.GetProperty("id").GetGuid();

        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/comments/{commentId}", new { contentMarkdown = "Admin edited" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        updated.GetProperty("contentMarkdown").GetString().ShouldBe("Admin edited");
    }

    [Fact]
    public async Task PatchComment_OtherUser_AsAgentAdministrator_Returns200()
    {
        // Arrange — AgentAdministrator can edit another user's comment
        // over REST (previously only Administrator was admitted; MCP update_comment was
        // already own-or-admin-level via McpAuthService.IsAdminLevel).
        var cardId = await CreateCardAsync();
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Patch Comment Author AA", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, author.AuthKey);

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Author's comment" });
        createResponse.EnsureSuccessStatusCode();
        var comment = await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var commentId = comment.GetProperty("id").GetGuid();

        var agentAdmin = await TestAuthHelper.CreateUserAsync(_client, _factory, "AgentAdmin Patch User", UserRole.AgentAdministrator);
        TestAuthHelper.SetAuth(_client, agentAdmin.AuthKey);

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/comments/{commentId}", new { contentMarkdown = "Agent admin edited" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        updated.GetProperty("contentMarkdown").GetString().ShouldBe("Agent admin edited");
    }

    [Fact]
    public async Task PatchComment_OtherUser_AsNonAdmin_Returns403()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Patch Comment Author 2", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, author.AuthKey);

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Author's comment" });
        createResponse.EnsureSuccessStatusCode();
        var comment = await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var commentId = comment.GetProperty("id").GetGuid();

        var otherUser = await TestAuthHelper.CreateUserAsync(_client, _factory, "Other Patch User", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, otherUser.AuthKey);

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/comments/{commentId}", new { contentMarkdown = "Unauthorized edit" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PostComment_OnExistingCard_Returns201()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var payload = new { contentMarkdown = "This is a test comment." };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", payload);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        json.TryGetProperty("id", out var idProp).ShouldBeTrue();
        idProp.GetGuid().ShouldNotBe(Guid.Empty);
        json.GetProperty("cardId").GetGuid().ShouldBe(cardId);
        json.GetProperty("userId").GetGuid().ShouldNotBe(Guid.Empty);
        json.GetProperty("contentMarkdown").GetString().ShouldBe("This is a test comment.");
        json.TryGetProperty("lastUpdatedAtUtc", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task PostComment_OnNonexistentCard_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var fakeCardId = Guid.NewGuid();
        var payload = new { contentMarkdown = "Comment on missing card." };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{fakeCardId}/comments", payload);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostComment_NewlinesInContent_PreservedOnRoundTrip()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var content = "Line one\nLine two\nLine three\ttab here";
        var payload = new { contentMarkdown = content };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", payload);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        json.GetProperty("contentMarkdown").GetString().ShouldBe(content);
    }

    [Fact]
    public async Task DeleteComment_OwnComment_Returns204()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        var human = await TestAuthHelper.CreateUserAsync(_client, _factory, "Comment Owner", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, human.AuthKey);

        var payload = new { contentMarkdown = "My comment to delete." };
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", payload);
        createResponse.EnsureSuccessStatusCode();
        var comment = await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var commentId = comment.GetProperty("id").GetGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/comments/{commentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteComment_OtherUsersComment_AsAdmin_Returns204()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        var human = await TestAuthHelper.CreateUserAsync(_client, _factory, "Comment Author", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, human.AuthKey);

        var payload = new { contentMarkdown = "Someone else's comment." };
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", payload);
        createResponse.EnsureSuccessStatusCode();
        var comment = await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var commentId = comment.GetProperty("id").GetGuid();

        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.DeleteAsync($"/api/v1/comments/{commentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteComment_OtherUsersComment_AsNonAdmin_Returns403()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Comment Author 2", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, author.AuthKey);

        var payload = new { contentMarkdown = "Author's comment." };
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", payload);
        createResponse.EnsureSuccessStatusCode();
        var comment = await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var commentId = comment.GetProperty("id").GetGuid();

        var otherUser = await TestAuthHelper.CreateUserAsync(_client, _factory, "Other User", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, otherUser.AuthKey);

        // Act
        var response = await _client.DeleteAsync($"/api/v1/comments/{commentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteComment_NonexistentComment_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var fakeCommentId = Guid.NewGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/comments/{fakeCommentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostComment_SetsCreatedAtUtc_EqualToLastUpdatedAtUtc()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "provenance check" });

        // Assert — a fresh comment's creation time is present and equal to its last-touched time
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        json.TryGetProperty("createdAtUtc", out var createdProp).ShouldBeTrue();
        createdProp.GetDateTimeOffset().ShouldBe(json.GetProperty("lastUpdatedAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task PatchComment_PreservesCreatedAtUtc_AndBumpsLastUpdatedAtUtc()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "before edit" });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var commentId = created.GetProperty("id").GetGuid();
        var createdAtUtc = created.GetProperty("createdAtUtc").GetDateTimeOffset();

        // Act — an edit resurfaces the comment as latest activity but must not rewrite its creation time
        var response = await _client.PatchAsJsonAsync($"/api/v1/comments/{commentId}", new { contentMarkdown = "after edit" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        updated.GetProperty("createdAtUtc").GetDateTimeOffset().ShouldBe(createdAtUtc);
        updated.GetProperty("lastUpdatedAtUtc").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(createdAtUtc);
    }

    [Fact]
    public async Task GetComments_IncludesCreatedAtUtc()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "listed comment" });

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/comments");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var comments = await response.Content.ReadFromJsonAsync<JsonElement[]>(TestAuthHelper.JsonOptions);
        comments.ShouldNotBeNull();
        comments.ShouldNotBeEmpty();
        comments[0].TryGetProperty("createdAtUtc", out _).ShouldBeTrue();
    }
}
