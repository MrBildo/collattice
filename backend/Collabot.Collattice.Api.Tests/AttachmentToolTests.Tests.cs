using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class AttachmentToolTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private static int _nextNumber = 5000;

    private async Task<(AttachmentTools Tools, Guid CardId, string AuthKey)> CreateToolWithCardAsync()
    {
        var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();

        var admin = await db.Users.FirstAsync(u => u.Role == UserRole.Administrator);
        var board = await db.Boards.FirstAsync();
        var lane = await db.Lanes.FirstAsync(l => l.BoardId == board.Id);
        var defaultSize = await db.CardSizes
            .Where(s => s.BoardId == board.Id)
            .OrderBy(s => s.Ordinal)
                .FirstAsync();

        var card = new CardItem
        {
            Id = Guid.NewGuid(),
            BoardId = board.Id,
            LaneId = lane.Id,
            SizeId = defaultSize.Id,
            Name = "MCP Upload Test Card",
            Number = Interlocked.Increment(ref _nextNumber),
            Position = 9000,
            CreatedByUserId = admin.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastUpdatedByUserId = admin.Id,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Cards.Add(card);
        await db.SaveChangesAsync();

        var settings = Options.Create(new AttachmentSettings());
        var tools = new AttachmentTools(db, auth, broadcaster, settings);
        return (tools, card.Id, admin.AuthKey);
    }

    [Fact]
    public async Task UploadAttachment_ExceedsFiveMbLimit_ReturnsError()
    {
        // Arrange
        var (tools, cardId, authKey) = await CreateToolWithCardAsync();
        var oversizedPayload = new byte[(5 * 1024 * 1024) + 1];
        var base64Content = Convert.ToBase64String(oversizedPayload);

        // Act
        var result = await tools.UploadAttachmentAsync
        (
            authKey,
            "big-file.bin",
            base64Content,
            cardId: cardId
        );

        // Assert
        result.ShouldContain("File exceeds 5MB limit");
        result.ShouldContain("REST endpoint");
    }

    [Fact]
    public async Task UploadAttachment_SixMb_OverMcpCapUnderRestCap_ReturnsError()
    {
        // Arrange — 6MB is the size the REST path accepts (see
        // AttachmentEndpointTests.PostAttachment_SixMb_OverMcpCapUnderRestCap_Succeeds);
        // MCP must still reject it and point the caller at REST, per the 5MB-MCP /
        // 50MB-REST split.
        var (tools, cardId, authKey) = await CreateToolWithCardAsync();
        var sixMb = new byte[6 * 1024 * 1024];
        var base64Content = Convert.ToBase64String(sixMb);

        // Act
        var result = await tools.UploadAttachmentAsync
        (
            authKey,
            "six-mb.bin",
            base64Content,
            cardId: cardId
        );

        // Assert
        result.ShouldContain("File exceeds 5MB limit");
        result.ShouldContain("up to 50MB");
    }

    [Fact]
    public async Task UploadAttachment_ExactlyFiveMb_Succeeds()
    {
        // Arrange
        var (tools, cardId, authKey) = await CreateToolWithCardAsync();
        var exactPayload = new byte[5 * 1024 * 1024];
        var base64Content = Convert.ToBase64String(exactPayload);

        // Act
        var result = await tools.UploadAttachmentAsync
        (
            authKey,
            "exact-5mb.bin",
            base64Content,
            cardId: cardId
        );

        // Assert
        result.ShouldNotContain("Error");
        result.ShouldContain("exact-5mb.bin");
    }
}
