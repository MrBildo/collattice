using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collabot.Collattice.Api.Mcp;
using Collabot.Collattice.Api.Models;
using Collabot.Collattice.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// A write that checks the card, lane, label or size it refers to and then loses it to a delete
// committing in between used to fail with a 500 (an unhandled tool error on MCP). It now answers 409
// on REST and an "Error: " string on MCP, the same for every write path. A delete that is allowed
// only while nothing depends on the row (a lane, a size, a board) used to check, then delete, and
// take with it whatever landed in between; it now refuses instead. The interceptor commits each rival
// change inside the real request, right after the request's own check.
public class ConcurrentDeleteRaceTests(ConcurrentDeleteRaceFactory factory) : IClassFixture<ConcurrentDeleteRaceFactory>, IDisposable
{
    private const string _historyInsert = "INSERT INTO \"CardFieldHistories\"";
    private const string _lostToDelete = "Error: Something this change refers to was changed or deleted at the same moment. Reload and try again.";

    private readonly ConcurrentDeleteRaceFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly List<IServiceScope> _scopes = [];

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // Built the way the MCP server builds a tool class: from its constructor, out of a request scope.
    private T CreateMcpTools<T>()
        where T : class
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);

        return ActivatorUtilities.CreateInstance<T>(scope.ServiceProvider);
    }

    // Runs a tool call through the server's real CallToolFilter, which is where an MCP write that
    // lost to a delete is answered; calling the tool method alone would surface the raw exception.
    private static async Task<string> ThroughCallToolFilterAsync(Func<Task<string>> toolCall)
    {
        var pipeline = McpErrorTranslator.WrapForCallTool(async (_, _) => new CallToolResult
        {
            Content = [new TextContentBlock { Text = await toolCall() }],
        });

        var result = await pipeline(null!, CancellationToken.None);

        return result.Content
            .OfType<TextContentBlock>()
                .Single()
                    .Text;
    }

    private async Task<Guid> PostForIdAsync(string url, object body)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var response = await _client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<(Guid BoardId, Guid LaneId, Guid CardId)> SeedCardAsync()
    {
        var boardId = await PostForIdAsync("/api/v1/boards", new { name = $"Delete Race {Guid.NewGuid():N}" });
        var laneId = await PostForIdAsync($"/api/v1/boards/{boardId}/lanes", new { name = "Lane", position = 0 });
        var cardId = await PostForIdAsync($"/api/v1/boards/{boardId}/cards", new { laneId, name = "Card", descriptionMarkdown = "Before" });

        return (boardId, laneId, cardId);
    }

    private async Task<T> ReadAsync<T>(Func<BoardDbContext, Task<T>> read)
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        return await read(scope.ServiceProvider.GetRequiredService<BoardDbContext>());
    }

    private Task<bool> CardExistsAsync(Guid cardId) =>
        ReadAsync(db => db.Cards.AnyAsync(c => c.Id == cardId));

    private static Func<BoardDbContext, Task> DeleteCard(Guid cardId) =>
        db => db.Cards
            .Where(c => c.Id == cardId)
                .ExecuteDeleteAsync();

    // A card committed straight to the store, as another request's create or move would leave it.
    private static Func<BoardDbContext, Task> InsertCard(Guid boardId, Guid laneId, Guid cardId, Guid? sizeId = null) =>
        async db =>
        {
            var adminId = await db.Users
                .Where(u => u.Role == UserRole.Administrator)
                    .Select(u => u.Id)
                        .FirstAsync();

            var lowestSizeId = await db.CardSizes
                .Where(s => s.BoardId == boardId)
                .OrderBy(s => s.Ordinal)
                    .Select(s => s.Id)
                        .FirstAsync();

            db.Cards.Add(new CardItem
            {
                Id = cardId,
                Number = 900,
                BoardId = boardId,
                Name = "Arrived",
                SizeId = sizeId ?? lowestSizeId,
                LaneId = laneId,
                Position = 0,
                CreatedByUserId = adminId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                LastUpdatedByUserId = adminId,
                LastUpdatedAtUtc = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync();
        };

    [Fact]
    public async Task AddComment_CardDeletedAfterTheCheck_ConflictsOnBothSurfaces()
    {
        // Arrange
        var (_, _, restCardId) = await SeedCardAsync();
        var (_, _, mcpCardId) = await SeedCardAsync();
        var tools = CreateMcpTools<CommentTools>();

        // Act — the card exists when checked and is deleted before the comment is saved
        _factory.Interceptor.Arm("Cards", DeleteCard(restCardId));
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{restCardId}/comments", new { contentMarkdown = "Late" });
        var restFired = _factory.Interceptor.FiredCount;

        _factory.Interceptor.Arm("Cards", DeleteCard(mcpCardId));
        var mcpResult = await ThroughCallToolFilterAsync(() => tools.AddCommentAsync(_factory.AdminAuthKey, "Late", cardId: mcpCardId));
        var mcpFired = _factory.Interceptor.FiredCount;

        // Assert
        restFired.ShouldBe(1);
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("was changed or deleted at the same moment");

        mcpFired.ShouldBe(1);
        mcpResult.ShouldBe(_lostToDelete);

        (await ReadAsync(db => db.Comments.AnyAsync(c => c.CardId == restCardId || c.CardId == mcpCardId))).ShouldBeFalse();
    }

    [Fact]
    public async Task UploadAttachment_CardDeletedAfterTheCheck_ConflictsOnBothSurfaces()
    {
        // Arrange
        var (_, _, restCardId) = await SeedCardAsync();
        var (_, _, mcpCardId) = await SeedCardAsync();
        var tools = CreateMcpTools<AttachmentTools>();

        using var file = new ByteArrayContent([1, 2, 3]);
        file.Headers.ContentType = new("application/octet-stream");
        using var form = new MultipartFormDataContent { { file, "file", "late.bin" } };

        // Act
        _factory.Interceptor.Arm("Cards", DeleteCard(restCardId));
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsync($"/api/v1/cards/{restCardId}/attachments", form);

        _factory.Interceptor.Arm("Cards", DeleteCard(mcpCardId));
        var mcpResult = await ThroughCallToolFilterAsync(() => tools.UploadAttachmentAsync(_factory.AdminAuthKey, "late.bin", "AQID", cardId: mcpCardId));

        // Assert
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        mcpResult.ShouldBe(_lostToDelete);

        (await ReadAsync(db => db.Attachments.AnyAsync(a => a.CardId == restCardId || a.CardId == mcpCardId))).ShouldBeFalse();
    }

    [Fact]
    public async Task AddLabelToCard_LabelDeletedAfterTheCheck_ConflictsOnBothSurfaces()
    {
        // Arrange
        var (restBoardId, _, restCardId) = await SeedCardAsync();
        var (mcpBoardId, _, mcpCardId) = await SeedCardAsync();
        var restLabelId = await PostForIdAsync($"/api/v1/boards/{restBoardId}/labels", new { name = "Doomed", color = "#111111" });
        var mcpLabelId = await PostForIdAsync($"/api/v1/boards/{mcpBoardId}/labels", new { name = "Doomed", color = "#111111" });
        var tools = CreateMcpTools<LabelTools>();

        // Act — the label exists when checked and is deleted before it is assigned
        _factory.Interceptor.Arm("Labels", db => db.Labels.Where(l => l.Id == restLabelId).ExecuteDeleteAsync());
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/cards/{restCardId}/labels", new { labelId = restLabelId });

        _factory.Interceptor.Arm("Labels", db => db.Labels.Where(l => l.Id == mcpLabelId).ExecuteDeleteAsync());
        var mcpResult = await ThroughCallToolFilterAsync(() => tools.AddLabelToCardAsync(_factory.AdminAuthKey, cardId: mcpCardId, labelId: mcpLabelId));

        // Assert
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        mcpResult.ShouldBe(_lostToDelete);

        (await ReadAsync(db => db.CardLabels.AnyAsync(cl => cl.CardId == restCardId || cl.CardId == mcpCardId))).ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateCardDescription_CardDeletedAfterItIsLoaded_ConflictsOnBothSurfaces()
    {
        // Arrange — the description edit also writes history rows under the card
        var (_, _, restCardId) = await SeedCardAsync();
        var (_, _, mcpCardId) = await SeedCardAsync();
        var tools = CreateMcpTools<CardTools>();

        // Act
        _factory.Interceptor.Arm("Cards", DeleteCard(restCardId));
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PatchAsJsonAsync($"/api/v1/cards/{restCardId}", new { descriptionMarkdown = "After" });
        var restHistorySaves = _factory.Interceptor.CommandsContaining(_historyInsert);

        _factory.Interceptor.Arm("Cards", DeleteCard(mcpCardId));
        var mcpResult = await ThroughCallToolFilterAsync(() => tools.UpdateCardAsync(_factory.AdminAuthKey, cardId: mcpCardId, descriptionMarkdown: "After"));
        var mcpHistorySaves = _factory.Interceptor.CommandsContaining(_historyInsert);

        // Assert — answered on the first failed save; a missing card is not a revision collision to
        // retry
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        restHistorySaves.ShouldBe(1);

        mcpResult.ShouldBe(_lostToDelete);
        mcpHistorySaves.ShouldBe(1);

        (await ReadAsync(db => db.CardFieldHistories.AnyAsync(h => h.CardId == restCardId || h.CardId == mcpCardId))).ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateComment_CardDeletedAfterTheCommentIsLoaded_ConflictsOnBothSurfaces()
    {
        // Arrange — deleting the card takes its comments with it
        var (_, _, restCardId) = await SeedCardAsync();
        var (_, _, mcpCardId) = await SeedCardAsync();
        var restCommentId = await PostForIdAsync($"/api/v1/cards/{restCardId}/comments", new { contentMarkdown = "First" });
        var mcpCommentId = await PostForIdAsync($"/api/v1/cards/{mcpCardId}/comments", new { contentMarkdown = "First" });
        var tools = CreateMcpTools<CommentTools>();

        // Act
        _factory.Interceptor.Arm("Comments", DeleteCard(restCardId));
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PatchAsJsonAsync($"/api/v1/comments/{restCommentId}", new { contentMarkdown = "Edited" });

        _factory.Interceptor.Arm("Comments", DeleteCard(mcpCardId));
        var mcpResult = await ThroughCallToolFilterAsync(() => tools.UpdateCommentAsync(_factory.AdminAuthKey, mcpCommentId, "Edited"));

        // Assert
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        mcpResult.ShouldBe(_lostToDelete);
    }

    [Fact]
    public async Task CreateCard_LaneDeletedAfterTheCheck_ConflictsOnBothSurfaces()
    {
        // Arrange — an empty second lane on each board, deleted as the card is created into it
        var (restBoardId, _, _) = await SeedCardAsync();
        var (mcpBoardId, _, _) = await SeedCardAsync();
        var restLaneId = await PostForIdAsync($"/api/v1/boards/{restBoardId}/lanes", new { name = "Doomed", position = 1 });
        var mcpLaneId = await PostForIdAsync($"/api/v1/boards/{mcpBoardId}/lanes", new { name = "Doomed", position = 1 });
        var tools = CreateMcpTools<CardTools>();

        // Act — the lane passes every check (the size is resolved last), then is deleted before the insert
        _factory.Interceptor.Arm("CardSizes", db => db.Lanes.Where(l => l.Id == restLaneId).ExecuteDeleteAsync());
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/cards", new { laneId = restLaneId, name = "Late" });

        _factory.Interceptor.Arm("CardSizes", db => db.Lanes.Where(l => l.Id == mcpLaneId).ExecuteDeleteAsync());
        var mcpResult = await ThroughCallToolFilterAsync(() => tools.CreateCardAsync(_factory.AdminAuthKey, "Late", mcpLaneId));

        // Assert
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        mcpResult.ShouldBe(_lostToDelete);

        (await ReadAsync(db => db.Cards.AnyAsync(c => c.Name == "Late" && (c.BoardId == restBoardId || c.BoardId == mcpBoardId)))).ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteLane_CardArrivesAfterTheEmptinessCheck_RefusesAndKeepsTheCardOnBothSurfaces()
    {
        // Arrange
        var (restBoardId, _, _) = await SeedCardAsync();
        var (mcpBoardId, _, _) = await SeedCardAsync();
        var restLaneId = await PostForIdAsync($"/api/v1/boards/{restBoardId}/lanes", new { name = "Target", position = 1 });
        var mcpLaneId = await PostForIdAsync($"/api/v1/boards/{mcpBoardId}/lanes", new { name = "Target", position = 1 });
        var restArrivedId = Guid.NewGuid();
        var mcpArrivedId = Guid.NewGuid();
        var tools = CreateMcpTools<LaneTools>();

        // Act — the lane is empty when checked, then a card lands in it before the delete
        _factory.Interceptor.Arm("Cards", InsertCard(restBoardId, restLaneId, restArrivedId));
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.DeleteAsync($"/api/v1/lanes/{restLaneId}");
        var restFired = _factory.Interceptor.FiredCount;

        _factory.Interceptor.Arm("Cards", InsertCard(mcpBoardId, mcpLaneId, mcpArrivedId));
        var mcpResult = await tools.DeleteLaneAsync(_factory.AdminAuthKey, mcpLaneId);
        var mcpFired = _factory.Interceptor.FiredCount;

        // Assert — the answer a lane with a card always gets, and the card is still there
        restFired.ShouldBe(1);
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("Lane must be empty");
        (await CardExistsAsync(restArrivedId)).ShouldBeTrue();

        mcpFired.ShouldBe(1);
        mcpResult.ShouldBe("Error: Lane must be empty.");
        (await CardExistsAsync(mcpArrivedId)).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteLane_AnotherDeleteOfItLandsAfterTheEmptinessCheck_AnswersNotFoundOnBothSurfaces()
    {
        // Arrange
        var (restBoardId, _, _) = await SeedCardAsync();
        var (mcpBoardId, _, _) = await SeedCardAsync();
        var restLaneId = await PostForIdAsync($"/api/v1/boards/{restBoardId}/lanes", new { name = "Target", position = 1 });
        var mcpLaneId = await PostForIdAsync($"/api/v1/boards/{mcpBoardId}/lanes", new { name = "Target", position = 1 });
        var tools = CreateMcpTools<LaneTools>();

        // Act — the lane is empty when checked, then a simultaneous delete of it commits first, so this
        // delete removes nothing and must not claim the lane is still in use
        _factory.Interceptor.Arm("Cards", db => db.Lanes.Where(l => l.Id == restLaneId).ExecuteDeleteAsync());
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.DeleteAsync($"/api/v1/lanes/{restLaneId}");
        var restFired = _factory.Interceptor.FiredCount;

        _factory.Interceptor.Arm("Cards", db => db.Lanes.Where(l => l.Id == mcpLaneId).ExecuteDeleteAsync());
        var mcpResult = await tools.DeleteLaneAsync(_factory.AdminAuthKey, mcpLaneId);
        var mcpFired = _factory.Interceptor.FiredCount;

        // Assert — the answer a delete arriving after the other one gets
        restFired.ShouldBe(1);
        restResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        mcpFired.ShouldBe(1);
        mcpResult.ShouldBe("Error: Lane not found.");
    }

    [Fact]
    public async Task DeleteSize_CardStartsUsingItAfterTheCheck_RefusesOnBothSurfaces()
    {
        // Arrange
        var (restBoardId, restLaneId, _) = await SeedCardAsync();
        var (mcpBoardId, mcpLaneId, _) = await SeedCardAsync();
        var restSizeId = await PostForIdAsync($"/api/v1/boards/{restBoardId}/sizes", new { name = "Spare", ordinal = 10 });
        var mcpSizeId = await PostForIdAsync($"/api/v1/boards/{mcpBoardId}/sizes", new { name = "Spare", ordinal = 10 });
        var tools = CreateMcpTools<SizeTools>();

        // Act — no card uses the size when checked, then one is created with it before the delete
        _factory.Interceptor.Arm("Cards", InsertCard(restBoardId, restLaneId, Guid.NewGuid(), restSizeId));
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.DeleteAsync($"/api/v1/sizes/{restSizeId}");

        _factory.Interceptor.Arm("Cards", InsertCard(mcpBoardId, mcpLaneId, Guid.NewGuid(), mcpSizeId));
        var mcpResult = await tools.DeleteSizeAsync(_factory.AdminAuthKey, mcpSizeId);

        // Assert
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("Size is in use by cards");

        mcpResult.ShouldBe("Error: Size is in use by cards.");

        (await ReadAsync(db => db.CardSizes.CountAsync(s => s.Id == restSizeId || s.Id == mcpSizeId))).ShouldBe(2);
    }

    [Fact]
    public async Task DeleteBoard_LaneArrivesAfterTheCheck_RefusesAndKeepsTheLane()
    {
        // Arrange — a board with only its archive lane; delete board is REST-only
        var boardId = await PostForIdAsync("/api/v1/boards", new { name = $"Delete Race {Guid.NewGuid():N}" });
        var arrivedLaneId = Guid.NewGuid();

        // Act — the board has no lanes when checked, then one is created before the delete
        _factory.Interceptor.Arm("Lanes", async db =>
        {
            db.Lanes.Add(new Lane { Id = arrivedLaneId, BoardId = boardId, Name = "Arrived", Position = 0 });
            await db.SaveChangesAsync();
        });
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.DeleteAsync($"/api/v1/boards/{boardId}");

        // Assert — the answer a board with a lane always gets, and the lane is still there
        _factory.Interceptor.FiredCount.ShouldBe(1);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Board must have no lanes");
        (await ReadAsync(db => db.Lanes.AnyAsync(l => l.Id == arrivedLaneId))).ShouldBeTrue();
    }
}
