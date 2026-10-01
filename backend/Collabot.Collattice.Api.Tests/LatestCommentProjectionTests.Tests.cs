using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// get_cards carries a latestComment projection (author, isFromAdmin, timestamp, preview)
// on both surfaces (REST GET /boards/{boardId}/cards and the MCP get_cards tool), so a fresh
// operator comment is visible in a lane-scan without a second per-card fetch.
public class LatestCommentProjectionTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> GetFirstLaneIdAsync()
        => await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

    private async Task<Guid> CreateCardAsync(Guid laneId, string name)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name,
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        response.EnsureSuccessStatusCode();
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        return card.GetProperty("id").GetGuid();
    }

    private async Task PostCommentAsync(Guid cardId, string content)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = content });
        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> GetCardFromListAsync(Guid laneId, Guid cardId)
    {
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?laneId={laneId}");
        response.EnsureSuccessStatusCode();
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        paged.ShouldNotBeNull();
        return paged.Items.First(c => c.GetProperty("id").GetGuid() == cardId);
    }

    [Fact]
    public async Task GetCards_NoComments_LatestCommentIsNull()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Card with no comments");

        // Act
        var card = await GetCardFromListAsync(laneId, cardId);

        // Assert
        card.GetProperty("commentCount").GetInt32().ShouldBe(0);
        card.GetProperty("latestComment").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetCards_AdminComment_LatestCommentIsFromAdminTrue()
    {
        // Arrange — the admin seed user is an Administrator (the operator).
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Card with an admin comment");
        await PostCommentAsync(cardId, "Approved for 3a");

        // Act
        var card = await GetCardFromListAsync(laneId, cardId);

        // Assert
        var latest = card.GetProperty("latestComment");
        latest.ValueKind.ShouldBe(JsonValueKind.Object);
        latest.GetProperty("isFromAdmin").GetBoolean().ShouldBeTrue();
        latest.GetProperty("preview").GetString().ShouldBe("Approved for 3a");
        latest.GetProperty("author").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetCards_AgentComment_LatestCommentIsFromAdminFalse()
    {
        // Arrange — an AgentUser (not admin-level) leaves the comment.
        var agent = await TestAuthHelper.CreateUserAsync(_client, _factory, "Agent Scribe", UserRole.AgentUser);

        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Card with an agent comment");

        TestAuthHelper.SetAuth(_client, agent.AuthKey);
        await PostCommentAsync(cardId, "Picked this up");

        // Act
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var card = await GetCardFromListAsync(laneId, cardId);

        // Assert
        var latest = card.GetProperty("latestComment");
        latest.ValueKind.ShouldBe(JsonValueKind.Object);
        latest.GetProperty("isFromAdmin").GetBoolean().ShouldBeFalse();
        latest.GetProperty("author").GetString().ShouldBe("Agent Scribe");
    }

    [Fact]
    public async Task GetCards_MultipleComments_LatestCommentIsTheNewest()
    {
        // Arrange — newest comment (by LastUpdatedAtUtc) wins. An agent comments first,
        // then the admin lands the ruling — the admin one must surface.
        var agent = await TestAuthHelper.CreateUserAsync(_client, _factory, "Agent Early", UserRole.AgentUser);

        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Card with a comment thread");

        TestAuthHelper.SetAuth(_client, agent.AuthKey);
        await PostCommentAsync(cardId, "first take from the agent");

        TestAuthHelper.SetAdminAuth(_client, _factory);
        await PostCommentAsync(cardId, "operator ruling, this is newest");

        // Act
        var card = await GetCardFromListAsync(laneId, cardId);

        // Assert
        card.GetProperty("commentCount").GetInt32().ShouldBe(2);
        var latest = card.GetProperty("latestComment");
        latest.GetProperty("isFromAdmin").GetBoolean().ShouldBeTrue();
        latest.GetProperty("preview").GetString().ShouldBe("operator ruling, this is newest");
    }

    [Fact]
    public async Task GetCards_LongComment_PreviewIsTruncatedWithEllipsis()
    {
        // Arrange — body well past the ~140-char preview budget.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Card with a long comment");

        var body = new string('a', 300);
        await PostCommentAsync(cardId, body);

        // Act
        var card = await GetCardFromListAsync(laneId, cardId);

        // Assert — first 140 chars + a single ellipsis character.
        var preview = card.GetProperty("latestComment").GetProperty("preview").GetString();
        preview.ShouldNotBeNull();
        preview.ShouldBe(new string('a', 140) + "…");
    }

    [Fact]
    public async Task GetCards_ShortComment_PreviewIsNotTruncated()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Card with a short comment");
        await PostCommentAsync(cardId, "short and sweet");

        // Act
        var card = await GetCardFromListAsync(laneId, cardId);

        // Assert — no trailing ellipsis on a body under the budget.
        var preview = card.GetProperty("latestComment").GetProperty("preview").GetString();
        preview.ShouldBe("short and sweet");
    }

    [Fact]
    public async Task McpGetCards_CarriesLatestCommentProjection()
    {
        // Arrange — exercise the MCP surface directly to prove parity with REST.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "MCP latest-comment card");
        await PostCommentAsync(cardId, "operator ruling via MCP");

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var cardTools = new CardTools(db, authService, broadcaster);

        var json = await cardTools.GetCardsAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, laneId: laneId);

        // Assert
        using var doc = JsonDocument.Parse(json);
        var card = doc.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .First(c => c.GetProperty("id").GetGuid() == cardId);

        var latest = card.GetProperty("latestComment");
        latest.ValueKind.ShouldBe(JsonValueKind.Object);
        latest.GetProperty("isFromAdmin").GetBoolean().ShouldBeTrue();
        latest.GetProperty("preview").GetString().ShouldBe("operator ruling via MCP");
        latest.GetProperty("author").GetString().ShouldNotBeNullOrEmpty();
        latest.TryGetProperty("lastUpdatedAtUtc", out var ts).ShouldBeTrue();
        ts.ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task McpGetCards_NoComments_LatestCommentIsNull()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "MCP no-comment card");

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var cardTools = new CardTools(db, authService, broadcaster);

        var json = await cardTools.GetCardsAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, laneId: laneId);

        // Assert
        using var doc = JsonDocument.Parse(json);
        var card = doc.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .First(c => c.GetProperty("id").GetGuid() == cardId);

        card.GetProperty("latestComment").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
