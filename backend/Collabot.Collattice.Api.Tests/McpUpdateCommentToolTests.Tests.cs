using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The MCP update_comment tool closes the gap where REST had
// PATCH /comments/{id} but MCP could only add and delete a comment. These tests
// exercise it by direct tool-class invocation: the canonical
// contentMarkdown body param (the deprecated `content` alias was removed),
// own-or-admin-level gating (mirrors delete_comment), empty-content
// rejection, the archive freeze, and the JSON return shape.
public class McpUpdateCommentToolTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly List<IServiceScope> _scopes = [];
    private static int _nextCardNumber = 9500;
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

    private async Task<BoardUser> CreateUserAsync(UserRole role = UserRole.HumanUser)
    {
        using var setupClient = _factory.CreateClient();
        return await TestAuthHelper.CreateUserAsync
        (
            setupClient,
            _factory,
            $"updater-{role}-{Guid.NewGuid():N}",
            role
        );
    }

    private async Task<Guid> CreateCardAsync(BoardDbContext db, BoardUser createdBy, bool archived = false)
    {
        var board = await db.Boards.FirstAsync();
        var lane = archived
            ? await db.Lanes.FirstAsync(l => l.BoardId == board.Id && l.IsArchiveLane)
            : await db.Lanes.FirstAsync(l => l.BoardId == board.Id && !l.IsArchiveLane);
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
            Name = $"Update Comment Test Card {Guid.NewGuid():N}",
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

    private async Task<Guid> CreateCommentAsync(BoardDbContext db, Guid cardId, BoardUser author)
    {
        var comment = new CardComment
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            UserId = author.Id,
            ContentMarkdown = "original body",
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync();
        return comment.Id;
    }

    [Fact]
    public async Task UpdateComment_AuthorEditsOwnComment_PersistsNewBody()
    {
        // Arrange
        var (db, commentTools) = CreateTools();
        var author = await CreateUserAsync();
        var cardId = await CreateCardAsync(db, author);
        var commentId = await CreateCommentAsync(db, cardId, author);

        // Act
        var result = await commentTools.UpdateCommentAsync(author.AuthKey, commentId, contentMarkdown: "edited body");

        // Assert
        result.ShouldNotStartWith("Error");
        var stored = await db.Comments.FindAsync(commentId);
        stored.ShouldNotBeNull();
        stored.ContentMarkdown.ShouldBe("edited body");
    }

    [Fact]
    public async Task UpdateComment_ReturnsCommentJsonWithUpdatedBody()
    {
        // Arrange
        var (db, commentTools) = CreateTools();
        var author = await CreateUserAsync();
        var cardId = await CreateCardAsync(db, author);
        var commentId = await CreateCommentAsync(db, cardId, author);

        // Act — assert against the JSON the caller actually sees
        var result = await commentTools.UpdateCommentAsync(author.AuthKey, commentId, contentMarkdown: "json shape body");

        // Assert
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("id").GetGuid().ShouldBe(commentId);
        doc.RootElement.GetProperty("contentMarkdown").GetString().ShouldBe("json shape body");
    }

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task UpdateComment_AdminLevelEditingOthersComment_Succeeds(UserRole editorRole)
    {
        // Arrange
        var (db, commentTools) = CreateTools();
        var author = await CreateUserAsync(UserRole.HumanUser);
        var editor = await CreateUserAsync(editorRole);
        var cardId = await CreateCardAsync(db, author);
        var commentId = await CreateCommentAsync(db, cardId, author);

        // Act
        var result = await commentTools.UpdateCommentAsync(editor.AuthKey, commentId, contentMarkdown: "admin edited");

        // Assert
        result.ShouldNotStartWith("Error");
        var stored = await db.Comments.FindAsync(commentId);
        stored.ShouldNotBeNull();
        stored.ContentMarkdown.ShouldBe("admin edited");
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task UpdateComment_NonAdminEditingOthersComment_ReturnsError(UserRole editorRole)
    {
        // Arrange
        var (db, commentTools) = CreateTools();
        var author = await CreateUserAsync(UserRole.HumanUser);
        var editor = await CreateUserAsync(editorRole);
        var cardId = await CreateCardAsync(db, author);
        var commentId = await CreateCommentAsync(db, cardId, author);

        // Act
        var result = await commentTools.UpdateCommentAsync(editor.AuthKey, commentId, contentMarkdown: "should not stick");

        // Assert
        result.ShouldBe("Error: You can only edit your own comments.");
        var stored = await db.Comments.FindAsync(commentId);
        stored.ShouldNotBeNull();
        stored.ContentMarkdown.ShouldBe("original body");
    }

    [Fact]
    public async Task UpdateComment_EmptyContent_ReturnsError()
    {
        // Arrange
        var (db, commentTools) = CreateTools();
        var author = await CreateUserAsync();
        var cardId = await CreateCardAsync(db, author);
        var commentId = await CreateCommentAsync(db, cardId, author);

        // Act
        var result = await commentTools.UpdateCommentAsync(author.AuthKey, commentId, contentMarkdown: "   ");

        // Assert
        result.ShouldStartWith("Error");
        result.ShouldContain("contentMarkdown");
        var stored = await db.Comments.FindAsync(commentId);
        stored.ShouldNotBeNull();
        stored.ContentMarkdown.ShouldBe("original body");
    }

    [Fact]
    public async Task UpdateComment_CommentNotFound_ReturnsError()
    {
        // Arrange
        var (_, commentTools) = CreateTools();
        var author = await CreateUserAsync();

        // Act
        var result = await commentTools.UpdateCommentAsync(author.AuthKey, Guid.NewGuid(), contentMarkdown: "x");

        // Assert
        result.ShouldBe("Error: Comment not found.");
    }

    [Fact]
    public async Task UpdateComment_OnArchivedCard_ReturnsError()
    {
        // Arrange
        var (db, commentTools) = CreateTools();
        var author = await CreateUserAsync();
        var cardId = await CreateCardAsync(db, author, archived: true);
        var commentId = await CreateCommentAsync(db, cardId, author);

        // Act
        var result = await commentTools.UpdateCommentAsync(author.AuthKey, commentId, contentMarkdown: "frozen edit");

        // Assert
        result.ShouldBe("Archived cards cannot be modified.");
        var stored = await db.Comments.FindAsync(commentId);
        stored.ShouldNotBeNull();
        stored.ContentMarkdown.ShouldBe("original body");
    }

    [Fact]
    public async Task UpdateComment_InvalidAuthKey_ReturnsError()
    {
        // Arrange
        var (db, commentTools) = CreateTools();
        var author = await CreateUserAsync();
        var cardId = await CreateCardAsync(db, author);
        var commentId = await CreateCommentAsync(db, cardId, author);

        // Act
        var result = await commentTools.UpdateCommentAsync("not-a-real-key", commentId, contentMarkdown: "x");

        // Assert
        result.ShouldStartWith("Error");
        var stored = await db.Comments.FindAsync(commentId);
        stored.ShouldNotBeNull();
        stored.ContentMarkdown.ShouldBe("original body");
    }

    [Fact]
    public async Task UpdateComment_PreservesCreatedAtUtc_WhileBumpingLastUpdated()
    {
        // Arrange — create through the real add path so CreatedAtUtc is stamped by production code
        var (db, commentTools) = CreateTools();
        var author = await CreateUserAsync();
        var cardId = await CreateCardAsync(db, author);

        var addResult = await commentTools.AddCommentAsync(author.AuthKey, "before edit", cardId: cardId);
        using var addDoc = JsonDocument.Parse(addResult);
        var commentId = addDoc.RootElement.GetProperty("id").GetGuid();
        var createdAtUtc = addDoc.RootElement.GetProperty("createdAtUtc").GetDateTimeOffset();

        // Act — an edit must not rewrite the creation time
        var editResult = await commentTools.UpdateCommentAsync(author.AuthKey, commentId, contentMarkdown: "after edit");

        // Assert — assert against the JSON the caller actually sees
        editResult.ShouldNotStartWith("Error");
        using var editDoc = JsonDocument.Parse(editResult);
        editDoc.RootElement.GetProperty("createdAtUtc").GetDateTimeOffset().ShouldBe(createdAtUtc);
        editDoc.RootElement.GetProperty("lastUpdatedAtUtc").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(createdAtUtc);
    }
}
