using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The two existing MCP own-or-admin role checks
// (delete_comment, delete_attachment) widen from "own-or-Administrator" to
// "own-or-Administrator-or-AgentAdministrator." This file exercises the role
// matrix at each site:
//
//   - Administrator deletes another user's content       → success
//   - AgentAdministrator deletes another user's content  → success (the new admit)
//   - HumanUser deletes own content                      → success (status quo)
//   - HumanUser deletes another user's content           → "own only" error
//   - AgentUser deletes own content                      → success (status quo)
//   - AgentUser deletes another user's content           → "own only" error
public class McpOwnOrAdminWideningTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly List<IServiceScope> _scopes = [];
    private static int _nextCardNumber = 7000;
    private static int _nextCardPosition = 0;

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private (BoardDbContext Db, CommentTools CommentTools, AttachmentTools AttachmentTools) CreateTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = new McpAuthService(new UserResolver(db));
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var attachmentSettings = Options.Create(new AttachmentSettings());
        return (db, new CommentTools(db, auth, broadcaster), new AttachmentTools(db, auth, broadcaster, attachmentSettings));
    }

    private async Task<BoardUser> CreateUserAsync(UserRole role, string nameHint)
    {
        // Mint via the REST endpoint so the auth-key shape (ULID) and admin
        // seed flow match production. The MCP tools under test only need
        // a valid (BoardUser.AuthKey, BoardUser.Role) pair.
        using var setupClient = _factory.CreateClient();
        return await TestAuthHelper.CreateUserAsync
        (
            setupClient,
            _factory,
            $"{nameHint}-{role}-{Guid.NewGuid():N}",
            role
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
            Name = $"Widening Test Card {Guid.NewGuid():N}",
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
            ContentMarkdown = $"Comment by {author.Name}",
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync();
        return comment.Id;
    }

    private async Task<Guid> CreateAttachmentAsync(BoardDbContext db, Guid cardId, BoardUser uploader)
    {
        var attachment = new CardAttachment
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            FileName = $"attachment-{Guid.NewGuid():N}.bin",
            ContentType = "application/octet-stream",
            Payload = [1, 2, 3, 4],
            AddedByUserId = uploader.Id,
            AddedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();
        return attachment.Id;
    }

    // delete_comment

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task DeleteComment_AdminLevelDeletingOthersComment_Succeeds(UserRole deleterRole)
    {
        // Arrange
        var (db, commentTools, _) = CreateTools();
        var author = await CreateUserAsync(UserRole.HumanUser, "comment-author");
        var deleter = await CreateUserAsync(deleterRole, "comment-deleter");
        var cardId = await CreateCardAsync(db, author);
        var commentId = await CreateCommentAsync(db, cardId, author);

        // Act
        var result = await commentTools.DeleteCommentAsync(deleter.AuthKey, commentId);

        // Assert
        result.ShouldBe("Comment deleted.");
        (await db.Comments.FindAsync(commentId)).ShouldBeNull();
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task DeleteComment_NonAdminDeletingOwnComment_Succeeds(UserRole authorRole)
    {
        // Arrange
        var (db, commentTools, _) = CreateTools();
        var author = await CreateUserAsync(authorRole, "comment-self");
        var cardId = await CreateCardAsync(db, author);
        var commentId = await CreateCommentAsync(db, cardId, author);

        // Act
        var result = await commentTools.DeleteCommentAsync(author.AuthKey, commentId);

        // Assert
        result.ShouldBe("Comment deleted.");
        (await db.Comments.FindAsync(commentId)).ShouldBeNull();
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task DeleteComment_NonAdminDeletingOthersComment_ReturnsError(UserRole deleterRole)
    {
        // Arrange
        var (db, commentTools, _) = CreateTools();
        var author = await CreateUserAsync(UserRole.HumanUser, "comment-author");
        var deleter = await CreateUserAsync(deleterRole, "comment-deleter");
        var cardId = await CreateCardAsync(db, author);
        var commentId = await CreateCommentAsync(db, cardId, author);

        // Act
        var result = await commentTools.DeleteCommentAsync(deleter.AuthKey, commentId);

        // Assert
        result.ShouldBe("Error: You can only delete your own comments.");
        (await db.Comments.FindAsync(commentId)).ShouldNotBeNull();
    }

    // delete_attachment

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task DeleteAttachment_AdminLevelDeletingOthersAttachment_Succeeds(UserRole deleterRole)
    {
        // Arrange
        var (db, _, attachmentTools) = CreateTools();
        var uploader = await CreateUserAsync(UserRole.HumanUser, "attach-uploader");
        var deleter = await CreateUserAsync(deleterRole, "attach-deleter");
        var cardId = await CreateCardAsync(db, uploader);
        var attachmentId = await CreateAttachmentAsync(db, cardId, uploader);

        // Act
        var result = await attachmentTools.DeleteAttachmentAsync(deleter.AuthKey, attachmentId);

        // Assert
        result.ShouldContain("deleted");
        result.ShouldNotStartWith("Error");
        (await db.Attachments.FindAsync(attachmentId)).ShouldBeNull();
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task DeleteAttachment_NonAdminDeletingOwnAttachment_Succeeds(UserRole uploaderRole)
    {
        // Arrange
        var (db, _, attachmentTools) = CreateTools();
        var uploader = await CreateUserAsync(uploaderRole, "attach-self");
        var cardId = await CreateCardAsync(db, uploader);
        var attachmentId = await CreateAttachmentAsync(db, cardId, uploader);

        // Act
        var result = await attachmentTools.DeleteAttachmentAsync(uploader.AuthKey, attachmentId);

        // Assert
        result.ShouldContain("deleted");
        result.ShouldNotStartWith("Error");
        (await db.Attachments.FindAsync(attachmentId)).ShouldBeNull();
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task DeleteAttachment_NonAdminDeletingOthersAttachment_ReturnsError(UserRole deleterRole)
    {
        // Arrange
        var (db, _, attachmentTools) = CreateTools();
        var uploader = await CreateUserAsync(UserRole.HumanUser, "attach-uploader");
        var deleter = await CreateUserAsync(deleterRole, "attach-deleter");
        var cardId = await CreateCardAsync(db, uploader);
        var attachmentId = await CreateAttachmentAsync(db, cardId, uploader);

        // Act
        var result = await attachmentTools.DeleteAttachmentAsync(deleter.AuthKey, attachmentId);

        // Assert
        result.ShouldBe("Error: You can only delete your own attachments.");
        (await db.Attachments.FindAsync(attachmentId)).ShouldNotBeNull();
    }
}
