using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Sealed so the plain IDisposable shape below is complete; no derived class can add state to dispose.
public sealed class McpCardToolsTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly List<IServiceScope> _scopes = [];

    private (BoardDbContext Db, CardTools Tools, string AuthKey) CreateTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var auth = new McpAuthService(new UserResolver(db));
        var tools = new CardTools(db, auth, broadcaster);
        return (db, tools, CollatticeApiFactory.TestAdminAuthKey);
    }

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }
    }

    private async Task<(Guid LaneId1, Guid LaneId2)> GetTwoLaneIdsAsync(BoardDbContext db)
    {
        var lanes = await db.Lanes
            .Where(l => l.BoardId == _factory.DefaultBoardId)
            .OrderBy(l => l.Position)
                .Select(l => l.Id)
                    .ToListAsync();

        lanes.Count.ShouldBeGreaterThanOrEqualTo(2);
        return (lanes[0], lanes[1]);
    }

    private async Task<Guid> CreateCardInLaneAsync(CardTools tools, string authKey, Guid laneId, string name = "Test Card")
    {
        var result = await tools.CreateCardAsync(authKey, name, laneId);
        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        return json.RootElement.GetProperty("id").GetGuid();
    }

    // ── No-op guard ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateCard_NoChangesSpecified_ReturnsNoOpMessage()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var cardId = await CreateCardInLaneAsync(tools, authKey, lanes[0]);

        // Act
        var result = await tools.UpdateCardAsync(authKey, cardId);

        // Assert
        result.ShouldBe("No changes specified.");
    }

    [Fact]
    public async Task UpdateCard_NoChangesSpecified_DoesNotUpdateTimestamp()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var cardId = await CreateCardInLaneAsync(tools, authKey, lanes[0]);
        var card = await db.Cards.FindAsync(cardId);
        var originalTimestamp = card!.LastUpdatedAtUtc;

        await Task.Delay(50);

        // Act
        var result = await tools.UpdateCardAsync(authKey, cardId);

        // Assert
        result.ShouldBe("No changes specified.");
        var refreshedCard = await db.Cards.FindAsync(cardId);
        refreshedCard!.LastUpdatedAtUtc.ShouldBe(originalTimestamp);
    }

    [Fact]
    public async Task UpdateCard_ExistingCard_ReturnsEnrichedCardSummary()
    {
        // Arrange — parity with create_card: the JSON payload carries sizeName,
        // labels, commentCount, attachmentCount, isArchived so the caller has
        // everything without a follow-up get_card.
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var cardId = await CreateCardInLaneAsync(tools, authKey, lanes[0]);

        // Act
        var result = await tools.UpdateCardAsync(authKey, cardId, name: "Marcus enriched assert");

        // Assert — parse the JSON payload and check every enriched field
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("name").GetString().ShouldBe("Marcus enriched assert");
        root.GetProperty("sizeName").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("commentCount").GetInt32().ShouldBe(0);
        root.GetProperty("attachmentCount").GetInt32().ShouldBe(0);
        root.GetProperty("isArchived").GetBoolean().ShouldBeFalse();
        root.GetProperty("labels").GetArrayLength().ShouldBe(0);
    }

    // ── Lane move via update_card ───────────────────────────────────────────

    [Fact]
    public async Task UpdateCard_WithLaneId_MovesCardToTargetLane()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var (lane1, lane2) = await GetTwoLaneIdsAsync(db);
        var cardId = await CreateCardInLaneAsync(tools, authKey, lane1, "Move Me");

        // Act
        var result = await tools.UpdateCardAsync(authKey, cardId, laneId: lane2);

        // Assert
        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("laneId").GetGuid().ShouldBe(lane2);
    }

    [Fact]
    public async Task UpdateCard_WithLaneIdAndIndex_PlacesCardAtSpecifiedPosition()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var (lane1, lane2) = await GetTwoLaneIdsAsync(db);

        // Create two existing cards in lane2
        await CreateCardInLaneAsync(tools, authKey, lane2, "Existing Card A");
        await CreateCardInLaneAsync(tools, authKey, lane2, "Existing Card B");

        // Create the card to move in lane1
        var cardId = await CreateCardInLaneAsync(tools, authKey, lane1, "Insert Me");

        // Act — move to lane2 at index 0 (front)
        var result = await tools.UpdateCardAsync(authKey, cardId, laneId: lane2, index: 0);

        // Assert
        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("laneId").GetGuid().ShouldBe(lane2);
        json.RootElement.GetProperty("position").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task UpdateCard_WithLaneIdNoIndex_PlacesAtTopOfLane()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var (lane1, lane2) = await GetTwoLaneIdsAsync(db);

        // Create an existing card in lane2
        await CreateCardInLaneAsync(tools, authKey, lane2, "Already Here");

        // Create the card to move
        var cardId = await CreateCardInLaneAsync(tools, authKey, lane1, "Prepend Me");

        // Act — move to lane2 without index (should place at top)
        var result = await tools.UpdateCardAsync(authKey, cardId, laneId: lane2);

        // Assert
        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("laneId").GetGuid().ShouldBe(lane2);

        // Card should have the lowest position in lane2
        var minPosOtherCards = await db.Cards
            .Where(c => c.LaneId == lane2 && c.Id != cardId)
                .MinAsync(c => (int?)c.Position) ?? int.MaxValue;
        json.RootElement.GetProperty("position").GetInt32().ShouldBeLessThan(minPosOtherCards);
    }

    [Fact]
    public async Task UpdateCard_WithInvalidLaneId_ReturnsError()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var cardId = await CreateCardInLaneAsync(tools, authKey, lanes[0]);

        // Act
        var result = await tools.UpdateCardAsync(authKey, cardId, laneId: Guid.NewGuid());

        // Assert
        result.ShouldContain("Error: Lane not found.");
    }

    [Fact]
    public async Task UpdateCard_WithLaneId_ReordersSourceLane()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var (lane1, lane2) = await GetTwoLaneIdsAsync(db);

        // Create three cards in lane1
        var card1Id = await CreateCardInLaneAsync(tools, authKey, lane1, "Source A");
        var card2Id = await CreateCardInLaneAsync(tools, authKey, lane1, "Source B");
        var card3Id = await CreateCardInLaneAsync(tools, authKey, lane1, "Source C");

        // Act — move the middle card to lane2
        await tools.UpdateCardAsync(authKey, card2Id, laneId: lane2);

        // Assert — card2 is no longer in lane1
        var card2 = await db.Cards.FindAsync(card2Id);
        card2!.LaneId.ShouldBe(lane2);

        // Assert — remaining source cards maintain gap-free position spacing
        var sourceCards = await db.Cards
            .Where(c => c.LaneId == lane1 && (c.Id == card1Id || c.Id == card3Id))
            .OrderBy(c => c.Position)
                .ToListAsync();

        sourceCards.Count.ShouldBe(2);
        sourceCards.Select(c => c.Id).ShouldContain(card1Id);
        sourceCards.Select(c => c.Id).ShouldContain(card3Id);
        // Positions should be contiguous multiples of 10 (gap-free after reorder)
        (sourceCards[1].Position - sourceCards[0].Position).ShouldBe(10);
    }

    // ── Label replace via update_card ───────────────────────────────────────

    [Fact]
    public async Task UpdateCard_WithLabelIds_AssignsLabels()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var cardId = await CreateCardInLaneAsync(tools, authKey, lanes[0]);

        // Create labels
        var label1 = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"McpLabel1-{Guid.NewGuid()}", Color = "red" };
        var label2 = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"McpLabel2-{Guid.NewGuid()}", Color = "blue" };
        db.Labels.AddRange(label1, label2);
        await db.SaveChangesAsync();

        // Act
        var result = await tools.UpdateCardAsync(authKey, cardId, labelIds: $"{label1.Id},{label2.Id}");

        // Assert
        result.ShouldNotContain("Error");
        var cardLabels = await db.CardLabels.Where(cl => cl.CardId == cardId).Select(cl => cl.LabelId).ToListAsync();
        cardLabels.Count.ShouldBe(2);
        cardLabels.ShouldContain(label1.Id);
        cardLabels.ShouldContain(label2.Id);
    }

    [Fact]
    public async Task UpdateCard_WithLabelIds_ReplacesExistingLabels()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var cardId = await CreateCardInLaneAsync(tools, authKey, lanes[0]);

        var label1 = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"Replace1-{Guid.NewGuid()}", Color = "red" };
        var label2 = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"Replace2-{Guid.NewGuid()}", Color = "green" };
        var label3 = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"Replace3-{Guid.NewGuid()}", Color = "blue" };
        db.Labels.AddRange(label1, label2, label3);
        await db.SaveChangesAsync();

        // First: assign label1 and label2
        await tools.UpdateCardAsync(authKey, cardId, labelIds: $"{label1.Id},{label2.Id}");

        // Act — replace with label2 and label3 (label1 removed, label3 added, label2 kept)
        var result = await tools.UpdateCardAsync(authKey, cardId, labelIds: $"{label2.Id},{label3.Id}");

        // Assert
        result.ShouldNotContain("Error");
        var cardLabels = await db.CardLabels.Where(cl => cl.CardId == cardId).Select(cl => cl.LabelId).ToListAsync();
        cardLabels.Count.ShouldBe(2);
        cardLabels.ShouldContain(label2.Id);
        cardLabels.ShouldContain(label3.Id);
        cardLabels.ShouldNotContain(label1.Id);
    }

    [Fact]
    public async Task UpdateCard_WithEmptyLabelIds_ClearsAllLabels()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var cardId = await CreateCardInLaneAsync(tools, authKey, lanes[0]);

        var label = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"ClearMe-{Guid.NewGuid()}", Color = "red" };
        db.Labels.Add(label);
        await db.SaveChangesAsync();

        await tools.UpdateCardAsync(authKey, cardId, labelIds: label.Id.ToString());

        // Act — clear all labels
        var result = await tools.UpdateCardAsync(authKey, cardId, labelIds: "");

        // Assert
        result.ShouldNotContain("Error");
        var cardLabels = await db.CardLabels.Where(cl => cl.CardId == cardId).ToListAsync();
        cardLabels.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpdateCard_WithInvalidLabelId_ReturnsError()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var cardId = await CreateCardInLaneAsync(tools, authKey, lanes[0]);

        var bogusLabelId = Guid.NewGuid();

        // Act
        var result = await tools.UpdateCardAsync(authKey, cardId, labelIds: bogusLabelId.ToString());

        // Assert
        result.ShouldContain("Error: Labels not found or not on the same board");
    }

    [Fact]
    public async Task UpdateCard_WithMalformedLabelIds_ReturnsError()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var cardId = await CreateCardInLaneAsync(tools, authKey, lanes[0]);

        // Act
        var result = await tools.UpdateCardAsync(authKey, cardId, labelIds: "not-a-guid");

        // Assert
        result.ShouldContain("Error: Invalid label ID");
    }

    // ── create_card labelIds ────────────────────────────────────────────────

    [Fact]
    public async Task CreateCard_WithLabelIds_AssignsLabels()
    {
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var label1 = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"CL1-{Guid.NewGuid()}", Color = "red" };
        var label2 = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"CL2-{Guid.NewGuid()}", Color = "blue" };
        db.Labels.AddRange(label1, label2);
        await db.SaveChangesAsync();

        var result = await tools.CreateCardAsync(authKey, "Labeled Card", lanes[0], labelIds: $"{label1.Id},{label2.Id}");

        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        var cardId = json.RootElement.GetProperty("id").GetGuid();
        var cardLabels = await db.CardLabels.Where(cl => cl.CardId == cardId).Select(cl => cl.LabelId).ToListAsync();
        cardLabels.Count.ShouldBe(2);
        cardLabels.ShouldContain(label1.Id);
        cardLabels.ShouldContain(label2.Id);
    }

    [Fact]
    public async Task CreateCard_WithNoLabelIds_CreatesCardWithoutLabels()
    {
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();

        var result = await tools.CreateCardAsync(authKey, "No Labels", lanes[0]);

        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        var cardId = json.RootElement.GetProperty("id").GetGuid();
        (await db.CardLabels.Where(cl => cl.CardId == cardId).ToListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateCard_WithNonexistentLabel_ReturnsError()
    {
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();

        var result = await tools.CreateCardAsync(authKey, "Bad Label", lanes[0], labelIds: Guid.NewGuid().ToString());

        result.ShouldContain("Error");
    }

    [Fact]
    public async Task CreateCard_WithInvalidLabelIdFormat_ReturnsError()
    {
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();

        var result = await tools.CreateCardAsync(authKey, "Bad Format", lanes[0], labelIds: "not-a-guid");

        result.ShouldContain("Error");
    }

    [Fact]
    public async Task CreateCard_WithEmptyLabelIds_CreatesCardWithoutLabels()
    {
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();

        var result = await tools.CreateCardAsync(authKey, "Empty Labels", lanes[0], labelIds: "");

        result.ShouldNotContain("Error");
    }

    // ── labelIds JSON-array shape (regression cover) ───────────────

    [Fact]
    public async Task CreateCard_WithLabelIdsAsJsonArrayString_AssignsLabels()
    {
        // Regression: some MCP-host clients forward array-typed tool args as a JSON-string
        // (e.g. '["guid1","guid2"]') instead of unwrapping to the documented CSV shape.
        // The handler must accept both.
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var label1 = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"JA1-{Guid.NewGuid()}", Color = "red" };
        var label2 = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"JA2-{Guid.NewGuid()}", Color = "blue" };
        db.Labels.AddRange(label1, label2);
        await db.SaveChangesAsync();

        var jsonArrayShape = JsonSerializer.Serialize(new[] { label1.Id.ToString(), label2.Id.ToString() });

        var result = await tools.CreateCardAsync(authKey, "Json Array Labels", lanes[0], labelIds: jsonArrayShape);

        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        var cardId = json.RootElement.GetProperty("id").GetGuid();
        var cardLabels = await db.CardLabels.Where(cl => cl.CardId == cardId).Select(cl => cl.LabelId).ToListAsync();
        cardLabels.Count.ShouldBe(2);
        cardLabels.ShouldContain(label1.Id);
        cardLabels.ShouldContain(label2.Id);
    }

    [Fact]
    public async Task CreateCard_WithSingleLabelIdAsJsonArrayString_AssignsLabel()
    {
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var label = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"JA1S-{Guid.NewGuid()}", Color = "green" };
        db.Labels.Add(label);
        await db.SaveChangesAsync();

        var jsonArrayShape = JsonSerializer.Serialize(new[] { label.Id.ToString() });

        var result = await tools.CreateCardAsync(authKey, "Single Json Array", lanes[0], labelIds: jsonArrayShape);

        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        var cardId = json.RootElement.GetProperty("id").GetGuid();
        var cardLabels = await db.CardLabels.Where(cl => cl.CardId == cardId).Select(cl => cl.LabelId).ToListAsync();
        cardLabels.Count.ShouldBe(1);
        cardLabels.ShouldContain(label.Id);
    }

    [Fact]
    public async Task CreateCard_WithEmptyJsonArrayString_CreatesCardWithoutLabels()
    {
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();

        var result = await tools.CreateCardAsync(authKey, "Empty Json Array", lanes[0], labelIds: "[]");

        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        var cardId = json.RootElement.GetProperty("id").GetGuid();
        (await db.CardLabels.Where(cl => cl.CardId == cardId).ToListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateCard_WithMalformedJsonArrayString_ReturnsError()
    {
        // A string that looks array-like but isn't valid JSON should not crash —
        // it falls through to the CSV path and fails GUID parsing there.
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();

        var result = await tools.CreateCardAsync(authKey, "Malformed Json", lanes[0], labelIds: "[not-valid-json]");

        result.ShouldContain("Error");
    }

    [Fact]
    public async Task UpdateCard_WithLabelIdsAsJsonArrayString_ReplacesLabels()
    {
        // Same shape coverage for update_card — same parser, both surfaces care.
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var cardId = await CreateCardInLaneAsync(tools, authKey, lanes[0]);
        var label1 = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"UJA1-{Guid.NewGuid()}", Color = "red" };
        var label2 = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"UJA2-{Guid.NewGuid()}", Color = "blue" };
        db.Labels.AddRange(label1, label2);
        await db.SaveChangesAsync();

        var jsonArrayShape = JsonSerializer.Serialize(new[] { label1.Id.ToString(), label2.Id.ToString() });

        var result = await tools.UpdateCardAsync(authKey, cardId, labelIds: jsonArrayShape);

        result.ShouldNotContain("Error");
        var cardLabels = await db.CardLabels.Where(cl => cl.CardId == cardId).Select(cl => cl.LabelId).ToListAsync();
        cardLabels.Count.ShouldBe(2);
        cardLabels.ShouldContain(label1.Id);
        cardLabels.ShouldContain(label2.Id);
    }

    [Fact]
    public async Task UpdateCard_WithEmptyJsonArrayString_ClearsLabels()
    {
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var cardId = await CreateCardInLaneAsync(tools, authKey, lanes[0]);
        var label = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"UJAClear-{Guid.NewGuid()}", Color = "red" };
        db.Labels.Add(label);
        await db.SaveChangesAsync();
        await tools.UpdateCardAsync(authKey, cardId, labelIds: label.Id.ToString());

        var result = await tools.UpdateCardAsync(authKey, cardId, labelIds: "[]");

        result.ShouldNotContain("Error");
        (await db.CardLabels.Where(cl => cl.CardId == cardId).ToListAsync()).ShouldBeEmpty();
    }

    // ── Hardened reorder/move tests ─────────────────────────────────────

    [Fact]
    public async Task MoveCard_SameLaneReorder_PositionsAreContiguous()
    {
        // Arrange — use a dedicated lane to avoid interference from other tests
        var (db, tools, authKey) = CreateTools();
        var lane = new Lane { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = "Reorder Lane", Position = 900 };
        db.Lanes.Add(lane);
        await db.SaveChangesAsync();

        var card1Id = await CreateCardInLaneAsync(tools, authKey, lane.Id, "Reorder A");
        var card2Id = await CreateCardInLaneAsync(tools, authKey, lane.Id, "Reorder B");
        var card3Id = await CreateCardInLaneAsync(tools, authKey, lane.Id, "Reorder C");

        // Act — move card3 to index 0 (front) within same lane
        await tools.MoveCardAsync(authKey, lane.Id, cardId: card3Id, index: 0);

        // Assert — card3 should be first, positions contiguous: 0, 10, 20
        var cards = await db.Cards.Where(c => c.LaneId == lane.Id)
            .OrderBy(c => c.Position).ToListAsync();
        cards.Count.ShouldBe(3);
        cards[0].Position.ShouldBe(0);
        cards[1].Position.ShouldBe(10);
        cards[2].Position.ShouldBe(20);
        cards[0].Id.ShouldBe(card3Id);
    }

    [Fact]
    public async Task MoveCard_CrossLane_SourceAndTargetBothReordered()
    {
        // Arrange — use dedicated lanes to avoid interference from other tests
        var (db, tools, authKey) = CreateTools();
        var srcLane = new Lane { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = "CrossSrc", Position = 901 };
        var tgtLane = new Lane { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = "CrossTgt", Position = 902 };
        db.Lanes.AddRange(srcLane, tgtLane);
        await db.SaveChangesAsync();

        var s1 = await CreateCardInLaneAsync(tools, authKey, srcLane.Id, "Source 1");
        var s2 = await CreateCardInLaneAsync(tools, authKey, srcLane.Id, "Source 2");
        var s3 = await CreateCardInLaneAsync(tools, authKey, srcLane.Id, "Source 3");
        var t1 = await CreateCardInLaneAsync(tools, authKey, tgtLane.Id, "Target 1");

        // Act — move s2 to target lane at index 0
        await tools.MoveCardAsync(authKey, tgtLane.Id, cardId: s2, index: 0);

        // Assert — source lane: s1, s3 with positions 0, 10
        var sourceCards = await db.Cards.Where(c => c.LaneId == srcLane.Id)
            .OrderBy(c => c.Position).ToListAsync();
        sourceCards.Count.ShouldBe(2);
        sourceCards[0].Position.ShouldBe(0);
        sourceCards[1].Position.ShouldBe(10);

        // Assert — target lane: s2 at front, t1 after
        var targetCards = await db.Cards.Where(c => c.LaneId == tgtLane.Id)
            .OrderBy(c => c.Position).ToListAsync();
        targetCards.Count.ShouldBe(2);
        targetCards[0].Id.ShouldBe(s2);
        targetCards[0].Position.ShouldBe(0);
        targetCards[1].Position.ShouldBe(10);
    }

    [Fact]
    public async Task MoveCard_IndexBeyondEnd_ClampsToEnd()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var (lane1, lane2) = await GetTwoLaneIdsAsync(db);

        var existing = await CreateCardInLaneAsync(tools, authKey, lane2, "Existing");
        var card = await CreateCardInLaneAsync(tools, authKey, lane1, "Clamp Test");

        // Count cards already in lane2 (excluding the card being moved)
        var existingCount = await db.Cards.CountAsync(c => c.LaneId == lane2);

        // Act — move with index 999 (way beyond end)
        var result = await tools.MoveCardAsync(authKey, lane2, cardId: card, index: 999);

        // Assert — clamped to end (after all existing cards)
        result.ShouldContain($"moved to lane at index {existingCount.ToString(CultureInfo.InvariantCulture)}");
    }

    [Fact]
    public async Task MoveCard_NegativeIndex_ClampsToZero()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var (lane1, lane2) = await GetTwoLaneIdsAsync(db);

        var card = await CreateCardInLaneAsync(tools, authKey, lane1, "Negative Test");

        // Act
        var result = await tools.MoveCardAsync(authKey, lane2, cardId: card, index: -5);

        // Assert
        result.ShouldContain("moved to lane at index 0");
    }

    [Fact]
    public async Task UpdateCard_MoveWithinSameLane_PositionsContiguous()
    {
        // Arrange — use a dedicated lane to avoid interference from other tests
        var (db, tools, authKey) = CreateTools();
        var lane = new Lane { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = "UpdateReorder Lane", Position = 903 };
        db.Lanes.Add(lane);
        await db.SaveChangesAsync();

        var c1 = await CreateCardInLaneAsync(tools, authKey, lane.Id, "Same Lane A");
        var c2 = await CreateCardInLaneAsync(tools, authKey, lane.Id, "Same Lane B");
        var c3 = await CreateCardInLaneAsync(tools, authKey, lane.Id, "Same Lane C");

        // Act — move c1 to index 2 (end) via update_card
        await tools.UpdateCardAsync(authKey, c1, laneId: lane.Id, index: 2);

        // Assert — c1 should be last, positions contiguous: 0, 10, 20
        var cards = await db.Cards.Where(c => c.LaneId == lane.Id)
            .OrderBy(c => c.Position).ToListAsync();
        cards.Count.ShouldBe(3);
        cards[0].Position.ShouldBe(0);
        cards[1].Position.ShouldBe(10);
        cards[2].Position.ShouldBe(20);
        cards[2].Id.ShouldBe(c1);
    }

    [Fact]
    public async Task MoveCard_EmptyTargetLane_CardGetsPositionZero()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var (lane1, _) = await GetTwoLaneIdsAsync(db);

        // Create a fresh empty lane to guarantee no pre-existing cards
        var emptyLane = new Lane { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = "Empty Lane", Position = 999 };
        db.Lanes.Add(emptyLane);
        await db.SaveChangesAsync();

        var card = await CreateCardInLaneAsync(tools, authKey, lane1, "Empty Target");

        // Act
        await tools.MoveCardAsync(authKey, emptyLane.Id, cardId: card);

        // Assert
        var movedCard = await db.Cards.FindAsync(card);
        movedCard!.LaneId.ShouldBe(emptyLane.Id);
        movedCard.Position.ShouldBe(0);
    }

    // ── Combined operations ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateCard_MoveAndRenameAndLabel_AllApplied()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var (lane1, lane2) = await GetTwoLaneIdsAsync(db);
        var cardId = await CreateCardInLaneAsync(tools, authKey, lane1, "Original");

        var label = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = $"Combo-{Guid.NewGuid()}", Color = "green" };
        db.Labels.Add(label);
        await db.SaveChangesAsync();

        // Act
        var result = await tools.UpdateCardAsync(authKey, cardId, name: "Renamed", laneId: lane2, labelIds: label.Id.ToString());

        // Assert
        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("name").GetString().ShouldBe("Renamed");
        json.RootElement.GetProperty("laneId").GetGuid().ShouldBe(lane2);

        var cardLabels = await db.CardLabels.Where(cl => cl.CardId == cardId).Select(cl => cl.LabelId).ToListAsync();
        cardLabels.ShouldContain(label.Id);
    }

    // ── get_cards pagination ─────────────────────────────────────────────

    [Fact]
    public async Task GetCards_DefaultBoard_ReturnsPagedEnvelope()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        await CreateCardInLaneAsync(tools, authKey, lanes[0], $"PagedEnvelope-{Guid.NewGuid()}");

        // Act
        var result = await tools.GetCardsAsync(authKey, _factory.DefaultBoardId);

        // Assert — response is a paged envelope, not a flat array
        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        var root = json.RootElement;
        root.TryGetProperty("items", out _).ShouldBeTrue();
        root.TryGetProperty("totalCount", out _).ShouldBeTrue();
        root.TryGetProperty("offset", out _).ShouldBeTrue();
        root.TryGetProperty("limit", out _).ShouldBeTrue();
        root.GetProperty("items").GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);
        root.GetProperty("totalCount").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        root.GetProperty("offset").GetInt32().ShouldBe(0);
        root.GetProperty("limit").GetInt32().ShouldBe(200);
    }

    [Fact]
    public async Task GetCards_WithLimit_ReturnsLimitedItems()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        for (var i = 0; i < 3; i++)
        {
            await CreateCardInLaneAsync(tools, authKey, lanes[0], $"LimitTest-{Guid.NewGuid()}");
        }

        // Act
        var result = await tools.GetCardsAsync(authKey, _factory.DefaultBoardId, limit: 2);

        // Assert
        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        var root = json.RootElement;
        root.GetProperty("items").GetArrayLength().ShouldBe(2);
        root.GetProperty("totalCount").GetInt32().ShouldBeGreaterThanOrEqualTo(3);
        root.GetProperty("limit").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task GetCards_WithOffset_SkipsCards()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        for (var i = 0; i < 3; i++)
        {
            await CreateCardInLaneAsync(tools, authKey, lanes[0], $"OffsetTest-{Guid.NewGuid()}");
        }

        // Act — get all, then get with offset
        var allResult = await tools.GetCardsAsync(authKey, _factory.DefaultBoardId, limit: 500);
        var offsetResult = await tools.GetCardsAsync(authKey, _factory.DefaultBoardId, offset: 2, limit: 500);

        // Assert
        var allJson = JsonDocument.Parse(allResult);
        var offsetJson = JsonDocument.Parse(offsetResult);
        var allCount = allJson.RootElement.GetProperty("items").GetArrayLength();
        var offsetCount = offsetJson.RootElement.GetProperty("items").GetArrayLength();
        offsetCount.ShouldBe(allCount - 2);
        offsetJson.RootElement.GetProperty("totalCount").GetInt32().ShouldBe(allJson.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task GetCards_LimitExceedsMax_ClampedTo500()
    {
        // Arrange
        var (_, tools, authKey) = CreateTools();

        // Act
        var result = await tools.GetCardsAsync(authKey, _factory.DefaultBoardId, limit: 9999);

        // Assert
        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("limit").GetInt32().ShouldBe(500);
    }

    [Fact]
    public async Task GetCards_NegativeOffset_ClampedToZero()
    {
        // Arrange
        var (_, tools, authKey) = CreateTools();

        // Act
        var result = await tools.GetCardsAsync(authKey, _factory.DefaultBoardId, offset: -10);

        // Assert
        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("offset").GetInt32().ShouldBe(0);
    }

    // ── get_cards since filter (regression) ─────────────────────────
    //
    // Pre-fix, the `since` filter composed top-level db.Comments/db.Attachments
    // projections via .Contains(x.Id) inside an ||, which the SQLite provider
    // could not translate — get_cards threw InvalidOperationException at the
    // first query execution (CountAsync), so any valid ISO 8601 `since` value
    // failed the whole call. These tests run against the in-memory SQLite
    // provider so a translation regression fails here, not just in production.

    [Fact]
    public async Task GetCards_WithSinceFilter_DoesNotThrowAndReturnsPagedEnvelope()
    {
        // Arrange
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        await CreateCardInLaneAsync(tools, authKey, lanes[0], $"SinceSmoke-{Guid.NewGuid()}");
        var since = DateTimeOffset.UtcNow.AddDays(-7);

        // Act
        var result = await tools.GetCardsAsync(authKey, _factory.DefaultBoardId, since: since);

        // Assert — the previously-untranslatable query now executes server-side
        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        var root = json.RootElement;
        root.TryGetProperty("items", out _).ShouldBeTrue();
        root.TryGetProperty("totalCount", out _).ShouldBeTrue();
        root.GetProperty("items").GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetCards_WithSinceFilter_IncludesCardWithRecentCommentOnly()
    {
        // Arrange — card itself is old; only a recent comment makes it match.
        // This isolates the previously-untranslatable comment-activity sub-query.
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var cardId = await CreateCardInLaneAsync(tools, authKey, lanes[0], $"SinceComment-{Guid.NewGuid()}");

        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var card = await db.Cards.SingleAsync(c => c.Id == cardId);
        card.CreatedAtUtc = since.AddDays(-30);
        card.LastUpdatedAtUtc = since.AddDays(-30);
        db.Comments.Add(new CardComment
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            UserId = card.CreatedByUserId,
            ContentMarkdown = "recent comment",
            LastUpdatedAtUtc = since.AddDays(1),
        });
        await db.SaveChangesAsync();

        // Act
        var result = await tools.GetCardsAsync(authKey, _factory.DefaultBoardId, since: since);

        // Assert
        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        var ids = json.RootElement.GetProperty("items")
            .EnumerateArray()
                .Select(i => i.GetProperty("id").GetGuid())
                    .ToList();
        ids.ShouldContain(cardId);
    }

    [Fact]
    public async Task GetCards_WithSinceFilter_IncludesCardWithRecentAttachmentOnly()
    {
        // Arrange — card itself is old; only a recent attachment makes it match.
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var cardId = await CreateCardInLaneAsync(tools, authKey, lanes[0], $"SinceAttach-{Guid.NewGuid()}");

        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var card = await db.Cards.SingleAsync(c => c.Id == cardId);
        card.CreatedAtUtc = since.AddDays(-30);
        card.LastUpdatedAtUtc = since.AddDays(-30);
        db.Attachments.Add(new CardAttachment
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            FileName = "recent.txt",
            ContentType = "text/plain",
            Payload = [1, 2, 3],
            AddedByUserId = card.CreatedByUserId,
            AddedAtUtc = since.AddDays(1),
        });
        await db.SaveChangesAsync();

        // Act
        var result = await tools.GetCardsAsync(authKey, _factory.DefaultBoardId, since: since);

        // Assert
        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        var ids = json.RootElement.GetProperty("items")
            .EnumerateArray()
                .Select(i => i.GetProperty("id").GetGuid())
                    .ToList();
        ids.ShouldContain(cardId);
    }

    [Fact]
    public async Task GetCards_WithSinceFilter_ExcludesCardWithNoRecentActivity()
    {
        // Arrange — card and its comment are both older than `since`; it must
        // be filtered out server-side (proves the filter discriminates, not
        // a pass-through that "works" by returning everything).
        var (db, tools, authKey) = CreateTools();
        var lanes = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId).Select(l => l.Id).ToListAsync();
        var staleName = $"SinceStale-{Guid.NewGuid()}";
        var cardId = await CreateCardInLaneAsync(tools, authKey, lanes[0], staleName);

        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var card = await db.Cards.SingleAsync(c => c.Id == cardId);
        card.CreatedAtUtc = since.AddDays(-30);
        card.LastUpdatedAtUtc = since.AddDays(-30);
        db.Comments.Add(new CardComment
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            UserId = card.CreatedByUserId,
            ContentMarkdown = "stale comment",
            LastUpdatedAtUtc = since.AddDays(-15),
        });
        await db.SaveChangesAsync();

        // Act
        var result = await tools.GetCardsAsync(authKey, _factory.DefaultBoardId, since: since);

        // Assert
        result.ShouldNotContain("Error");
        var json = JsonDocument.Parse(result);
        var ids = json.RootElement.GetProperty("items")
            .EnumerateArray()
                .Select(i => i.GetProperty("id").GetGuid())
                    .ToList();
        ids.ShouldNotContain(cardId);
    }
}
