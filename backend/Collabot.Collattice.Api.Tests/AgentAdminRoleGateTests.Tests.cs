using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Role-gate matrix coverage for the AgentAdministrator role.
//
// Role-gate behavior: admin-level endpoints widen to AgentAdministrator; strict-admin endpoints stay Administrator-only.
//
// Each widened endpoint is exercised with three role classes:
//   - Administrator         → expected success (status quo)
//   - AgentAdministrator    → expected success (the new admit)
//   - HumanUser / AgentUser → expected 403 (status quo)
//
// Strict endpoints (DELETE /boards/{id}, user CRUD, prune action=delete) are
// also verified to keep rejecting AgentAdministrator.
public class AgentAdminRoleGateTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;

    // Lane positions are unique per board, and these tests create lanes on the shared default board,
    // so they accumulate across the class run. A monotonic counter keeps them distinct by
    // construction; a random pick eventually collided on the unique (BoardId, Position) index
    // (birthday paradox), which read as an intermittent CI failure on unchanged code.
    private static int _nextLanePosition = 50_000;
    private static int NextLanePosition() => Interlocked.Increment(ref _nextLanePosition);

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, _factory);
        return client;
    }

    private async Task<HttpClient> ClientForRoleAsync(UserRole role, string nameHint)
    {
        var setupClient = _factory.CreateClient();
        var user = await TestAuthHelper.CreateUserAsync
        (
            setupClient,
            _factory,
            $"{nameHint}-{role}-{Guid.NewGuid():N}",
            role
        );

        var client = _factory.CreateClient();
        TestAuthHelper.SetAuth(client, user.AuthKey);
        return client;
    }

    // Lanes — POST, PATCH, DELETE all widen to AdminOrAgentAdmin.

    [Theory]
    [InlineData(UserRole.Administrator, HttpStatusCode.Created)]
    [InlineData(UserRole.AgentAdministrator, HttpStatusCode.Created)]
    [InlineData(UserRole.HumanUser, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.AgentUser, HttpStatusCode.Forbidden)]
    public async Task PostLane_RoleGate(UserRole role, HttpStatusCode expected)
    {
        // Arrange
        var client = await ClientForRoleAsync(role, "post-lane");

        // Act
        var response = await client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/lanes",
            new { name = $"RoleGate Lane {Guid.NewGuid():N}", position = NextLanePosition() }
        );

        // Assert
        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData(UserRole.Administrator, HttpStatusCode.OK)]
    [InlineData(UserRole.AgentAdministrator, HttpStatusCode.OK)]
    [InlineData(UserRole.HumanUser, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.AgentUser, HttpStatusCode.Forbidden)]
    public async Task PatchLane_RoleGate(UserRole role, HttpStatusCode expected)
    {
        // Arrange
        var laneId = await CreateLaneAsAdminAsync("RoleGate Patch Lane", NextLanePosition());
        var client = await ClientForRoleAsync(role, "patch-lane");

        // Act
        var response = await client.PatchAsJsonAsync
        (
            $"/api/v1/lanes/{laneId}",
            new { name = "Renamed" }
        );

        // Assert
        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData(UserRole.Administrator, HttpStatusCode.NoContent)]
    [InlineData(UserRole.AgentAdministrator, HttpStatusCode.NoContent)]
    [InlineData(UserRole.HumanUser, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.AgentUser, HttpStatusCode.Forbidden)]
    public async Task DeleteLane_RoleGate(UserRole role, HttpStatusCode expected)
    {
        // Arrange
        var laneId = await CreateLaneAsAdminAsync("RoleGate Delete Lane", NextLanePosition());
        var client = await ClientForRoleAsync(role, "delete-lane");

        // Act
        var response = await client.DeleteAsync($"/api/v1/lanes/{laneId}");

        // Assert
        response.StatusCode.ShouldBe(expected);
    }

    // Labels — POST, PATCH, DELETE all widen to AdminOrAgentAdmin.

    [Theory]
    [InlineData(UserRole.Administrator, HttpStatusCode.Created)]
    [InlineData(UserRole.AgentAdministrator, HttpStatusCode.Created)]
    [InlineData(UserRole.HumanUser, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.AgentUser, HttpStatusCode.Forbidden)]
    public async Task PostLabel_RoleGate(UserRole role, HttpStatusCode expected)
    {
        // Arrange
        var client = await ClientForRoleAsync(role, "post-label");

        // Act
        var response = await client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/labels",
            new { name = $"RoleGate Label {Guid.NewGuid():N}", color = "#abcdef" }
        );

        // Assert
        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData(UserRole.Administrator, HttpStatusCode.OK)]
    [InlineData(UserRole.AgentAdministrator, HttpStatusCode.OK)]
    [InlineData(UserRole.HumanUser, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.AgentUser, HttpStatusCode.Forbidden)]
    public async Task PatchLabel_RoleGate(UserRole role, HttpStatusCode expected)
    {
        // Arrange
        var labelId = await CreateLabelAsAdminAsync();
        var client = await ClientForRoleAsync(role, "patch-label");

        // Act
        var response = await client.PatchAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/labels/{labelId}",
            new { name = $"Renamed-{role}-{Guid.NewGuid():N}" }
        );

        // Assert
        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData(UserRole.Administrator, HttpStatusCode.NoContent)]
    [InlineData(UserRole.AgentAdministrator, HttpStatusCode.NoContent)]
    [InlineData(UserRole.HumanUser, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.AgentUser, HttpStatusCode.Forbidden)]
    public async Task DeleteLabel_RoleGate(UserRole role, HttpStatusCode expected)
    {
        // Arrange
        var labelId = await CreateLabelAsAdminAsync();
        var client = await ClientForRoleAsync(role, "delete-label");

        // Act
        var response = await client.DeleteAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels/{labelId}");

        // Assert
        response.StatusCode.ShouldBe(expected);
    }

    // Sizes — POST, PATCH, DELETE all widen to AdminOrAgentAdmin.

    [Theory]
    [InlineData(UserRole.Administrator, HttpStatusCode.Created)]
    [InlineData(UserRole.AgentAdministrator, HttpStatusCode.Created)]
    [InlineData(UserRole.HumanUser, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.AgentUser, HttpStatusCode.Forbidden)]
    public async Task PostSize_RoleGate(UserRole role, HttpStatusCode expected)
    {
        // Arrange
        var client = await ClientForRoleAsync(role, "post-size");

        // Act
        var response = await client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/sizes",
            new { name = $"RG-{Guid.NewGuid():N}".Substring(0, 8) }
        );

        // Assert
        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData(UserRole.Administrator, HttpStatusCode.OK)]
    [InlineData(UserRole.AgentAdministrator, HttpStatusCode.OK)]
    [InlineData(UserRole.HumanUser, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.AgentUser, HttpStatusCode.Forbidden)]
    public async Task PatchSize_RoleGate(UserRole role, HttpStatusCode expected)
    {
        // Arrange
        var sizeId = await CreateSizeAsAdminAsync();
        var client = await ClientForRoleAsync(role, "patch-size");

        // Act
        var response = await client.PatchAsJsonAsync
        (
            $"/api/v1/sizes/{sizeId}",
            new { name = $"R-{role}-{Guid.NewGuid().ToString("N").Substring(0, 6)}" }
        );

        // Assert
        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData(UserRole.Administrator, HttpStatusCode.NoContent)]
    [InlineData(UserRole.AgentAdministrator, HttpStatusCode.NoContent)]
    [InlineData(UserRole.HumanUser, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.AgentUser, HttpStatusCode.Forbidden)]
    public async Task DeleteSize_RoleGate(UserRole role, HttpStatusCode expected)
    {
        // Arrange
        var sizeId = await CreateSizeAsAdminAsync();
        var client = await ClientForRoleAsync(role, "delete-size");

        // Act
        var response = await client.DeleteAsync($"/api/v1/sizes/{sizeId}");

        // Assert
        response.StatusCode.ShouldBe(expected);
    }

    // Boards — POST and PATCH widen; DELETE stays strict.

    [Theory]
    [InlineData(UserRole.Administrator, HttpStatusCode.Created)]
    [InlineData(UserRole.AgentAdministrator, HttpStatusCode.Created)]
    [InlineData(UserRole.HumanUser, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.AgentUser, HttpStatusCode.Forbidden)]
    public async Task PostBoard_RoleGate(UserRole role, HttpStatusCode expected)
    {
        // Arrange
        var client = await ClientForRoleAsync(role, "post-board");

        // Act
        var response = await client.PostAsJsonAsync
        (
            "/api/v1/boards",
            new { name = $"RoleGate Board {Guid.NewGuid():N}" }
        );

        // Assert
        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData(UserRole.Administrator, HttpStatusCode.OK)]
    [InlineData(UserRole.AgentAdministrator, HttpStatusCode.OK)]
    [InlineData(UserRole.HumanUser, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.AgentUser, HttpStatusCode.Forbidden)]
    public async Task PatchBoard_RoleGate(UserRole role, HttpStatusCode expected)
    {
        // Arrange
        var boardId = await CreateBoardAsAdminAsync();
        var client = await ClientForRoleAsync(role, "patch-board");

        // Act
        var response = await client.PatchAsJsonAsync
        (
            $"/api/v1/boards/{boardId}",
            new { name = $"Renamed {Guid.NewGuid():N}" }
        );

        // Assert
        response.StatusCode.ShouldBe(expected);
    }

    // DELETE /boards/{id} stays strict — AgentAdministrator must be 403, not success.
    [Theory]
    [InlineData(UserRole.AgentAdministrator)]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task DeleteBoard_NonStrictAdmin_Returns403(UserRole role)
    {
        // Arrange
        var boardId = await CreateBoardAsAdminAsync();
        var client = await ClientForRoleAsync(role, "delete-board");

        // Act
        var response = await client.DeleteAsync($"/api/v1/boards/{boardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // Prune — preview + action=archive widen; action=delete stays Administrator-only.

    [Theory]
    [InlineData(UserRole.Administrator, HttpStatusCode.OK)]
    [InlineData(UserRole.AgentAdministrator, HttpStatusCode.OK)]
    [InlineData(UserRole.HumanUser, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.AgentUser, HttpStatusCode.Forbidden)]
    public async Task PrunePreview_RoleGate(UserRole role, HttpStatusCode expected)
    {
        // Arrange
        var client = await ClientForRoleAsync(role, "prune-preview");

        // Act — pass a filter (preview requires at least one)
        var response = await client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune/preview",
            new { olderThan = DateTimeOffset.UtcNow.AddYears(-100) }
        );

        // Assert
        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData(UserRole.Administrator, HttpStatusCode.OK)]
    [InlineData(UserRole.AgentAdministrator, HttpStatusCode.OK)]
    [InlineData(UserRole.HumanUser, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.AgentUser, HttpStatusCode.Forbidden)]
    public async Task PruneArchive_RoleGate(UserRole role, HttpStatusCode expected)
    {
        // Arrange
        var client = await ClientForRoleAsync(role, "prune-archive");

        // Act — archive action, filter that matches nothing (no cards old enough)
        var response = await client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune",
            new { action = "archive", olderThan = DateTimeOffset.UtcNow.AddYears(-100) }
        );

        // Assert
        response.StatusCode.ShouldBe(expected);
    }

    // action=delete is rejected in-body for AgentAdministrator (the only
    // in-body role check the design admits — see PruneEndpoints.cs for the enforcement).
    [Fact]
    public async Task PruneDelete_AsAgentAdministrator_Returns403()
    {
        // Arrange
        var client = await ClientForRoleAsync(UserRole.AgentAdministrator, "prune-delete");

        // Act
        var response = await client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune",
            new { action = "delete", olderThan = DateTimeOffset.UtcNow.AddYears(-100) }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PruneDelete_AsAdministrator_Returns200()
    {
        // Arrange — strict admin can still delete via prune
        var client = AdminClient();

        // Act
        var response = await client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/prune",
            new { action = "delete", olderThan = DateTimeOffset.UtcNow.AddYears(-100) }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // Users — all admin user endpoints stay strict; AgentAdministrator is 403.

    [Fact]
    public async Task PostUser_AsAgentAdministrator_Returns403()
    {
        // Arrange
        var client = await ClientForRoleAsync(UserRole.AgentAdministrator, "post-user");

        // Act
        var response = await client.PostAsJsonAsync
        (
            "/api/v1/users",
            new { name = "Should Not Create", role = (int)UserRole.AgentUser }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PatchUser_AsAgentAdministrator_Returns403()
    {
        // Arrange — make a target user; only an Administrator should be able to PATCH them.
        var adminClient = AdminClient();
        var targetResponse = await adminClient.PostAsJsonAsync
        (
            "/api/v1/users",
            new { name = $"PatchTarget-{Guid.NewGuid():N}", role = (int)UserRole.AgentUser }
        );
        targetResponse.EnsureSuccessStatusCode();
        var target = await targetResponse.Content.ReadFromJsonAsync<JsonElement>();
        var targetId = target.GetProperty("id").GetGuid();

        var client = await ClientForRoleAsync(UserRole.AgentAdministrator, "patch-user");

        // Act
        var response = await client.PatchAsJsonAsync
        (
            $"/api/v1/users/{targetId}",
            new { name = "Renamed" }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeactivateUser_AsAgentAdministrator_Returns403()
    {
        // Arrange
        var adminClient = AdminClient();
        var targetResponse = await adminClient.PostAsJsonAsync
        (
            "/api/v1/users",
            new { name = $"DeactivateTarget-{Guid.NewGuid():N}", role = (int)UserRole.AgentUser }
        );
        targetResponse.EnsureSuccessStatusCode();
        var target = await targetResponse.Content.ReadFromJsonAsync<JsonElement>();
        var targetId = target.GetProperty("id").GetGuid();

        var client = await ClientForRoleAsync(UserRole.AgentAdministrator, "deactivate-user");

        // Act
        var response = await client.PatchAsync($"/api/v1/users/{targetId}/deactivate", content: null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetUsers_AsAgentAdministrator_Returns403()
    {
        // Arrange
        var client = await ClientForRoleAsync(UserRole.AgentAdministrator, "get-users");

        // Act
        var response = await client.GetAsync("/api/v1/users");

        // Assert — auth-key list is sensitive; AgentAdministrator must not see it.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // Moderation — DELETE comment/attachment is own-or-admin-level.
    // AgentAdministrator may delete another user's content over REST, matching
    // the MCP delete_comment / delete_attachment tools. Plain non-owners cannot.

    // Admin-level roles can delete a comment they did not author.
    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task DeleteComment_AsAdminLevel_DeletesOthersComment(UserRole role)
    {
        // Arrange — a HumanUser authors the comment; an admin-level user deletes it.
        var commentId = await CreateCommentAsAuthorAsync();
        var client = await ClientForRoleAsync(role, "delete-comment-admin");

        // Act
        var response = await client.DeleteAsync($"/api/v1/comments/{commentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    // Non-owner, non-admin roles cannot delete someone else's comment.
    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task DeleteComment_AsNonOwnerNonAdmin_Returns403(UserRole role)
    {
        // Arrange
        var commentId = await CreateCommentAsAuthorAsync();
        var client = await ClientForRoleAsync(role, "delete-comment-nonadmin");

        // Act
        var response = await client.DeleteAsync($"/api/v1/comments/{commentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // Admin-level roles can delete an attachment they did not add.
    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task DeleteAttachment_AsAdminLevel_DeletesOthersAttachment(UserRole role)
    {
        // Arrange — a HumanUser adds the attachment; an admin-level user deletes it.
        var attachmentId = await CreateAttachmentAsAuthorAsync();
        var client = await ClientForRoleAsync(role, "delete-attachment-admin");

        // Act
        var response = await client.DeleteAsync($"/api/v1/attachments/{attachmentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    // Non-owner, non-admin roles cannot delete someone else's attachment.
    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task DeleteAttachment_AsNonOwnerNonAdmin_Returns403(UserRole role)
    {
        // Arrange
        var attachmentId = await CreateAttachmentAsAuthorAsync();
        var client = await ClientForRoleAsync(role, "delete-attachment-nonadmin");

        // Act
        var response = await client.DeleteAsync($"/api/v1/attachments/{attachmentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // Helpers

    private async Task<Guid> CreateLaneAsAdminAsync(string namePrefix, int position)
    {
        var client = AdminClient();
        var response = await client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/lanes",
            new { name = $"{namePrefix} {Guid.NewGuid():N}", position }
        );
        response.EnsureSuccessStatusCode();
        var lane = await response.Content.ReadFromJsonAsync<JsonElement>();
        return lane.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateLabelAsAdminAsync()
    {
        var client = AdminClient();
        var response = await client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/labels",
            new { name = $"RoleGate Label {Guid.NewGuid():N}", color = "#cccccc" }
        );
        response.EnsureSuccessStatusCode();
        var label = await response.Content.ReadFromJsonAsync<JsonElement>();
        return label.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateSizeAsAdminAsync()
    {
        var client = AdminClient();
        var response = await client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/sizes",
            new { name = $"RG-{Guid.NewGuid().ToString("N").Substring(0, 6)}" }
        );
        response.EnsureSuccessStatusCode();
        var size = await response.Content.ReadFromJsonAsync<JsonElement>();
        return size.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateBoardAsAdminAsync()
    {
        var client = AdminClient();
        var response = await client.PostAsJsonAsync
        (
            "/api/v1/boards",
            new { name = $"RoleGate Board {Guid.NewGuid():N}" }
        );
        response.EnsureSuccessStatusCode();
        var board = await response.Content.ReadFromJsonAsync<JsonElement>();
        return board.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateCardAsAdminAsync()
    {
        var client = AdminClient();
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, _factory.DefaultBoardId);
        var response = await client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name = $"RoleGate Card {Guid.NewGuid():N}", laneId }
        );
        response.EnsureSuccessStatusCode();
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        return card.GetProperty("id").GetGuid();
    }

    // Authors a comment as a fresh HumanUser so the deleter in the test is always
    // a different user — exercising the own-or-admin branch, not the own branch.
    private async Task<Guid> CreateCommentAsAuthorAsync()
    {
        var cardId = await CreateCardAsAdminAsync();
        var authorClient = await ClientForRoleAsync(UserRole.HumanUser, "comment-author");

        var response = await authorClient.PostAsJsonAsync
        (
            $"/api/v1/cards/{cardId}/comments",
            new { contentMarkdown = "Comment by another user" }
        );
        response.EnsureSuccessStatusCode();
        var comment = await response.Content.ReadFromJsonAsync<JsonElement>();
        return comment.GetProperty("id").GetGuid();
    }

    // Adds an attachment as a fresh HumanUser, for the same own-vs-admin reason.
    private async Task<Guid> CreateAttachmentAsAuthorAsync()
    {
        var cardId = await CreateCardAsAdminAsync();
        var authorClient = await ClientForRoleAsync(UserRole.HumanUser, "attachment-author");

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([1, 2, 3, 4]);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "rolegate.bin");

        var response = await authorClient.PostAsync($"/api/v1/cards/{cardId}/attachments", content);
        response.EnsureSuccessStatusCode();
        var attachment = await response.Content.ReadFromJsonAsync<JsonElement>();
        return attachment.GetProperty("id").GetGuid();
    }
}
