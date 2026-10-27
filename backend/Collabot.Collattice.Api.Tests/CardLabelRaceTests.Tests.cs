using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Adding or removing a card's label reads whether the assignment exists and then writes, so a second
// caller doing the same thing at the same moment can land in between. The interceptor commits that
// rival write deterministically, inside the real request, on REST and MCP alike. The caller that
// loses gets the answer it would have got arriving second: already assigned (409) on add, not
// assigned (404) on remove, never a 500, and it announces no change it did not make.
public class CardLabelRaceTests(CardLabelRaceFactory factory) : IClassFixture<CardLabelRaceFactory>, IDisposable
{
    private readonly CardLabelRaceFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly List<IServiceScope> _scopes = [];

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private LabelTools CreateMcpTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);

        return new LabelTools
        (
            scope.ServiceProvider.GetRequiredService<BoardDbContext>(),
            scope.ServiceProvider.GetRequiredService<McpAuthService>(),
            scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>()
        );
    }

    private async Task<JsonElement> PostAsync(string url, object body)
    {
        var response = await _client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // A fresh board with one working lane, one label and one card, the label on the card when asked.
    private async Task<(Guid CardId, Guid LabelId)> SeedCardAsync(bool labeled)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var boardId = (await PostAsync("/api/v1/boards", new { name = $"Label Race {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var laneId = (await PostAsync($"/api/v1/boards/{boardId}/lanes", new { name = "Work" })).GetProperty("id").GetGuid();
        var labelId = (await PostAsync($"/api/v1/boards/{boardId}/labels", new { name = "Urgent", color = "#ff0000" })).GetProperty("id").GetGuid();

        Guid[] labelIds = labeled ? [labelId] : [];
        var cardId = (await PostAsync($"/api/v1/boards/{boardId}/cards", new { name = "Raced card", laneId, labelIds })).GetProperty("id").GetGuid();

        return (cardId, labelId);
    }

    private async Task<int> AssignmentCountAsync(Guid cardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        return await db.CardLabels.CountAsync(cl => cl.CardId == cardId);
    }

    [Fact]
    public async Task AddLabel_SameLabelAddedDuringTheAdd_AlreadyAssignedOnBothSurfaces()
    {
        // Arrange
        var (restCardId, restLabelId) = await SeedCardAsync(labeled: false);
        var (mcpCardId, mcpLabelId) = await SeedCardAsync(labeled: false);
        var tools = CreateMcpTools();

        // Act — the label is not on the card when checked, then a rival adds it before the insert
        _factory.Interceptor.Arm(restCardId);
        _factory.Sink.Clear();
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{restCardId}/labels", new { labelId = restLabelId });
        var restFired = _factory.Interceptor.FiredCount;
        var restEvents = _factory.Sink.Captured;

        _factory.Interceptor.Arm(mcpCardId);
        _factory.Sink.Clear();
        var mcpResult = await tools.AddLabelToCardAsync(_factory.AdminAuthKey, cardId: mcpCardId, labelId: mcpLabelId);
        var mcpFired = _factory.Interceptor.FiredCount;
        var mcpEvents = _factory.Sink.Captured;

        // Assert — the same answer as a label assigned before the call, and the one row the rival wrote
        restFired.ShouldBe(1);
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("Label is already assigned to this card");
        restEvents.ShouldBeEmpty();
        (await AssignmentCountAsync(restCardId)).ShouldBe(1);

        mcpFired.ShouldBe(1);
        mcpResult.ShouldBe("Label already assigned to this card.");
        mcpEvents.ShouldBeEmpty();
        (await AssignmentCountAsync(mcpCardId)).ShouldBe(1);
    }

    [Fact]
    public async Task RemoveLabel_SameLabelRemovedDuringTheRemove_NotAssignedOnBothSurfaces()
    {
        // Arrange
        var (restCardId, restLabelId) = await SeedCardAsync(labeled: true);
        var (mcpCardId, mcpLabelId) = await SeedCardAsync(labeled: true);
        var tools = CreateMcpTools();

        // Act — the assignment exists when read, then a rival removes it before the delete
        _factory.Interceptor.Arm(restCardId);
        _factory.Sink.Clear();
        var restResponse = await _client.DeleteAsync($"/api/v1/cards/{restCardId}/labels/{restLabelId}");
        var restFired = _factory.Interceptor.FiredCount;
        var restEvents = _factory.Sink.Captured;

        _factory.Interceptor.Arm(mcpCardId);
        _factory.Sink.Clear();
        var mcpResult = await tools.RemoveLabelFromCardAsync(_factory.AdminAuthKey, cardId: mcpCardId, labelId: mcpLabelId);
        var mcpFired = _factory.Interceptor.FiredCount;
        var mcpEvents = _factory.Sink.Captured;

        // Assert — the same answer as a label that was never on the card
        restFired.ShouldBe(1);
        restResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        restEvents.ShouldBeEmpty();
        (await AssignmentCountAsync(restCardId)).ShouldBe(0);

        mcpFired.ShouldBe(1);
        mcpResult.ShouldBe("Error: Label not assigned to this card.");
        mcpEvents.ShouldBeEmpty();
        (await AssignmentCountAsync(mcpCardId)).ShouldBe(0);
    }

    [Fact]
    public async Task AddAndRemoveLabel_Uncontended_StillAssignAndUnassignWithOneEventEach()
    {
        // Arrange — the interceptor stays disarmed, so this is the ordinary path through the helper
        var (cardId, labelId) = await SeedCardAsync(labeled: false);
        _factory.Interceptor.Arm(Guid.Empty);

        // Act
        _factory.Sink.Clear();
        var added = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId });
        var countAfterAdd = await AssignmentCountAsync(cardId);
        var addEvents = _factory.Sink.Captured;

        _factory.Sink.Clear();
        var removed = await _client.DeleteAsync($"/api/v1/cards/{cardId}/labels/{labelId}");
        var removeEvents = _factory.Sink.Captured;

        // Assert
        added.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await added.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("cardId").GetGuid().ShouldBe(cardId);
        body.GetProperty("labelId").GetGuid().ShouldBe(labelId);
        countAfterAdd.ShouldBe(1);
        addEvents.Select(e => e.EventType).ShouldBe(["card.labeled"]);

        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        removeEvents.Select(e => e.EventType).ShouldBe(["card.unlabeled"]);
        (await AssignmentCountAsync(cardId)).ShouldBe(0);
    }
}
