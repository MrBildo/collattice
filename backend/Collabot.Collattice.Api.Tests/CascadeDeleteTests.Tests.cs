using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class CascadeDeleteTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private static int _nextPosition = 9000;

    private static int NextPosition() => Interlocked.Increment(ref _nextPosition);

    private async Task<Guid> GetFirstLaneIdAsync()
    {
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/board");
        response.EnsureSuccessStatusCode();
        var board = await response.Content.ReadFromJsonAsync<JsonElement>();
        return board.GetProperty("lanes")[0].GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateCardAsync(Guid laneId)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = $"Cascade Test Card {Guid.NewGuid()}",
            descriptionMarkdown = "",
            laneId,
            position = NextPosition()
        });
        response.EnsureSuccessStatusCode();
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        return card.GetProperty("id").GetGuid();
    }

    private async Task<Guid> AddCommentAsync(Guid cardId)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new
        {
            contentMarkdown = "Test comment for cascade"
        });
        response.EnsureSuccessStatusCode();
        var comment = await response.Content.ReadFromJsonAsync<JsonElement>();
        return comment.GetProperty("id").GetGuid();
    }

    private async Task<Guid> AddAttachmentAsync(Guid cardId)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([1, 2, 3, 4]);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "cascade-test.bin");
        var response = await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", content);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateLabelAsync()
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels", new
        {
            name = $"CascadeLabel-{Guid.NewGuid()}",
            color = "red"
        });
        response.EnsureSuccessStatusCode();
        var label = await response.Content.ReadFromJsonAsync<JsonElement>();
        return label.GetProperty("id").GetGuid();
    }

    private async Task AddLabelToCardAsync(Guid cardId, Guid labelId)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task DeleteCard_WithChildren_CascadesToCommentsLabelsAttachments()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId);

        var commentId = await AddCommentAsync(cardId);
        var attachmentId = await AddAttachmentAsync(cardId);
        var labelId = await CreateLabelAsync();
        await AddLabelToCardAsync(cardId, labelId);

        // Verify children exist
        var commentsResponse = await _client.GetAsync($"/api/v1/cards/{cardId}/comments");
        commentsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var comments = await commentsResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        comments.ShouldNotBeNull();
        comments.Length.ShouldBeGreaterThanOrEqualTo(1);

        var attachmentsResponse = await _client.GetAsync($"/api/v1/cards/{cardId}/attachments");
        attachmentsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var attachments = await attachmentsResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        attachments.ShouldNotBeNull();
        attachments.Length.ShouldBeGreaterThanOrEqualTo(1);

        var labelsResponse = await _client.GetAsync($"/api/v1/cards/{cardId}/labels");
        labelsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var labels = await labelsResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        labels.ShouldNotBeNull();
        labels.Length.ShouldBeGreaterThanOrEqualTo(1);

        // Act
        var deleteResponse = await _client.DeleteAsync($"/api/v1/cards/{cardId}");

        // Assert
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Children should be gone (card endpoints return 404 for non-existent card)
        var commentsAfter = await _client.GetAsync($"/api/v1/cards/{cardId}/comments");
        commentsAfter.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var attachmentsAfter = await _client.GetAsync($"/api/v1/cards/{cardId}/attachments");
        attachmentsAfter.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Verify comment is gone by trying to patch it
        var patchComment = await _client.PatchAsJsonAsync($"/api/v1/comments/{commentId}", new { contentMarkdown = "updated" });
        patchComment.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Verify attachment is gone
        var getAttachment = await _client.GetAsync($"/api/v1/attachments/{attachmentId}");
        getAttachment.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteLane_WithCardAndChildren_CascadesToCardsAndTheirChildren()
    {
        // Arrange — create a new lane with a card that has comments and attachments
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var laneResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", new
        {
            name = $"CascadeLane-{Guid.NewGuid()}",
            position = NextPosition()
        });
        laneResponse.EnsureSuccessStatusCode();
        var lane = await laneResponse.Content.ReadFromJsonAsync<JsonElement>();
        var laneId = lane.GetProperty("id").GetGuid();

        var cardId = await CreateCardAsync(laneId);
        var commentId = await AddCommentAsync(cardId);
        await AddAttachmentAsync(cardId);

        // Act — delete the lane (which should cascade to cards, comments, attachments)
        // The lane endpoint currently checks for cards and returns 409.
        // With FK cascade in place, we test that directly via the DbContext.
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var laneEntity = await db.Lanes.FindAsync(laneId);
        laneEntity.ShouldNotBeNull();

        db.Lanes.Remove(laneEntity);
        await db.SaveChangesAsync();

        // Assert — card and its children should be gone
        var cardEntity = await db.Cards.FindAsync(cardId);
        cardEntity.ShouldBeNull();

        var commentEntity = await db.Comments.FindAsync(commentId);
        commentEntity.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteSize_InUseByCard_IsBlocked()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        // Create a custom size
        var sizeResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new
        {
            name = $"RestrictSize-{Guid.NewGuid()}",
            ordinal = NextPosition()
        });
        sizeResponse.EnsureSuccessStatusCode();
        var size = await sizeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sizeId = size.GetProperty("id").GetGuid();

        // Create a card using that size
        var cardResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = $"Restrict Test Card {Guid.NewGuid()}",
            laneId,
            sizeId,
            position = NextPosition()
        });
        cardResponse.EnsureSuccessStatusCode();

        // Act — try to delete size via API (should be blocked by endpoint validation)
        var deleteResponse = await _client.DeleteAsync($"/api/v1/sizes/{sizeId}");

        // Assert
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DeleteUser_WhoCreatedCards_IsBlockedByForeignKey()
    {
        // Arrange — create a user, have them create a card, then try to deactivate
        // (Note: there's no user delete endpoint, but we verify the FK constraint
        //  prevents deletion at the database level)
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var user = await TestAuthHelper.CreateUserAsync(_client, _factory, $"FKUser-{Guid.NewGuid()}", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, user.AuthKey);

        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId);

        // Act — try to delete the user directly via DbContext (should throw due to FK Restrict)
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var userEntity = await db.Users.FindAsync(user.Id);
        userEntity.ShouldNotBeNull();

        db.Users.Remove(userEntity);
        var ex = await Should.ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(async () =>
        {
            await db.SaveChangesAsync();
        });

        ex.ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteLabel_AssignedToCard_CascadesToCardLabelAssignments()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId);

        var labelId = await CreateLabelAsync();
        await AddLabelToCardAsync(cardId, labelId);

        // Verify the label is assigned
        var labelsBefore = await _client.GetAsync($"/api/v1/cards/{cardId}/labels");
        labelsBefore.StatusCode.ShouldBe(HttpStatusCode.OK);
        var labelsBeforeArr = await labelsBefore.Content.ReadFromJsonAsync<JsonElement[]>();
        labelsBeforeArr!.Any(l => l.GetProperty("id").GetGuid() == labelId).ShouldBeTrue();

        // Act — delete the label
        var deleteResponse = await _client.DeleteAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels/{labelId}");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Assert — the card should no longer have that label
        var labelsAfter = await _client.GetAsync($"/api/v1/cards/{cardId}/labels");
        labelsAfter.StatusCode.ShouldBe(HttpStatusCode.OK);
        var labelsAfterArr = await labelsAfter.Content.ReadFromJsonAsync<JsonElement[]>();
        labelsAfterArr!.Any(l => l.GetProperty("id").GetGuid() == labelId).ShouldBeFalse();
    }
}
