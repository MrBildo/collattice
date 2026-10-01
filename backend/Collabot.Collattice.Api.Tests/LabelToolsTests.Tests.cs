using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Sealed so the plain IDisposable shape below is complete; no derived class can add state to dispose.
public sealed class LabelToolsTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly List<IServiceScope> _scopes = [];

    private (BoardDbContext Db, LabelTools Tools) CreateTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = new McpAuthService(new UserResolver(db));
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        return (db, new LabelTools(db, auth, broadcaster));
    }

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }
    }

    private async Task<(Guid BoardId, Guid LaneId)> GetDefaultBoardAndLaneAsync(BoardDbContext db)
    {
        var board = await db.Boards.FirstAsync();
        var lane = await db.Lanes.FirstAsync(l => l.BoardId == board.Id);
        return (board.Id, lane.Id);
    }

    private async Task<Guid> CreateCardAsync(BoardDbContext db, Guid laneId)
    {
        var admin = await db.Users.FirstAsync(u => u.Role == UserRole.Administrator);
        var lane = await db.Lanes.FindAsync(laneId);
        var boardId = lane!.BoardId;
        var defaultSize = await db.CardSizes
            .Where(s => s.BoardId == boardId)
            .OrderBy(s => s.Ordinal)
                .FirstAsync();
        var nextNumber = (await db.Cards.Where(c => c.BoardId == boardId).MaxAsync(c => (long?)c.Number) ?? 0) + 1;
        var card = new CardItem
        {
            Id = Guid.NewGuid(),
            Name = $"Test Card {Guid.NewGuid()}",
            Number = nextNumber,
            BoardId = boardId,
            SizeId = defaultSize.Id,
            LaneId = laneId,
            Position = Random.Shared.Next(10000, 99999),
            CreatedByUserId = admin.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastUpdatedByUserId = admin.Id,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Cards.Add(card);
        await db.SaveChangesAsync();
        return card.Id;
    }

    private async Task<(Guid Id, string Name)> CreateLabelAsync(BoardDbContext db, Guid boardId, string? name = null)
    {
        name ??= $"Label-{Guid.NewGuid()}";
        var label = new Label
        {
            Id = Guid.NewGuid(),
            BoardId = boardId,
            Name = name,
            Color = "blue",
        };
        db.Labels.Add(label);
        await db.SaveChangesAsync();
        return (label.Id, name);
    }

    [Fact]
    public async Task AddLabelToCard_ByName_Succeeds()
    {
        // Arrange
        var (db, tools) = CreateTools();
        var (boardId, laneId) = await GetDefaultBoardAndLaneAsync(db);
        var cardId = await CreateCardAsync(db, laneId);
        var (_, labelName) = await CreateLabelAsync(db, boardId, $"ByName-{Guid.NewGuid()}");
        var authKey = (await db.Users.FirstAsync(u => u.Role == UserRole.Administrator)).AuthKey;

        // Act
        var result = await tools.AddLabelToCardAsync(authKey, cardId, labelName: labelName);

        // Assert
        result.ShouldBe("Label added successfully.");
    }

    [Fact]
    public async Task AddLabelToCard_ByName_CaseInsensitive_Succeeds()
    {
        // Arrange
        var (db, tools) = CreateTools();
        var (boardId, laneId) = await GetDefaultBoardAndLaneAsync(db);
        var cardId = await CreateCardAsync(db, laneId);
        var (_, labelName) = await CreateLabelAsync(db, boardId, $"MixedCase-{Guid.NewGuid()}");
        var authKey = (await db.Users.FirstAsync(u => u.Role == UserRole.Administrator)).AuthKey;

        // Act
        var result = await tools.AddLabelToCardAsync(authKey, cardId, labelName: labelName.ToUpperInvariant());

        // Assert
        result.ShouldBe("Label added successfully.");
    }

    [Fact]
    public async Task AddLabelToCard_ByName_NotFound_ReturnsAvailableLabels()
    {
        // Arrange
        var (db, tools) = CreateTools();
        var (boardId, laneId) = await GetDefaultBoardAndLaneAsync(db);
        var cardId = await CreateCardAsync(db, laneId);
        var (_, existingName) = await CreateLabelAsync(db, boardId, $"Existing-{Guid.NewGuid()}");
        var authKey = (await db.Users.FirstAsync(u => u.Role == UserRole.Administrator)).AuthKey;

        // Act
        var result = await tools.AddLabelToCardAsync(authKey, cardId, labelName: "NonexistentLabel");

        // Assert
        result.ShouldStartWith("Error: No label named 'NonexistentLabel' found on this board.");
        result.ShouldContain("Available labels:");
        result.ShouldContain(existingName);
    }

    [Fact]
    public async Task AddLabelToCard_NeitherIdNorName_ReturnsError()
    {
        // Arrange
        var (db, tools) = CreateTools();
        var (_, laneId) = await GetDefaultBoardAndLaneAsync(db);
        var cardId = await CreateCardAsync(db, laneId);
        var authKey = (await db.Users.FirstAsync(u => u.Role == UserRole.Administrator)).AuthKey;

        // Act
        var result = await tools.AddLabelToCardAsync(authKey, cardId);

        // Assert
        result.ShouldBe("Error: Provide either labelId or labelName.");
    }

    [Fact]
    public async Task AddLabelToCard_BothIdAndName_ReturnsError()
    {
        // Arrange
        var (db, tools) = CreateTools();
        var (boardId, laneId) = await GetDefaultBoardAndLaneAsync(db);
        var cardId = await CreateCardAsync(db, laneId);
        var (labelId, labelName) = await CreateLabelAsync(db, boardId);
        var authKey = (await db.Users.FirstAsync(u => u.Role == UserRole.Administrator)).AuthKey;

        // Act
        var result = await tools.AddLabelToCardAsync(authKey, cardId, labelId: labelId, labelName: labelName);

        // Assert
        result.ShouldBe("Error: Provide either labelId or labelName, not both.");
    }

    [Fact]
    public async Task RemoveLabelFromCard_ByName_Succeeds()
    {
        // Arrange
        var (db, tools) = CreateTools();
        var (boardId, laneId) = await GetDefaultBoardAndLaneAsync(db);
        var cardId = await CreateCardAsync(db, laneId);
        var (labelId, labelName) = await CreateLabelAsync(db, boardId, $"RemoveByName-{Guid.NewGuid()}");
        var authKey = (await db.Users.FirstAsync(u => u.Role == UserRole.Administrator)).AuthKey;

        // Add the label first
        db.CardLabels.Add(new CardLabel { CardId = cardId, LabelId = labelId });
        await db.SaveChangesAsync();

        // Act
        var result = await tools.RemoveLabelFromCardAsync(authKey, cardId, labelName: labelName);

        // Assert
        result.ShouldBe("Label removed successfully.");
    }

    [Fact]
    public async Task RemoveLabelFromCard_ByName_NotFound_ReturnsAvailableLabels()
    {
        // Arrange
        var (db, tools) = CreateTools();
        var (boardId, laneId) = await GetDefaultBoardAndLaneAsync(db);
        var cardId = await CreateCardAsync(db, laneId);
        await CreateLabelAsync(db, boardId, $"SomeLabel-{Guid.NewGuid()}");
        var authKey = (await db.Users.FirstAsync(u => u.Role == UserRole.Administrator)).AuthKey;

        // Act
        var result = await tools.RemoveLabelFromCardAsync(authKey, cardId, labelName: "GhostLabel");

        // Assert
        result.ShouldStartWith("Error: No label named 'GhostLabel' found on this board.");
        result.ShouldContain("Available labels:");
    }

    [Fact]
    public async Task RemoveLabelFromCard_NeitherIdNorName_ReturnsError()
    {
        // Arrange
        var (db, tools) = CreateTools();
        var (_, laneId) = await GetDefaultBoardAndLaneAsync(db);
        var cardId = await CreateCardAsync(db, laneId);
        var authKey = (await db.Users.FirstAsync(u => u.Role == UserRole.Administrator)).AuthKey;

        // Act
        var result = await tools.RemoveLabelFromCardAsync(authKey, cardId);

        // Assert
        result.ShouldBe("Error: Provide either labelId or labelName.");
    }

    [Fact]
    public async Task RemoveLabelFromCard_BothIdAndName_ReturnsError()
    {
        // Arrange
        var (db, tools) = CreateTools();
        var (boardId, laneId) = await GetDefaultBoardAndLaneAsync(db);
        var cardId = await CreateCardAsync(db, laneId);
        var (labelId, labelName) = await CreateLabelAsync(db, boardId);
        var authKey = (await db.Users.FirstAsync(u => u.Role == UserRole.Administrator)).AuthKey;

        // Act
        var result = await tools.RemoveLabelFromCardAsync(authKey, cardId, labelId: labelId, labelName: labelName);

        // Assert
        result.ShouldBe("Error: Provide either labelId or labelName, not both.");
    }
}
