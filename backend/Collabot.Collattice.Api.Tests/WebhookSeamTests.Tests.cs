using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Safety-property tests for the webhook fan-out seam. There is NO HTTP
// delivery yet — the CapturingWebhookSink IS the observable. These assert on the typed
// BoardEvent the seam enqueues (and its JSON-serialized wire shape), plus the SSE
// byte-for-byte-unchanged safety property.
public sealed class WebhookSeamTests(WebhookTestFactory factory) : IClassFixture<WebhookTestFactory>, IDisposable
{
    private readonly WebhookTestFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly List<IServiceScope> _scopes = [];

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }
    }

    // Each test starts from a clean sink — the fixture is shared across the class.
    private CapturingWebhookSink Sink
    {
        get
        {
            _factory.Sink.Clear();
            return _factory.Sink;
        }
    }

    private CardTools CreateCardTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var auth = new McpAuthService(new UserResolver(db));
        return new CardTools(db, auth, broadcaster);
    }

    private BulkCardTools CreateBulkTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var sink = scope.ServiceProvider.GetRequiredService<IWebhookSink>();
        var auth = new McpAuthService(new UserResolver(db));
        return new BulkCardTools(db, auth, broadcaster, sink);
    }

    // Resolve via the DB scope (not the HTTP /board endpoint) so the pure-MCP tests don't
    // need X-User-Key auth on the HTTP client just to look up lane ids.
    private async Task<(Guid LaneA, Guid LaneB)> GetTwoLanesAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var lanes = await db.Lanes
            .Where(l => l.BoardId == _factory.DefaultBoardId && !l.IsArchiveLane)
            .OrderBy(l => l.Position)
                .Select(l => l.Id)
                    .ToListAsync();

        return (lanes[0], lanes[1]);
    }

    private async Task<string> LaneNameAsync(Guid laneId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.Lanes.Where(l => l.Id == laneId).Select(l => l.Name).FirstAsync();
    }

    private static JsonElement Serialize(BoardEvent boardEvent)
    {
        var json = JsonSerializer.Serialize(boardEvent, JsonSerializerOptions.Web);
        return JsonDocument.Parse(json).RootElement;
    }

    // ── Scenario 1: card.created fires on REST direct create, fat + actor ────────

    [Fact]
    public async Task RestCreate_FiresCardCreated_WithFatPayloadAndActor()
    {
        // Arrange
        var sink = Sink;
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Webhook REST Create",
            descriptionMarkdown = "Repro steps...",
            laneId,
        });
        response.EnsureSuccessStatusCode();

        // Assert — exactly one card.created with the fat CardSummary + laneName + actor.
        sink.Captured.Count.ShouldBe(1);
        var evt = sink.Captured[0];
        evt.EventType.ShouldBe("card.created");
        evt.Version.ShouldBe("1");
        evt.BoardId.ShouldBe(_factory.DefaultBoardId);

        var wire = Serialize(evt);
        wire.GetProperty("event").GetString().ShouldBe("card.created");
        wire.GetProperty("boardSlug").GetString().ShouldNotBeNullOrEmpty();
        wire.GetProperty("eventId").GetString()!.Length.ShouldBe(26); // ULID

        var actor = wire.GetProperty("actor");
        actor.GetProperty("name").GetString().ShouldBe("Admin");
        actor.GetProperty("role").GetString().ShouldBe("Administrator");

        var data = wire.GetProperty("data");
        var card = data.GetProperty("card");
        card.GetProperty("name").GetString().ShouldBe("Webhook REST Create");
        card.GetProperty("number").GetInt64().ShouldBeGreaterThan(0);
        data.GetProperty("laneName").GetString().ShouldBe(await LaneNameAsync(laneId));
    }

    // ── Scenario 2: REST and MCP create produce identical event shape ────────────

    [Fact]
    public async Task RestAndMcpCreate_ProduceIdenticalEnvelopeAndDataFieldSet()
    {
        // Arrange
        var sink = Sink;
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

        // Act — one card via REST, one via MCP create_card (direct tool invocation).
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Parity REST",
            laneId,
        });
        restResponse.EnsureSuccessStatusCode();

        var tools = CreateCardTools();
        var mcpResult = await tools.CreateCardAsync(CollatticeApiFactory.TestAdminAuthKey, "Parity MCP", laneId);
        mcpResult.ShouldNotContain("Error");

        // Assert — both events have the same envelope keys and the same data field-set.
        sink.Captured.Count.ShouldBe(2);
        var restWire = Serialize(sink.Captured[0]);
        var mcpWire = Serialize(sink.Captured[1]);

        EnvelopeKeys(restWire).ShouldBe(EnvelopeKeys(mcpWire));
        DataKeys(restWire).ShouldBe(DataKeys(mcpWire));
        CardKeys(restWire).ShouldBe(CardKeys(mcpWire));

        // Both report card.created and an actor (drift in field presence fails above).
        restWire.GetProperty("event").GetString().ShouldBe("card.created");
        mcpWire.GetProperty("event").GetString().ShouldBe("card.created");
    }

    // ── Scenario 3: temp create fires card.created only on finalize ──────────────

    [Fact]
    public async Task TempCreate_FiresCardCreated_OnlyOnFinalize()
    {
        // Arrange
        var sink = Sink;
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

        // Act 1 — temp insert: NO event.
        var tempResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards/temp", new
        {
            name = "Temp Card",
            laneId,
        });
        tempResponse.EnsureSuccessStatusCode();
        var tempJson = await tempResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var tempId = tempJson.GetProperty("id").GetGuid();

        sink.Captured.ShouldBeEmpty();

        // Act 2 — finalize: exactly one card.created.
        var finalizeResponse = await _client.PostAsync($"/api/v1/cards/{tempId}/finalize", null);
        finalizeResponse.EnsureSuccessStatusCode();

        sink.Captured.Count.ShouldBe(1);
        sink.Captured[0].EventType.ShouldBe("card.created");
    }

    [Fact]
    public async Task TempCreate_ThenCancel_FiresNoEvent()
    {
        // Arrange
        var sink = Sink;
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

        var tempResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards/temp", new
        {
            name = "Temp To Cancel",
            laneId,
        });
        tempResponse.EnsureSuccessStatusCode();
        var tempJson = await tempResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var tempId = tempJson.GetProperty("id").GetGuid();

        // Act
        var cancelResponse = await _client.PostAsync($"/api/v1/cards/{tempId}/cancel", null);
        cancelResponse.EnsureSuccessStatusCode();

        // Assert — temp insert + cancel both emit nothing.
        sink.Captured.ShouldBeEmpty();
    }

    // ── Scenario 4: card.moved carries from/to — from all five lane-change sites ──

    [Fact]
    public async Task RestReorder_FiresCardMoved_WithFromTo()
    {
        var sink = Sink;
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var (laneA, laneB) = await GetTwoLanesAsync();
        var cardId = await CreateCardInLaneViaRestAsync(laneA, "Reorder Move");
        var fromPosition = await CardPositionAsync(cardId);
        sink.Clear();

        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/reorder", new { laneId = laneB, index = 0 });
        response.EnsureSuccessStatusCode();

        await AssertSingleMoveAsync(sink, cardId, laneA, await LaneNameAsync(laneA), fromPosition, laneB, await LaneNameAsync(laneB));
    }

    [Fact]
    public async Task McpMoveCard_FiresCardMoved_WithFromTo()
    {
        var sink = Sink;
        var (laneA, laneB) = await GetTwoLanesAsync();
        var tools = CreateCardTools();
        var cardId = await CreateCardViaMcpAsync(tools, laneA, "MCP Move");
        var fromPosition = await CardPositionAsync(cardId);
        sink.Clear();

        var result = await tools.MoveCardAsync(CollatticeApiFactory.TestAdminAuthKey, laneB, cardId: cardId, index: 0);
        result.ShouldNotContain("Error");

        await AssertSingleMoveAsync(sink, cardId, laneA, await LaneNameAsync(laneA), fromPosition, laneB, await LaneNameAsync(laneB));
    }

    [Fact]
    public async Task RestPatchWithLaneId_FiresCardMoved_WithFromTo()
    {
        var sink = Sink;
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var (laneA, laneB) = await GetTwoLanesAsync();
        var cardId = await CreateCardInLaneViaRestAsync(laneA, "PATCH Move");
        var fromPosition = await CardPositionAsync(cardId);
        sink.Clear();

        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { laneId = laneB });
        response.EnsureSuccessStatusCode();

        await AssertSingleMoveAsync(sink, cardId, laneA, await LaneNameAsync(laneA), fromPosition, laneB, await LaneNameAsync(laneB));
    }

    [Fact]
    public async Task McpUpdateCardWithLaneId_FiresCardMoved_WithFromTo()
    {
        var sink = Sink;
        var (laneA, laneB) = await GetTwoLanesAsync();
        var tools = CreateCardTools();
        var cardId = await CreateCardViaMcpAsync(tools, laneA, "MCP Update Move");
        var fromPosition = await CardPositionAsync(cardId);
        sink.Clear();

        var result = await tools.UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: cardId, laneId: laneB);
        result.ShouldNotContain("Error");

        await AssertSingleMoveAsync(sink, cardId, laneA, await LaneNameAsync(laneA), fromPosition, laneB, await LaneNameAsync(laneB));
    }

    [Fact]
    public async Task McpBulkUpdateWithLaneId_FiresOneCardMovedPerMovedCard()
    {
        var sink = Sink;
        var (laneA, laneB) = await GetTwoLanesAsync();
        var tools = CreateCardTools();
        var card1 = await CreateCardViaMcpAsync(tools, laneA, "Bulk Move 1");
        var card2 = await CreateCardViaMcpAsync(tools, laneA, "Bulk Move 2");
        sink.Clear();

        var bulk = CreateBulkTools();
        var result = await bulk.BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: $"{card1},{card2}",
            laneId: laneB
        );
        result.ShouldNotContain("\"failed\":2");

        // One card.moved per moved card (N events for N cards) — even though SSE coalesces.
        sink.Captured.Count.ShouldBe(2);
        sink.Captured.ShouldAllBe(e => e.EventType == "card.moved");

        var movedIds = sink.Captured
            .Select(e => Serialize(e).GetProperty("data").GetProperty("card").GetProperty("id").GetGuid())
                .ToHashSet();
        movedIds.ShouldBe([card1, card2], ignoreOrder: true);
    }

    // ── Scenario 4b: no-lane-change emits no card.moved; archive/restore emit none ─

    [Fact]
    public async Task UpdateWithoutLaneChange_FiresNoCardMoved()
    {
        var sink = Sink;
        var (laneId, _) = await GetTwoLanesAsync();
        var tools = CreateCardTools();
        var cardId = await CreateCardViaMcpAsync(tools, laneId, "Name Only");
        sink.Clear();

        // Name-only update (no laneId) — and a PATCH re-asserting the current lane on the lane's last
        // card, which re-appends it to exactly the position it already holds.
        var nameResult = await tools.UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: cardId, name: "Renamed");
        nameResult.ShouldNotContain("Error");

        TestAuthHelper.SetAdminAuth(_client, _factory);
        var sameLanePatch = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { laneId });
        sameLanePatch.EnsureSuccessStatusCode();

        // No card.moved for either: nothing about the card's placement changed.
        sink.Captured.ShouldNotContain(e => e.EventType == "card.moved");
    }

    // ── Scenario 4d: re-asserting the current lane is a move when the card lands somewhere new ──

    [Fact]
    public async Task RestPatchSameLaneNoPosition_NonLastCard_FiresCardMoved_WithinLane()
    {
        var sink = Sink;
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var (laneA, _) = await GetTwoLanesAsync();
        var cardId = await CreateCardInLaneViaRestAsync(laneA, "Reshuffled");
        await CreateCardInLaneViaRestAsync(laneA, "Behind It");
        var fromPosition = await CardPositionAsync(cardId);
        sink.Clear();

        // The current lane with no position re-appends the card to the lane's end — a visible move
        // for a card that was not already last, so it reports exactly one within-lane card.moved.
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { laneId = laneA });
        response.EnsureSuccessStatusCode();

        (await CardPositionAsync(cardId)).ShouldBeGreaterThan(fromPosition);

        var laneName = await LaneNameAsync(laneA);
        await AssertSingleMoveAsync(sink, cardId, laneA, laneName, fromPosition, laneA, laneName);
    }

    [Fact]
    public async Task McpUpdateCardSameLane_PositionChanged_FiresCardMoved_WithinLane()
    {
        var sink = Sink;
        var (laneA, _) = await GetTwoLanesAsync();
        var tools = CreateCardTools();
        await CreateCardViaMcpAsync(tools, laneA, "Already Here");
        var cardId = await CreateCardViaMcpAsync(tools, laneA, "Moves To Top");
        var fromPosition = await CardPositionAsync(cardId);
        sink.Clear();

        // update_card with the current lane and no index places the card at the top of that lane —
        // the MCP twin of the REST re-assert, held to the same resolved-position rule.
        var result = await tools.UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: cardId, laneId: laneA);
        result.ShouldNotContain("Error");

        (await CardPositionAsync(cardId)).ShouldNotBe(fromPosition);

        var laneName = await LaneNameAsync(laneA);
        await AssertSingleMoveAsync(sink, cardId, laneA, laneName, fromPosition, laneA, laneName);
    }

    [Fact]
    public async Task McpUpdateCardSameLane_PositionUnchanged_FiresNoCardMoved()
    {
        var sink = Sink;
        var (laneA, _) = await GetTwoLanesAsync();
        var tools = CreateCardTools();
        var cardId = await CreateCardViaMcpAsync(tools, laneA, "Stays On Top");

        // The first call puts the card at the top; the second asks for exactly where it already is.
        (await tools.UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: cardId, laneId: laneA, index: 0)).ShouldNotContain("Error");
        var settledPosition = await CardPositionAsync(cardId);
        sink.Clear();

        var result = await tools.UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: cardId, laneId: laneA, index: 0);
        result.ShouldNotContain("Error");

        (await CardPositionAsync(cardId)).ShouldBe(settledPosition);
        sink.Captured.ShouldNotContain(e => e.EventType == "card.moved");
    }

    [Fact]
    public async Task ArchiveAndRestore_FireCardArchivedAndRestored_NeverCardMoved()
    {
        var sink = Sink;
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var (laneA, laneB) = await GetTwoLanesAsync();
        var cardId = await CreateCardInLaneViaRestAsync(laneA, "Archive Me");
        sink.Clear();

        // Archive then restore — both route through MoveCardToLaneAsync but emit the
        // domain-distinct card.archived / card.restored at the call-site, NEVER card.moved
        // (the shared move helper stays emission-free).
        var archiveResponse = await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);
        archiveResponse.EnsureSuccessStatusCode();

        var restoreResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/restore", new { laneId = laneB });
        restoreResponse.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["card.archived", "card.restored"]);
        sink.Captured.ShouldNotContain(e => e.EventType == "card.moved");
    }

    // ── Scenario 4c: within-lane movement is a move — equal from/to lane ids ──────

    [Fact]
    public async Task RestPatchPositionOnly_FiresCardMoved_WithinLane()
    {
        var sink = Sink;
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var (laneA, _) = await GetTwoLanesAsync();
        var cardId = await CreateCardInLaneViaRestAsync(laneA, "Position Move");
        var fromPosition = await CardPositionAsync(cardId);
        sink.Clear();

        // A position-only PATCH (no laneId) is a within-lane move — one card.moved carrying the
        // position transition, with from/to lane ids equal.
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { position = fromPosition + 5 });
        response.EnsureSuccessStatusCode();

        var laneName = await LaneNameAsync(laneA);
        await AssertSingleMoveAsync(sink, cardId, laneA, laneName, fromPosition, laneA, laneName);

        // A consumer disambiguates a within-lane reorder from a cross-lane move on exactly this.
        var data = Serialize(sink.Captured[0]).GetProperty("data");
        data.GetProperty("from").GetProperty("laneId").GetGuid()
            .ShouldBe(data.GetProperty("to").GetProperty("laneId").GetGuid());
    }

    [Fact]
    public async Task RestPatchPositionUnchanged_FiresNoCardMoved()
    {
        var sink = Sink;
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var (laneA, _) = await GetTwoLanesAsync();
        var cardId = await CreateCardInLaneViaRestAsync(laneA, "Position No-op");
        var currentPosition = await CardPositionAsync(cardId);
        sink.Clear();

        // Re-send the current position — the real-position-changed guard suppresses card.moved.
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { position = currentPosition });
        response.EnsureSuccessStatusCode();

        sink.Captured.ShouldNotContain(e => e.EventType == "card.moved");
    }

    [Fact]
    public async Task RestReorderWithinLane_FiresCardMoved_WithEqualLaneIds()
    {
        var sink = Sink;
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var (laneA, _) = await GetTwoLanesAsync();
        var cardId = await CreateCardInLaneViaRestAsync(laneA, "Reorder Within");
        var fromPosition = await CardPositionAsync(cardId);
        sink.Clear();

        // Reordering to the SAME lane is a within-lane move — it already fires card.moved with equal
        // from/to lane ids; this pins that the reorder path and the PATCH-position path agree.
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/reorder", new { laneId = laneA, index = 0 });
        response.EnsureSuccessStatusCode();

        var laneName = await LaneNameAsync(laneA);
        await AssertSingleMoveAsync(sink, cardId, laneA, laneName, fromPosition, laneA, laneName);
    }

    // bulk_update_cards within the cards' own lane. The batch saves once, so "from" is each
    // card's placement before the call and "to" its saved placement; a card reports only when
    // the two differ. Each test seeds its own board so the lane holds exactly the cards it
    // names, at dense positions.

    [Fact]
    public async Task McpBulkUpdateSameLane_PositionChanged_FiresCardMovedWithinLane()
    {
        // Arrange — [A, B, C]; moving C to the top renumbers A and B too
        var sink = Sink;
        var (laneId, _, cards) = await SeedBoardAsync(3);
        var laneName = await LaneNameAsync(laneId);
        sink.Clear();

        // Act — the current lane with no index places C at the top
        var result = await CreateBulkTools().BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: $"{cards[2]}",
            laneId: laneId
        );

        // Assert — one card.moved, for C only: A and B were nudged by C's move, not moved
        result.ShouldContain("\"succeeded\":1");
        (await CardPositionAsync(cards[2])).ShouldBe(0);
        await AssertSingleMoveAsync(sink, cards[2], laneId, laneName, 20, laneId, laneName);
    }

    [Fact]
    public async Task McpBulkUpdateSameLane_PositionUnchanged_FiresNoCardMoved()
    {
        // Arrange — [A, B, C]
        var sink = Sink;
        var (laneId, _, cards) = await SeedBoardAsync(3);
        sink.Clear();

        // Act — A is already at the top of its lane
        var result = await CreateBulkTools().BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: $"{cards[0]}",
            laneId: laneId,
            index: 0
        );

        // Assert
        result.ShouldContain("\"succeeded\":1");
        (await CardPositionAsync(cards[0])).ShouldBe(0);
        sink.Captured.ShouldNotContain(e => e.EventType == "card.moved");
    }

    [Fact]
    public async Task McpBulkUpdateSameLane_MultiCard_ReportsExactlyTheCardsWhoseSavedPlacementChanged()
    {
        // Arrange — [A, B, C, D]
        var sink = Sink;
        var (laneId, _, cards) = await SeedBoardAsync(4);
        Guid[] batch = [cards[0], cards[2]];
        var before = await PlacementsAsync(cards);
        sink.Clear();

        // Act — A and C to index 3. A's step renumbers the lane and shifts C from 20 to 10 before
        // C's own turn, so C's position at its turn is neither where it was before the call nor
        // where it ends up.
        var result = await CreateBulkTools().BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: $"{batch[0]},{batch[1]}",
            laneId: laneId,
            index: 3
        );

        // Assert — the expectation is derived from the saved state, so it holds whatever order
        // the batch places the cards in: one card.moved per batch card whose placement differs
        // from before the call, carrying exactly the before and after placements
        result.ShouldContain("\"succeeded\":2");
        var after = await PlacementsAsync(cards);
        var changed = batch.Where(id => before[id] != after[id]).ToList();

        changed.ShouldNotBeEmpty("fixture must move at least one batch card");

        sink.Captured.ShouldAllBe(e => e.EventType == "card.moved");
        sink.Captured.Count.ShouldBe(changed.Count);

        foreach (var data in sink.Captured.Select(e => Serialize(e).GetProperty("data")))
        {
            var cardId = data.GetProperty("card").GetProperty("id").GetGuid();
            changed.ShouldContain(cardId);

            data.GetProperty("from").GetProperty("laneId").GetGuid().ShouldBe(before[cardId].LaneId);
            data.GetProperty("from").GetProperty("position").GetInt32().ShouldBe(before[cardId].Position);
            data.GetProperty("to").GetProperty("laneId").GetGuid().ShouldBe(after[cardId].LaneId);
            data.GetProperty("to").GetProperty("position").GetInt32().ShouldBe(after[cardId].Position);
        }
    }

    [Fact]
    public async Task McpBulkUpdateCrossLane_FromIsEachCardsPositionBeforeTheBatch()
    {
        // Arrange — [A, B, C] in the source lane, an empty target lane
        var sink = Sink;
        var (sourceLaneId, targetLaneId, cards) = await SeedBoardAsync(3);
        sink.Clear();

        // Act — B leaves first, which renumbers C from 20 to 10 before C's own turn
        var result = await CreateBulkTools().BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: $"{cards[1]},{cards[2]}",
            laneId: targetLaneId
        );

        // Assert — each card's from is where it sat before the call
        result.ShouldContain("\"succeeded\":2");
        sink.Captured.Count.ShouldBe(2);
        sink.Captured.ShouldAllBe(e => e.EventType == "card.moved");

        var fromByCard = sink.Captured
            .Select(e => Serialize(e).GetProperty("data"))
                .ToDictionary(d => d.GetProperty("card").GetProperty("id").GetGuid(), d => d.GetProperty("from"));

        fromByCard[cards[1]].GetProperty("laneId").GetGuid().ShouldBe(sourceLaneId);
        fromByCard[cards[1]].GetProperty("position").GetInt32().ShouldBe(10);
        fromByCard[cards[2]].GetProperty("laneId").GetGuid().ShouldBe(sourceLaneId);
        fromByCard[cards[2]].GetProperty("position").GetInt32().ShouldBe(20);
    }

    [Fact]
    public async Task McpBulkUpdateSameLane_TwoCardsToTheTop_ReportsWhereTheBatchPlacedThem()
    {
        // Arrange — [A, B, C, D]
        var sink = Sink;
        var (laneId, _, cards) = await SeedBoardAsync(4);
        sink.Clear();

        // Act — C and D to the top, in that order
        var result = await CreateBulkTools().BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: $"{cards[2]},{cards[3]}",
            laneId: laneId
        );

        // Assert — the lane is [C, D, A, B]; only the batch reports, C from 20 to 0 and D from 30 to 10
        result.ShouldContain("\"succeeded\":2");
        sink.Captured.Count.ShouldBe(2);
        sink.Captured.ShouldAllBe(e => e.EventType == "card.moved");

        var moves = sink.Captured
            .Select(e => Serialize(e).GetProperty("data"))
                .ToDictionary(d => d.GetProperty("card").GetProperty("id").GetGuid());

        moves[cards[2]].GetProperty("from").GetProperty("position").GetInt32().ShouldBe(20);
        moves[cards[2]].GetProperty("to").GetProperty("position").GetInt32().ShouldBe(0);
        moves[cards[3]].GetProperty("from").GetProperty("position").GetInt32().ShouldBe(30);
        moves[cards[3]].GetProperty("to").GetProperty("position").GetInt32().ShouldBe(10);
    }

    // ── Scenario 5: SSE byte-for-byte unchanged across all converted sites ────────

    [Fact]
    public async Task SseWire_StaysByteForByteUnchanged_AcrossConvertedSites()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var reader = broadcaster.Subscribe(_factory.DefaultBoardId);
        try
        {
            var (laneA, laneB) = await GetTwoLanesAsync();
            var tools = CreateCardTools();

            // One create site + the three NEWLY-COVERED move sites (PATCH+laneId,
            // update_card+laneId, bulk_update+laneId) — the conversions whose SSE
            // behavior is new. Each must still ring exactly the thin "board-updated" bell.
            var restCardId = await CreateCardInLaneViaRestAsync(laneA, "SSE Create");

            TestAuthHelper.SetAdminAuth(_client, _factory);
            var patchResponse = await _client.PatchAsJsonAsync($"/api/v1/cards/{restCardId}", new { laneId = laneB });
            patchResponse.EnsureSuccessStatusCode();

            var updateCardId = await CreateCardViaMcpAsync(tools, laneA, "SSE Update");
            var updateResult = await tools.UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: updateCardId, laneId: laneB);
            updateResult.ShouldNotContain("Error");

            // The single-site signals so far are byte-identical "board-updated" — no payload
            // leak, no new event type, no double-emit on the MCP sites.
            var singleSiteSignals = DrainChannel(reader);
            singleSiteSignals.ShouldNotBeEmpty();
            singleSiteSignals.ShouldAllBe(s => s == "board-updated");

            // The bulk arm uses ≥2 cards so the SSE CARDINALITY is
            // load-bearing. A 1-card bulk move cannot distinguish the correct one-bell-per-board
            // coalesce from the naive per-card broadcaster.Publish (which rings N bells) — one
            // over-ring is indistinguishable from one correct bell. With 2 cards, the naive break
            // rings 2+ bells; the correct coalesce rings exactly 1. Drain right before the bulk op
            // so the count is the bulk op's bells alone.
            var bulkCard1 = await CreateCardViaMcpAsync(tools, laneA, "SSE Bulk 1");
            var bulkCard2 = await CreateCardViaMcpAsync(tools, laneA, "SSE Bulk 2");
            DrainChannel(reader); // discard the creates' bells — measure the bulk op in isolation.

            var bulk = CreateBulkTools();
            var bulkResult = await bulk.BulkUpdateCardsAsync
            (
                CollatticeApiFactory.TestAdminAuthKey,
                cardIds: $"{bulkCard1},{bulkCard2}",
                laneId: laneB
            );
            bulkResult.ShouldNotContain("\"failed\":2");

            // Exactly ONE SSE bell for the 2-card bulk move (the BulkExecution per-board coalesce),
            // and it is the byte-identical "board-updated" string. The naive per-card publish would
            // ring 2 here — this is the assertion that would go red on that regression.
            var bulkSignals = DrainChannel(reader);
            bulkSignals.Count.ShouldBe(1);
            bulkSignals[0].ShouldBe("board-updated");
        }
        finally
        {
            broadcaster.Unsubscribe(_factory.DefaultBoardId, reader);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateCardInLaneViaRestAsync(Guid laneId, string name)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new { name, laneId });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    // A fresh board with two lanes and cardCount cards in the first, at the dense positions
    // 0, 10, 20, ... the bulk tests reason about. The positions are asserted rather than
    // assumed, so a change to how cards are created fails here and not as a wrong event.
    private async Task<(Guid SourceLaneId, Guid TargetLaneId, List<Guid> Cards)> SeedBoardAsync(int cardCount)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var boardResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = $"Bulk Move {Guid.NewGuid():N}" });
        boardResponse.EnsureSuccessStatusCode();
        var boardId = (await boardResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();

        var sourceResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes", new { name = "Source", position = 0 });
        sourceResponse.EnsureSuccessStatusCode();
        var sourceLaneId = (await sourceResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();

        var targetResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes", new { name = "Target", position = 1 });
        targetResponse.EnsureSuccessStatusCode();
        var targetLaneId = (await targetResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();

        List<Guid> cards = [];
        for (var i = 0; i < cardCount; i++)
        {
            var cardResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/cards", new { name = $"Card {i.ToString(CultureInfo.InvariantCulture)}", laneId = sourceLaneId });
            cardResponse.EnsureSuccessStatusCode();
            cards.Add((await cardResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid());
        }

        for (var i = 0; i < cardCount; i++)
        {
            (await CardPositionAsync(cards[i])).ShouldBe(i * 10);
        }

        return (sourceLaneId, targetLaneId, cards);
    }

    private static async Task<Guid> CreateCardViaMcpAsync(CardTools tools, Guid laneId, string name)
    {
        var result = await tools.CreateCardAsync(CollatticeApiFactory.TestAdminAuthKey, name, laneId);
        result.ShouldNotContain("Error");
        return JsonDocument.Parse(result).RootElement.GetProperty("id").GetGuid();
    }

    private async Task AssertSingleMoveAsync
    (
        CapturingWebhookSink sink,
        Guid cardId,
        Guid fromLaneId,
        string fromLaneName,
        int fromPosition,
        Guid toLaneId,
        string toLaneName
    )
    {
        sink.Captured.Count.ShouldBe(1);
        var evt = sink.Captured[0];
        evt.EventType.ShouldBe("card.moved");

        var wire = Serialize(evt);
        var data = wire.GetProperty("data");

        // from = the source lane/position captured BEFORE the mutation.
        data.GetProperty("from").GetProperty("laneId").GetGuid().ShouldBe(fromLaneId);
        data.GetProperty("from").GetProperty("laneName").GetString().ShouldBe(fromLaneName);
        data.GetProperty("from").GetProperty("position").GetInt32().ShouldBe(fromPosition);

        // to = the target lane and the card's ACTUAL post-move position (sites that don't
        // pass an explicit index append to the lane, so the position is not always 0).
        var actualPosition = await CardPositionAsync(cardId);
        data.GetProperty("to").GetProperty("laneId").GetGuid().ShouldBe(toLaneId);
        data.GetProperty("to").GetProperty("laneName").GetString().ShouldBe(toLaneName);
        data.GetProperty("to").GetProperty("position").GetInt32().ShouldBe(actualPosition);

        // The embedded fat card reflects the target lane.
        data.GetProperty("card").GetProperty("laneId").GetGuid().ShouldBe(toLaneId);
        data.GetProperty("card").GetProperty("position").GetInt32().ShouldBe(actualPosition);
    }

    private async Task<Dictionary<Guid, (Guid LaneId, int Position)>> PlacementsAsync(List<Guid> cardIds)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        return await db.Cards
            .Where(c => cardIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => (c.LaneId, c.Position));
    }

    private async Task<int> CardPositionAsync(Guid cardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.Cards.Where(c => c.Id == cardId).Select(c => c.Position).FirstAsync();
    }

    private static List<string> DrainChannel(System.Threading.Channels.ChannelReader<string> reader)
    {
        var items = new List<string>();
        while (reader.TryRead(out var item))
        {
            items.Add(item);
        }

        return items;
    }

    private static List<string> EnvelopeKeys(JsonElement wire) =>
        [.. wire.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    private static List<string> DataKeys(JsonElement wire) =>
        [.. wire.GetProperty("data").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    private static List<string> CardKeys(JsonElement wire) =>
        [.. wire.GetProperty("data").GetProperty("card").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
}
