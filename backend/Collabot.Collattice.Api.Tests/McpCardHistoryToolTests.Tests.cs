using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The tool class is instantiated directly against the factory's DI scope (the transport is trusted
// as thin), and the return-shape assertions parse the JSON string the caller actually receives
// rather than the rows behind it.
public class McpCardHistoryToolTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetCardHistory_WithNoFormat_DefaultsToDiff()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Mcp History Default", "alpha\nbeta");
        await PatchDescriptionAsync(cardId, "alpha\ngamma");

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateHistoryTools(scope);
        var json = await tools.GetCardHistoryAsync(_factory.AdminAuthKey, cardId: cardId);

        // Assert — diffs, and no full snapshots to pay for.
        var entries = ParseEntries(json);
        entries.Length.ShouldBe(2);
        entries.Any(e => e.TryGetProperty("value", out _)).ShouldBeFalse();
        entries[0].GetProperty("diff").GetString().ShouldBe("@@ -1,2 +1,2 @@\n alpha\n-beta\n+gamma\n");
        entries[1].GetProperty("diff").GetString().ShouldBe(string.Empty);
    }

    [Fact]
    public async Task GetCardHistory_FormatFull_ReturnsWholeValues()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Mcp History Full", "before");
        await PatchDescriptionAsync(cardId, "after");

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateHistoryTools(scope);
        var json = await tools.GetCardHistoryAsync(_factory.AdminAuthKey, cardId: cardId, format: "full");

        // Assert
        var entries = ParseEntries(json);
        entries[0].GetProperty("value").GetString().ShouldBe("after");
        entries[1].GetProperty("value").GetString().ShouldBe("before");
        entries.Any(e => e.TryGetProperty("diff", out _)).ShouldBeFalse();
    }

    [Fact]
    public async Task GetCardHistory_FormatBoth_ReturnsValuesAndDiffs()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Mcp History Both", "before");
        await PatchDescriptionAsync(cardId, "after");

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateHistoryTools(scope);
        var json = await tools.GetCardHistoryAsync(_factory.AdminAuthKey, cardId: cardId, format: "BOTH");

        // Assert — format matching is case-insensitive
        var entries = ParseEntries(json);
        entries[0].GetProperty("value").GetString().ShouldBe("after");
        entries[0].GetProperty("diff").GetString()!.ShouldContain("+after");
    }

    [Fact]
    public async Task GetCardHistory_ByCardNumberAndBoardSlug_ResolvesTheCard()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Mcp History By Number", "before");
        await PatchDescriptionAsync(cardId, "after");

        var card = await GetCardAsync(cardId);
        var cardNumber = card.GetProperty("card").GetProperty("number").GetInt64();
        var boardSlug = await GetDefaultBoardSlugAsync();

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateHistoryTools(scope);
        var json = await tools.GetCardHistoryAsync(_factory.AdminAuthKey, cardNumber: cardNumber, boardSlug: boardSlug);

        // Assert
        var trail = JsonDocument.Parse(json).RootElement;
        trail.GetProperty("cardId").GetGuid().ShouldBe(cardId);
        trail.GetProperty("field").GetString().ShouldBe("description");
        trail.GetProperty("entries").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task GetCardHistory_WithFromAndTo_ReturnsThePairShape()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Mcp History Pair", "one");
        await PatchDescriptionAsync(cardId, "two");
        await PatchDescriptionAsync(cardId, "three");

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateHistoryTools(scope);
        var json = await tools.GetCardHistoryAsync(_factory.AdminAuthKey, cardId: cardId, from: 1, to: 3);

        // Assert
        var pair = JsonDocument.Parse(json).RootElement;
        pair.GetProperty("from").GetInt32().ShouldBe(1);
        pair.GetProperty("to").GetInt32().ShouldBe(3);
        pair.GetProperty("diff").GetString().ShouldBe("@@ -1,1 +1,1 @@\n-one\n+three\n");
        pair.TryGetProperty("fromValue", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateCardOverMcp_RecordsHistoryAttributedToTheCallingUser()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Mcp History Attribution", "genesis");
        var agent = await TestAuthHelper.CreateUserAsync(_client, _factory, "Mcp History Editor", UserRole.AgentUser);

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();

        var cardTools = new CardTools(db, authService, broadcaster);
        await cardTools.UpdateCardAsync(agent.AuthKey, cardId: cardId, descriptionMarkdown: "rewritten");

        var historyTools = new HistoryTools(db, authService);
        var json = await historyTools.GetCardHistoryAsync(agent.AuthKey, cardId: cardId, format: "full");

        // Assert
        var entries = ParseEntries(json);
        entries[0].GetProperty("editedByName").GetString().ShouldBe("Mcp History Editor");
        entries[0].GetProperty("value").GetString().ShouldBe("rewritten");
        entries[1].GetProperty("value").GetString().ShouldBe("genesis");
    }

    [Fact]
    public async Task GetCardHistory_OldestRevisionCarriesTheCardCreatorAsAnInferredEditor()
    {
        // Arrange — created by the admin, first edited over MCP by someone else, so the inferred
        // editor can only be the creator if it is not simply whoever triggered the first capture.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Mcp History Inferred Editor", "genesis");
        var agent = await TestAuthHelper.CreateUserAsync(_client, _factory, "Mcp History Later Editor", UserRole.AgentUser);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();

        var cardTools = new CardTools(db, authService, broadcaster);
        await cardTools.UpdateCardAsync(agent.AuthKey, cardId: cardId, descriptionMarkdown: "rewritten");

        // Act — the tool's default format
        var historyTools = new HistoryTools(db, authService);
        var json = await historyTools.GetCardHistoryAsync(agent.AuthKey, cardId: cardId);

        // Assert — the observed editor stays null and the inference rides in its own field.
        var entries = ParseEntries(json);

        var oldest = entries[1];
        oldest.GetProperty("editedByName").ValueKind.ShouldBe(JsonValueKind.Null);
        oldest.GetProperty("inferredEditor").GetProperty("name").GetString().ShouldBe("Admin");
        oldest.GetProperty("inferredEditor").GetProperty("basis").GetString().ShouldBe("creator");

        var newest = entries[0];
        newest.GetProperty("editedByName").GetString().ShouldBe("Mcp History Later Editor");
        newest.TryGetProperty("inferredEditor", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task GetCardHistory_NeverEditedCard_ReturnsAnEmptyTrail()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Mcp History Untouched", "as created");

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateHistoryTools(scope);
        var json = await tools.GetCardHistoryAsync(_factory.AdminAuthKey, cardId: cardId);

        // Assert
        ParseEntries(json).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetCardHistory_WithAnInvalidAuthKey_ReturnsAnError()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Mcp History Bad Key", "text");

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateHistoryTools(scope);
        var result = await tools.GetCardHistoryAsync("not-a-real-key", cardId: cardId);

        // Assert
        result.ShouldStartWith("Error:");
    }

    [Fact]
    public async Task GetCardHistory_ForAnUnknownCard_ReturnsAnError()
    {
        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateHistoryTools(scope);
        var result = await tools.GetCardHistoryAsync(_factory.AdminAuthKey, cardId: Guid.NewGuid());

        // Assert
        result.ShouldBe("Error: Card not found.");
    }

    [Fact]
    public async Task GetCardHistory_WithAnUnknownField_ReturnsAnError()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Mcp History Bad Field", "text");

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateHistoryTools(scope);
        var result = await tools.GetCardHistoryAsync(_factory.AdminAuthKey, cardId: cardId, field: "titel");

        // Assert
        result.ShouldStartWith("Error: Unknown field");
    }

    [Fact]
    public async Task GetCardHistory_WithFromButNoTo_ReturnsAnError()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Mcp History Half Pair", "text");

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateHistoryTools(scope);
        var result = await tools.GetCardHistoryAsync(_factory.AdminAuthKey, cardId: cardId, from: 1);

        // Assert
        result.ShouldBe("Error: from and to must be supplied together.");
    }

    private static HistoryTools CreateHistoryTools(AsyncServiceScope scope) =>
        new
        (
            scope.ServiceProvider.GetRequiredService<BoardDbContext>(),
            scope.ServiceProvider.GetRequiredService<McpAuthService>()
        );

    private static JsonElement[] ParseEntries(string json) =>
        [.. JsonDocument.Parse(json).RootElement.GetProperty("entries").EnumerateArray()];

    [Fact]
    public async Task GetCardHistory_ReturnsThePagingEnvelopeAlongsideTheEntries()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("Mcp History Envelope", 4);

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateHistoryTools(scope);
        var json = await tools.GetCardHistoryAsync(_factory.AdminAuthKey, cardId: cardId);

        // Assert — asserted on the JSON the caller actually receives, not the rows behind it.
        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("totalCount").GetInt32().ShouldBe(4);
        root.GetProperty("offset").GetInt32().ShouldBe(0);
        root.GetProperty("limit").GetInt32().ShouldBe(200);
        root.GetProperty("entries").GetArrayLength().ShouldBe(4);
    }

    [Fact]
    public async Task GetCardHistory_PagesFromTheNewestEndAndKeepsTotalCountWhole()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("Mcp History Paging", 5);

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateHistoryTools(scope);
        var firstPage = await tools.GetCardHistoryAsync(_factory.AdminAuthKey, cardId: cardId, limit: 2);
        var secondPage = await tools.GetCardHistoryAsync(_factory.AdminAuthKey, cardId: cardId, offset: 2, limit: 2);

        // Assert
        var first = JsonDocument.Parse(firstPage).RootElement;
        first.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("revision").GetInt32()).ShouldBe([5, 4]);
        first.GetProperty("totalCount").GetInt32().ShouldBe(5);

        var second = JsonDocument.Parse(secondPage).RootElement;
        second.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("revision").GetInt32()).ShouldBe([3, 2]);
        second.GetProperty("offset").GetInt32().ShouldBe(2);

        // The page boundary does not flatten a real diff into the empty one the first revision has.
        second.GetProperty("entries")[1].GetProperty("diff").GetString().ShouldNotBe(string.Empty);
    }

    [Fact]
    public async Task GetCardHistory_ClampsItsLimitToTheToolCeiling()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("Mcp History Clamp", 3);

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateHistoryTools(scope);
        var json = await tools.GetCardHistoryAsync(_factory.AdminAuthKey, cardId: cardId, offset: -3, limit: 9999);

        // Assert
        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("offset").GetInt32().ShouldBe(0);
        root.GetProperty("limit").GetInt32().ShouldBe(500);
        root.GetProperty("entries").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task GetCardHistory_PagingAFromToComparison_IsAnError()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("Mcp History Pair Paging", 3);

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateHistoryTools(scope);
        var result = await tools.GetCardHistoryAsync(_factory.AdminAuthKey, cardId: cardId, from: 1, to: 3, limit: 1);

        // Assert
        result.ShouldBe("Error: offset and limit do not apply to a from/to comparison.");
    }

    [Fact]
    public async Task GetCard_CarriesTheDescriptionHistoryCount()
    {
        // The same count REST's card detail carries — both surfaces read it through one builder,
        // so a bot deciding whether to spend a call on the trail gets the same answer a browser does.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("Mcp History Count", 3);

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = new CardTools
        (
            scope.ServiceProvider.GetRequiredService<BoardDbContext>(),
            scope.ServiceProvider.GetRequiredService<McpAuthService>(),
            scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>()
        );
        var json = await tools.GetCardAsync(_factory.AdminAuthKey, cardId: cardId);

        // Assert
        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("descriptionHistoryCount").GetInt32().ShouldBe(3);

        // And it agrees with what the history tool reports for the same card.
        var historyTools = CreateHistoryTools(scope);
        var trail = await historyTools.GetCardHistoryAsync(_factory.AdminAuthKey, cardId: cardId);
        JsonDocument.Parse(trail).RootElement.GetProperty("totalCount").GetInt32().ShouldBe(3);
    }

    private async Task<Guid> CreateRevisionsAsync(string name, int revisions)
    {
        // One edit seeds two revisions, so N revisions take N-1 edits.
        var cardId = await CreateCardAsync(name, "v1");

        for (var version = 2; version <= revisions; version++)
        {
            await PatchDescriptionAsync(cardId, string.Create(CultureInfo.InvariantCulture, $"v{version}"));
        }

        return cardId;
    }

    private async Task<Guid> CreateCardAsync(string name, string descriptionMarkdown)
    {
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name, laneId, descriptionMarkdown }
        );
        response.EnsureSuccessStatusCode();
        var card = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return card.GetProperty("id").GetGuid();
    }

    private async Task PatchDescriptionAsync(Guid cardId, string descriptionMarkdown)
    {
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { descriptionMarkdown });
        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> GetCardAsync(Guid cardId)
    {
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
    }

    private async Task<string> GetDefaultBoardSlugAsync()
    {
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}");
        response.EnsureSuccessStatusCode();
        var board = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return board.GetProperty("slug").GetString()!;
    }
}
