using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class McpArchiveToolTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly List<IServiceScope> _scopes = [];

    private (BoardDbContext Db, ArchiveTools ArchiveTools, CardTools CardTools, BoardTools BoardTools,
        CommentTools CommentTools, LabelTools LabelTools, AttachmentTools AttachmentTools, string AuthKey) CreateAllTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = new McpAuthService(new UserResolver(db));
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var settings = Options.Create(new AttachmentSettings());

        var archiveTools = new ArchiveTools(db, auth, broadcaster);
        var cardTools = new CardTools(db, auth, broadcaster);
        var boardTools = new BoardTools(db, auth, scope.ServiceProvider.GetRequiredService<IWebhookSink>());
        var commentTools = new CommentTools(db, auth, broadcaster);
        var labelTools = new LabelTools(db, auth, broadcaster);
        var attachmentTools = new AttachmentTools(db, auth, broadcaster, settings);

        return (db, archiveTools, cardTools, boardTools, commentTools, labelTools, attachmentTools,
            CollatticeApiFactory.TestAdminAuthKey);
    }

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }
    }

    private async Task<Guid> GetFirstLaneIdAsync(BoardDbContext db) =>
        await db.Lanes
            .Where(l => l.BoardId == _factory.DefaultBoardId && !l.IsArchiveLane)
            .OrderBy(l => l.Position)
                .Select(l => l.Id)
                    .FirstAsync();

    private async Task<Guid> GetArchiveLaneIdAsync(BoardDbContext db) =>
        await db.Lanes
            .Where(l => l.BoardId == _factory.DefaultBoardId && l.IsArchiveLane)
                .Select(l => l.Id)
                    .FirstAsync();

    private async Task<Guid> CreateCardInLaneAsync(CardTools tools, string authKey, Guid laneId, string name = "Test Card")
    {
        var result = await tools.CreateCardAsync(authKey, name, laneId);
        result.ShouldNotContain("Error");
        result.ShouldNotContain("cannot");
        var json = System.Text.Json.JsonDocument.Parse(result);
        return json.RootElement.GetProperty("id").GetGuid();
    }

    // ── archive_card ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ArchiveCard_Works_CardDisappearsFromNormalListing()
    {
        // Arrange
        var (db, archiveTools, cardTools, _, _, _, _, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Archive Me");

        // Act
        var result = await archiveTools.ArchiveCardAsync(authKey, cardId: cardId);

        // Assert
        result.ShouldContain("archived");

        // Card should NOT appear in default get_cards listing
        var listing = await cardTools.GetCardsAsync(authKey, _factory.DefaultBoardId);
        listing.ShouldNotContain(cardId.ToString());

        // Card SHOULD appear with includeArchived
        var archivedListing = await cardTools.GetCardsAsync(authKey, _factory.DefaultBoardId, includeArchived: true);
        archivedListing.ShouldContain(cardId.ToString());
    }

    // ── restore_card ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RestoreCard_Works_CardReappearsInTargetLane()
    {
        // Arrange
        var (db, archiveTools, cardTools, _, _, _, _, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Restore Me");
        await archiveTools.ArchiveCardAsync(authKey, cardId: cardId);

        // Act
        var result = await archiveTools.RestoreCardAsync(authKey, laneId, cardId: cardId);

        // Assert
        result.ShouldContain("restored");

        var card = await db.Cards.FindAsync(cardId);
        card!.LaneId.ShouldBe(laneId);
    }

    // ── get_lanes excludes archive lane ──────────────────────────────────────

    [Fact]
    public async Task GetLanes_DefaultBoard_ExcludesArchiveLane()
    {
        // Arrange
        var (db, _, _, boardTools, _, _, _, authKey) = CreateAllTools();

        // Act
        var result = await boardTools.GetLanesAsync(authKey, _factory.DefaultBoardId);

        // Assert
        result.ShouldNotContain("Archive");
        var lanes = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(result)!;
        foreach (var lane in lanes)
        {
            var laneId = lane.GetProperty("id").GetGuid();
            var dbLane = await db.Lanes.FindAsync(laneId);
            dbLane!.IsArchiveLane.ShouldBeFalse();
        }
    }

    // ── move_card to archive lane → error ────────────────────────────────────

    [Fact]
    public async Task MoveCard_ToArchiveLane_ReturnsError()
    {
        // Arrange
        var (db, _, cardTools, _, _, _, _, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var archiveLaneId = await GetArchiveLaneIdAsync(db);
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Block Move To Archive");

        // Act
        var result = await cardTools.MoveCardAsync(authKey, archiveLaneId, cardId: cardId);

        // Assert
        result.ShouldContain("Use archive_card to archive cards.");
    }

    // ── update_card to archive lane → error (REST/MCP parity) ───────────

    [Fact]
    public async Task UpdateCard_ToArchiveLane_ReturnsErrorAndDoesNotMove()
    {
        // Arrange — update_card must reject an archive-lane target just like move_card
        // and REST PATCH /cards/{id} do; otherwise it's a back-door archive that also
        // emits a wrong card.moved webhook event.
        var (db, _, cardTools, _, _, _, _, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var archiveLaneId = await GetArchiveLaneIdAsync(db);
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Block Update To Archive");

        // Act
        var result = await cardTools.UpdateCardAsync(authKey, cardId, laneId: archiveLaneId);

        // Assert — rejected, and the card stayed put (no back-door archive)
        result.ShouldContain("Use archive_card to archive cards.");
        var card = await db.Cards.FindAsync(cardId);
        card!.LaneId.ShouldBe(laneId);
    }

    [Fact]
    public async Task UpdateCard_ToNonArchiveLane_StillMoves()
    {
        // Arrange — the guard rejects only archive-lane targets; a legitimate lane move
        // must still succeed.
        var (db, _, cardTools, _, _, _, _, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var targetLaneId = await db.Lanes
            .Where(l => l.BoardId == _factory.DefaultBoardId && !l.IsArchiveLane && l.Id != laneId)
            .OrderBy(l => l.Position)
                .Select(l => l.Id)
                    .FirstAsync();
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Legit Move");

        // Act
        var result = await cardTools.UpdateCardAsync(authKey, cardId, laneId: targetLaneId);

        // Assert
        result.ShouldNotContain("Error");
        result.ShouldNotContain("Use archive_card");
        var card = await db.Cards.FindAsync(cardId);
        card!.LaneId.ShouldBe(targetLaneId);
    }

    // ── move_card from archive lane → error ──────────────────────────────────

    [Fact]
    public async Task MoveCard_FromArchiveLane_ReturnsError()
    {
        // Arrange
        var (db, archiveTools, cardTools, _, _, _, _, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Block Move From Archive");
        await archiveTools.ArchiveCardAsync(authKey, cardId: cardId);

        // Act
        var result = await cardTools.MoveCardAsync(authKey, laneId, cardId: cardId);

        // Assert
        result.ShouldContain("Use restore_card to restore archived cards.");
    }

    // ── update_card on archived card → error ─────────────────────────────────

    [Fact]
    public async Task UpdateCard_OnArchivedCard_ReturnsError()
    {
        // Arrange
        var (db, archiveTools, cardTools, _, _, _, _, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Block Update");
        await archiveTools.ArchiveCardAsync(authKey, cardId: cardId);

        // Act
        var result = await cardTools.UpdateCardAsync(authKey, cardId, name: "New Name");

        // Assert
        result.ShouldContain("Archived cards cannot be edited. Restore the card first.");
    }

    // ── create_card in archive lane → error ──────────────────────────────────

    [Fact]
    public async Task CreateCard_InArchiveLane_ReturnsError()
    {
        // Arrange
        var (db, _, cardTools, _, _, _, _, authKey) = CreateAllTools();
        var archiveLaneId = await GetArchiveLaneIdAsync(db);

        // Act
        var result = await cardTools.CreateCardAsync(authKey, "Should Fail", archiveLaneId);

        // Assert
        result.ShouldContain("Cards cannot be created in the archive lane.");
    }

    // ── add_comment on archived card → error ─────────────────────────────────

    [Fact]
    public async Task AddComment_OnArchivedCard_ReturnsError()
    {
        // Arrange
        var (db, archiveTools, cardTools, _, commentTools, _, _, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Block Comment");
        await archiveTools.ArchiveCardAsync(authKey, cardId: cardId);

        // Act
        var result = await commentTools.AddCommentAsync(authKey, "Should fail", cardId: cardId);

        // Assert
        result.ShouldContain("Archived cards cannot be modified.");
    }

    // ── Agent role can archive but not delete cards ──────────────────────────

    [Fact]
    public async Task AgentRole_CanArchive_ButCannotDeleteCards()
    {
        // Arrange
        // MA0042: scope is owned by the class-level _scopes list and disposed
        // synchronously in Dispose() (IDisposable fixture), not locally — so the
        // async-scope suggestion does not apply. Matches the CreateAllTools() pattern.
#pragma warning disable MA0042
        var scope = _factory.Services.CreateScope();
#pragma warning restore MA0042
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();

        // Create an agent user
        var agentUser = new BoardUser
        {
            Id = Guid.NewGuid(),
            Name = "Test Agent",
            AuthKey = $"agent-key-{Guid.NewGuid()}",
            Role = UserRole.AgentUser,
            IsActive = true,
        };
        db.Users.Add(agentUser);
        await db.SaveChangesAsync();

        var auth = new McpAuthService(new UserResolver(db));
        var archiveTools = new ArchiveTools(db, auth, broadcaster);
        var cardTools = new CardTools(db, auth, broadcaster);

        var laneId = await GetFirstLaneIdAsync(db);
        var adminAuthKey = CollatticeApiFactory.TestAdminAuthKey;

        // Create card as admin
        var cardId = await CreateCardInLaneAsync(cardTools, adminAuthKey, laneId, "Agent Archive Test");

        // Act — agent archives the card
        var archiveResult = await archiveTools.ArchiveCardAsync(agentUser.AuthKey, cardId: cardId);

        // Assert — archive should succeed
        archiveResult.ShouldContain("archived");

        // Verify agent cannot delete via REST (AgentUser cannot delete cards — enforced at endpoint level)
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Key", agentUser.AuthKey);
        var deleteResponse = await client.DeleteAsync($"/api/v1/cards/{cardId}");
        deleteResponse.StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden);
    }

    // ── upload_attachment on archived card → error ────────────────────────────

    [Fact]
    public async Task UploadAttachment_OnArchivedCard_ReturnsError()
    {
        // Arrange
        var (db, archiveTools, cardTools, _, _, _, attachmentTools, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Block Upload");
        await archiveTools.ArchiveCardAsync(authKey, cardId: cardId);

        // Act
        var result = await attachmentTools.UploadAttachmentAsync
        (
            authKey,
            "test.txt",
            Convert.ToBase64String("test"u8.ToArray()),
            cardId: cardId
        );

        // Assert
        result.ShouldContain("Archived cards cannot be modified.");
    }

    // ── add_label_to_card on archived card → error ───────────────────────────

    [Fact]
    public async Task AddLabelToCard_OnArchivedCard_ReturnsError()
    {
        // Arrange
        var (db, archiveTools, cardTools, _, _, labelTools, _, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Block Label Add");
        await archiveTools.ArchiveCardAsync(authKey, cardId: cardId);

        var label = new Label
        {
            Id = Guid.NewGuid(),
            BoardId = _factory.DefaultBoardId,
            Name = $"ArchiveLabel-{Guid.NewGuid()}",
            Color = "red",
        };
        db.Labels.Add(label);
        await db.SaveChangesAsync();

        // Act
        var result = await labelTools.AddLabelToCardAsync(authKey, cardId, labelId: label.Id);

        // Assert
        result.ShouldContain("Archived cards cannot be modified.");
    }

    // ── remove_label_from_card on archived card → error ──────────────────────

    [Fact]
    public async Task RemoveLabelFromCard_OnArchivedCard_ReturnsError()
    {
        // Arrange
        var (db, archiveTools, cardTools, _, _, labelTools, _, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Block Label Remove");

        var label = new Label
        {
            Id = Guid.NewGuid(),
            BoardId = _factory.DefaultBoardId,
            Name = $"RemoveLabel-{Guid.NewGuid()}",
            Color = "blue",
        };
        db.Labels.Add(label);
        db.CardLabels.Add(new CardLabel { CardId = cardId, LabelId = label.Id });
        await db.SaveChangesAsync();

        await archiveTools.ArchiveCardAsync(authKey, cardId: cardId);

        // Act
        var result = await labelTools.RemoveLabelFromCardAsync(authKey, cardId, labelId: label.Id);

        // Assert
        result.ShouldContain("Archived cards cannot be modified.");
    }

    // ── delete_comment on archived card → error ──────────────────────────────

    [Fact]
    public async Task DeleteComment_OnArchivedCard_ReturnsError()
    {
        // Arrange
        var (db, archiveTools, cardTools, _, commentTools, _, _, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Block Comment Delete");

        // Add a comment before archiving
        var commentResult = await commentTools.AddCommentAsync(authKey, "A comment", cardId: cardId);
        commentResult.ShouldNotContain("Error");
        var commentJson = System.Text.Json.JsonDocument.Parse(commentResult);
        var commentId = commentJson.RootElement.GetProperty("id").GetGuid();

        await archiveTools.ArchiveCardAsync(authKey, cardId: cardId);

        // Act
        var result = await commentTools.DeleteCommentAsync(authKey, commentId);

        // Assert
        result.ShouldContain("Archived cards cannot be modified.");
    }

    // ── delete_attachment on archived card → error ────────────────────────────

    [Fact]
    public async Task DeleteAttachment_OnArchivedCard_ReturnsError()
    {
        // Arrange
        var (db, archiveTools, cardTools, _, _, _, attachmentTools, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Block Attachment Delete");

        // Upload an attachment before archiving
        var uploadResult = await attachmentTools.UploadAttachmentAsync
        (
            authKey,
            "file.txt",
            Convert.ToBase64String("data"u8.ToArray()),
            cardId: cardId
        );
        uploadResult.ShouldNotContain("Error");
        var uploadJson = System.Text.Json.JsonDocument.Parse(uploadResult);
        var attachmentId = uploadJson.RootElement.GetProperty("id").GetGuid();

        await archiveTools.ArchiveCardAsync(authKey, cardId: cardId);

        // Act
        var result = await attachmentTools.DeleteAttachmentAsync(authKey, attachmentId);

        // Assert
        result.ShouldContain("Archived cards cannot be modified.");
    }

    // ── archive already-archived card → idempotent error ─────────────────────

    [Fact]
    public async Task ArchiveCard_AlreadyArchived_ReturnsError()
    {
        // Arrange
        var (db, archiveTools, cardTools, _, _, _, _, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Double Archive");
        await archiveTools.ArchiveCardAsync(authKey, cardId: cardId);

        // Act
        var result = await archiveTools.ArchiveCardAsync(authKey, cardId: cardId);

        // Assert
        result.ShouldContain("already archived");
    }

    // ── restore non-archived card → error ────────────────────────────────────

    [Fact]
    public async Task RestoreCard_NotArchived_ReturnsError()
    {
        // Arrange
        var (db, archiveTools, cardTools, _, _, _, _, authKey) = CreateAllTools();
        var laneId = await GetFirstLaneIdAsync(db);
        var cardId = await CreateCardInLaneAsync(cardTools, authKey, laneId, "Not Archived");

        // Act
        var result = await archiveTools.RestoreCardAsync(authKey, laneId, cardId: cardId);

        // Assert
        result.ShouldContain("not archived");
    }
}
