using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// contentMarkdown is the canonical body param for add_comment.
// The deprecated `content` alias was removed — contentMarkdown is now the sole
// required param. These tests pin the canonical path and the empty-input guard.
public class McpCommentToolTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly List<IServiceScope> _scopes = [];
    private static int _nextCardNumber = 9000;
    private static int _nextCardPosition = 0;

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private (BoardDbContext Db, CommentTools CommentTools) CreateTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = new McpAuthService(new UserResolver(db));
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        return (db, new CommentTools(db, auth, broadcaster));
    }

    private async Task<BoardUser> CreateUserAsync()
    {
        using var setupClient = _factory.CreateClient();
        return await TestAuthHelper.CreateUserAsync
        (
            setupClient,
            _factory,
            $"commenter-{Guid.NewGuid():N}",
            UserRole.HumanUser
        );
    }

    private async Task<Guid> CreateCardAsync(BoardDbContext db, BoardUser createdBy)
    {
        var board = await db.Boards.FirstAsync();
        var lane = await db.Lanes.FirstAsync(l => l.BoardId == board.Id && !l.IsArchiveLane);
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
            Name = $"Comment Alias Test Card {Guid.NewGuid():N}",
            Number = Interlocked.Increment(ref _nextCardNumber),
            Position = Interlocked.Increment(ref _nextCardPosition),
            CreatedByUserId = createdBy.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastUpdatedByUserId = createdBy.Id,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Cards.Add(card);
        await db.SaveChangesAsync();
        return card.Id;
    }

    private static Guid ParseCommentId(string result)
    {
        result.ShouldNotStartWith("Error");
        using var doc = JsonDocument.Parse(result);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task AddComment_WithContentMarkdown_PersistsBody()
    {
        // Arrange
        var (db, commentTools) = CreateTools();
        var author = await CreateUserAsync();
        var cardId = await CreateCardAsync(db, author);

        // Act
        var result = await commentTools.AddCommentAsync(author.AuthKey, "Body via contentMarkdown", cardId: cardId);

        // Assert
        var commentId = ParseCommentId(result);
        var stored = await db.Comments.FindAsync(commentId);
        stored.ShouldNotBeNull();
        stored.ContentMarkdown.ShouldBe("Body via contentMarkdown");
    }

    [Fact]
    public async Task AddComment_WithWhitespaceContentMarkdown_ReturnsError()
    {
        // Arrange
        var (db, commentTools) = CreateTools();
        var author = await CreateUserAsync();
        var cardId = await CreateCardAsync(db, author);

        // Act
        var result = await commentTools.AddCommentAsync(author.AuthKey, "   ", cardId: cardId);

        // Assert
        result.ShouldStartWith("Error");
        result.ShouldContain("contentMarkdown");
    }

    [Fact]
    public async Task AddComment_ReturnsCreatedAtUtc_EqualToLastUpdatedAtUtc()
    {
        // Arrange
        var (db, commentTools) = CreateTools();
        var author = await CreateUserAsync();
        var cardId = await CreateCardAsync(db, author);

        // Act — assert against the JSON the caller actually sees, not the DB row
        var result = await commentTools.AddCommentAsync(author.AuthKey, "provenance body", cardId: cardId);

        // Assert — a fresh comment's creation time is present and equal to its last-touched time
        result.ShouldNotStartWith("Error");
        using var doc = JsonDocument.Parse(result);
        var createdAtUtc = doc.RootElement.GetProperty("createdAtUtc").GetDateTimeOffset();
        createdAtUtc.ShouldBe(doc.RootElement.GetProperty("lastUpdatedAtUtc").GetDateTimeOffset());
    }
}
