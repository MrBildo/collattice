using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class ArchiveGuardTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private static JsonSerializerOptions JsonOptions => TestAuthHelper.JsonOptions;

    [Fact]
    public async Task PatchCard_OnArchivedCard_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Patch Archived Card");
        await ArchiveCardAsync(cardId);

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { name = "Updated" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Archived cards cannot be modified");
    }

    [Fact]
    public async Task ReorderArchivedCard_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Reorder Archived Card");
        await ArchiveCardAsync(cardId);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/reorder", new { laneId, index = 0 });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Archived cards cannot be modified");
    }

    [Fact]
    public async Task ReorderToArchiveLane_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Reorder To Archive Lane");
        var archiveLaneId = await GetArchiveLaneIdAsync();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/reorder", new { laneId = archiveLaneId, index = 0 });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Use archive_card to archive cards");
    }

    [Fact]
    public async Task PostComment_OnArchivedCard_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Comment Archived Card");
        await ArchiveCardAsync(cardId);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Test" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Archived cards cannot be modified");
    }

    [Fact]
    public async Task PatchComment_OnArchivedCard_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Patch Comment Archived");
        var commentId = await CreateCommentAsync(cardId, "Original");
        await ArchiveCardAsync(cardId);

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/comments/{commentId}", new { contentMarkdown = "Updated" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Archived cards cannot be modified");
    }

    [Fact]
    public async Task DeleteComment_OnArchivedCard_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Delete Comment Archived");
        var commentId = await CreateCommentAsync(cardId, "To delete");
        await ArchiveCardAsync(cardId);

        // Act
        var response = await _client.DeleteAsync($"/api/v1/comments/{commentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Archived cards cannot be modified");
    }

    [Fact]
    public async Task PostLabel_OnArchivedCard_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Label Archived Card");
        var labelId = await CreateLabelAsync("Guard Label " + Guid.NewGuid().ToString()[..8]);
        await ArchiveCardAsync(cardId);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Archived cards cannot be modified");
    }

    [Fact]
    public async Task DeleteLabel_OnArchivedCard_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Delete Label Archived");
        var labelId = await CreateLabelAsync("Guard Del Label " + Guid.NewGuid().ToString()[..8]);
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId });
        await ArchiveCardAsync(cardId);

        // Act
        var response = await _client.DeleteAsync($"/api/v1/cards/{cardId}/labels/{labelId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Archived cards cannot be modified");
    }

    [Fact]
    public async Task PostAttachment_OnArchivedCard_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Attachment Archived Card");
        await ArchiveCardAsync(cardId);

        var upload = CreateFileUpload();

        // Act
        var response = await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", upload);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Archived cards cannot be modified");
    }

    [Fact]
    public async Task DeleteAttachment_OnArchivedCard_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Delete Attachment Archived");
        var attachmentId = await UploadAttachmentAsync(cardId);
        await ArchiveCardAsync(cardId);

        // Act
        var response = await _client.DeleteAsync($"/api/v1/attachments/{attachmentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Archived cards cannot be modified");
    }

    [Fact]
    public async Task DownloadAttachment_OnArchivedCard_Returns200()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Download Attachment Archived");
        var attachmentId = await UploadAttachmentAsync(cardId);
        await ArchiveCardAsync(cardId);

        // Act
        var response = await _client.GetAsync($"/api/v1/attachments/{attachmentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteCard_OnArchivedCard_Succeeds()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var cardId = await CreateCardAsync(laneId, "Delete Archived Card");
        await ArchiveCardAsync(cardId);

        // Act
        var response = await _client.DeleteAsync($"/api/v1/cards/{cardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private async Task<Guid> GetFirstLaneIdAsync()
        => await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

    private async Task<Guid> CreateCardAsync(Guid laneId, string name)
    {
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name, laneId }
        );
        response.EnsureSuccessStatusCode();
        var card = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return card.GetProperty("id").GetGuid();
    }

    private async Task ArchiveCardAsync(Guid cardId)
    {
        var response = await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private async Task<Guid> CreateCommentAsync(Guid cardId, string content)
    {
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/cards/{cardId}/comments",
            new { contentMarkdown = content }
        );
        response.EnsureSuccessStatusCode();
        var comment = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return comment.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateLabelAsync(string name)
    {
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/labels",
            new { name, color = "#FF0000" }
        );
        response.EnsureSuccessStatusCode();
        var label = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return label.GetProperty("id").GetGuid();
    }

    private static MultipartFormDataContent CreateFileUpload()
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([1, 2, 3, 4]);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "test.bin");
        return content;
    }

    private async Task<Guid> UploadAttachmentAsync(Guid cardId)
    {
        var upload = CreateFileUpload();
        var response = await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", upload);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task CreateCard_InArchiveLane_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var archiveLaneId = await GetArchiveLaneIdAsync();

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name = "Sneaky Card", laneId = archiveLaneId }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchCard_MoveToArchiveLane_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);
        var archiveLaneId = await GetArchiveLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name = "Move To Archive Test", laneId }
        );
        createResponse.EnsureSuccessStatusCode();
        var card = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var cardId = card.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PatchAsJsonAsync
        (
            $"/api/v1/cards/{cardId}",
            new { laneId = archiveLaneId }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<Guid> GetArchiveLaneIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var archiveLane = await db.Lanes.FirstAsync(l => l.BoardId == _factory.DefaultBoardId && l.IsArchiveLane);
        return archiveLane.Id;
    }
}
