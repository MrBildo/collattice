using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// A draft is a card started in the create dialog and not yet saved. It belongs to the person
// creating it: anyone else, an administrator included, gets the answer a card that does not exist
// gets, and nothing they send changes it. Every test seeds a fresh board whose only card is a
// draft started by a HumanUser, carrying one label, one comment and one attachment, then reads the
// draft back from the database to show it is exactly as the creator left it.
public partial class DraftVisibilityTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly List<IServiceScope> _scopes = [];

    // Every REST route that names a card, a comment or an attachment by id. The coverage test below
    // fails when a route is added that is not in this list.
    private static readonly string[] _restRoutes =
    [
        "GET /api/v1/cards/{id:guid}",
        "GET /api/v2/cards/{id:guid}",
        "PATCH /api/v1/cards/{id:guid}",
        "DELETE /api/v1/cards/{id:guid}",
        "POST /api/v1/cards/{id:guid}/reorder",
        "POST /api/v1/cards/{id:guid}/archive",
        "POST /api/v1/cards/{id:guid}/restore",
        "POST /api/v1/cards/{id:guid}/finalize",
        "POST /api/v1/cards/{id:guid}/cancel",
        "GET /api/v1/cards/{id:guid}/history",
        "GET /api/v1/cards/{id:guid}/labels",
        "POST /api/v1/cards/{id:guid}/labels",
        "DELETE /api/v1/cards/{id:guid}/labels/{labelId:guid}",
        "GET /api/v1/cards/{id:guid}/comments",
        "POST /api/v1/cards/{id:guid}/comments",
        "GET /api/v1/cards/{id:guid}/attachments",
        "POST /api/v1/cards/{id:guid}/attachments",
        "PATCH /api/v1/comments/{id:guid}",
        "DELETE /api/v1/comments/{id:guid}",
        "GET /api/v1/attachments/{id:guid}",
        "DELETE /api/v1/attachments/{id:guid}",
    ];

    // Every MCP tool that takes a card, a comment or an attachment by id. The coverage test below
    // fails when a tool is added that is not in this list.
    private static readonly string[] _mcpTools =
    [
        "get_card",
        "move_card",
        "update_card",
        "archive_card",
        "restore_card",
        "duplicate_card",
        "get_card_history",
        "add_comment",
        "update_comment",
        "delete_comment",
        "upload_attachment",
        "download_attachment",
        "delete_attachment",
        "add_label_to_card",
        "remove_label_from_card",
        "bulk_archive_cards",
        "bulk_restore_cards",
        "bulk_update_cards",
    ];

    // The tools above that also take a card number.
    private static readonly string[] _mcpToolsByNumber =
    [
        "get_card",
        "move_card",
        "update_card",
        "archive_card",
        "restore_card",
        "duplicate_card",
        "get_card_history",
        "add_comment",
        "upload_attachment",
        "add_label_to_card",
        "remove_label_from_card",
    ];

    private static readonly string[] _nonCreators = ["other", "admin"];

    public static TheoryData<string, string> RestRoutesForNonCreators() => Cross(_restRoutes, _nonCreators);

    public static TheoryData<string, string> McpToolsForNonCreators() => Cross(_mcpTools, _nonCreators);

    public static TheoryData<string, string> McpToolsByNumberZero() => Cross(_mcpToolsByNumber, ["creator", "other"]);

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [MemberData(nameof(RestRoutesForNonCreators))]
    public async Task RestRoute_AnotherUsersDraft_AnswersNotFoundAndLeavesTheDraftAlone(string route, string caller)
    {
        // Arrange
        var draft = await SeedDraftAsync();
        var before = await ReadDraftAsync(draft);

        // Act
        TestAuthHelper.SetAuth(_client, draft.KeyOf(caller));
        var response = await SendRestAsync(route, draft);
        var nowhere = draft.WithAbsentIds();
        var absent = await SendRestAsync(route, nowhere);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        (await ReadDraftAsync(draft)).ShouldBe(before);
        (await DescribeAsync(response, draft)).ShouldBe(await DescribeAsync(absent, nowhere));
    }

    [Theory]
    [MemberData(nameof(McpToolsForNonCreators))]
    public async Task McpTool_AnotherUsersDraftById_AnswersNotFoundAndLeavesTheDraftAlone(string tool, string caller)
    {
        // Arrange
        var draft = await SeedDraftAsync();
        var before = await ReadDraftAsync(draft);

        // Act
        var result = await CallToolAsync(tool, draft, draft.KeyOf(caller), byNumberZero: false);
        var nowhere = draft.WithAbsentIds();
        var absent = await CallToolAsync(tool, nowhere, draft.KeyOf(caller), byNumberZero: false);

        // Assert
        result.ShouldStartWith("Error");
        result.ShouldContain("not found", Case.Insensitive);
        (await ReadDraftAsync(draft)).ShouldBe(before);
        draft.Anonymize(result).ShouldBe(nowhere.Anonymize(absent));
    }

    [Theory]
    [MemberData(nameof(McpToolsByNumberZero))]
    public async Task McpTool_CardNumberZero_AnswersNotFoundEvenForTheCreator(string tool, string caller)
    {
        // Arrange
        var draft = await SeedDraftAsync();
        var before = await ReadDraftAsync(draft);

        // Act
        var result = await CallToolAsync(tool, draft, draft.KeyOf(caller), byNumberZero: true);

        // Assert
        result.ShouldStartWith("Error");
        result.ShouldContain("not found", Case.Insensitive);
        (await ReadDraftAsync(draft)).ShouldBe(before);
    }

    [Fact]
    public async Task CreateDialogFlow_CreatorUploadsThenSaves_SavesTheCardWithItsAttachment()
    {
        // Arrange
        var draft = await SeedDraftAsync();
        TestAuthHelper.SetAuth(_client, draft.CreatorKey);

        // Act
        var upload = await _client.PostAsync($"/api/v1/cards/{draft.CardId}/attachments", AttachmentContent("second.bin"));
        var finalize = await _client.PostAsync($"/api/v1/cards/{draft.CardId}/finalize", null);

        // Assert
        upload.StatusCode.ShouldBe(HttpStatusCode.Created);
        finalize.StatusCode.ShouldBe(HttpStatusCode.OK);

        var saved = await ReadDraftAsync(draft);
        (saved.IsTemp, saved.Attachments).ShouldBe((false, 2));
    }

    [Fact]
    public async Task CreateDialogFlow_CreatorCancels_RemovesTheDraft()
    {
        // Arrange
        var draft = await SeedDraftAsync();
        TestAuthHelper.SetAuth(_client, draft.CreatorKey);

        // Act
        var response = await _client.PostAsync($"/api/v1/cards/{draft.CardId}/cancel", null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ReadDraftAsync(draft)).Exists.ShouldBeFalse();
    }

    [Fact]
    public async Task GetCard_CreatorReadsOwnDraftById_ReturnsItOverRestAndMcp()
    {
        // Arrange
        var draft = await SeedDraftAsync();
        TestAuthHelper.SetAuth(_client, draft.CreatorKey);

        // Act
        var rest = await _client.GetAsync($"/api/v1/cards/{draft.CardId}");
        var mcp = await Tool<CardTools>().GetCardAsync(draft.CreatorKey, cardId: draft.CardId, commentsLimit: 10);

        // Assert
        rest.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonDocument.Parse(mcp).RootElement.GetProperty("card").GetProperty("id").GetGuid().ShouldBe(draft.CardId);
    }

    [Fact]
    public void RestRoutes_EveryRouteNamingACardCommentOrAttachmentById_IsCovered()
    {
        // Arrange
        var routes = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => IdRoute.IsMatch(endpoint.RoutePattern.RawText ?? string.Empty))
                .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
                    .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
                    .ToHashSet(StringComparer.Ordinal);

        // Act
        var untested = routes.Except(_restRoutes, StringComparer.Ordinal).ToList();

        // Assert
        untested.ShouldBeEmpty();
        routes.Count.ShouldBe(_restRoutes.Length);
    }

    [Fact]
    public void McpTools_EveryToolTakingACardCommentOrAttachmentById_IsCovered()
    {
        // Arrange
        string[] idArguments = ["cardId", "cardIds", "commentId", "attachmentId"];
        var tools = _factory.Services.GetServices<McpServerTool>()
            .Where(tool => tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
                && idArguments.Any(argument => properties.TryGetProperty(argument, out _)))
                .Select(tool => tool.ProtocolTool.Name)
                    .ToHashSet(StringComparer.Ordinal);

        // Act
        var untested = tools.Except(_mcpTools, StringComparer.Ordinal).ToList();

        // Assert
        untested.ShouldBeEmpty();
        tools.Count.ShouldBe(_mcpTools.Length);
    }

    private async Task<HttpResponseMessage> SendRestAsync(string route, SeededDraft draft)
    {
        var separator = route.IndexOf(' ', StringComparison.Ordinal);
        var method = route[..separator];
        var pattern = route[(separator + 1)..];

        var id = pattern switch
        {
            _ when pattern.StartsWith("/api/v1/comments/", StringComparison.Ordinal) => draft.CommentId,
            _ when pattern.StartsWith("/api/v1/attachments/", StringComparison.Ordinal) => draft.AttachmentId,
            _ => draft.CardId,
        };

        var path = pattern
            .Replace("{id:guid}", id.ToString(), StringComparison.Ordinal)
            .Replace("{labelId:guid}", draft.LabelOnDraft.ToString(), StringComparison.Ordinal);

        HttpContent? content = route switch
        {
            "PATCH /api/v1/cards/{id:guid}" => JsonContent.Create(new { name = "Changed by someone else" }),
            "POST /api/v1/cards/{id:guid}/reorder" => JsonContent.Create(new { laneId = draft.OtherLaneId, index = 0 }),
            "POST /api/v1/cards/{id:guid}/restore" => JsonContent.Create(new { laneId = draft.OtherLaneId }),
            "POST /api/v1/cards/{id:guid}/labels" => JsonContent.Create(new { labelId = draft.LabelOffDraft }),
            "POST /api/v1/cards/{id:guid}/comments" => JsonContent.Create(new { contentMarkdown = "Someone else's comment" }),
            "PATCH /api/v1/comments/{id:guid}" => JsonContent.Create(new { contentMarkdown = "Someone else's edit" }),
            "POST /api/v1/cards/{id:guid}/attachments" => AttachmentContent("someone-else.bin"),
            _ => null,
        };

        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
        return await _client.SendAsync(request);
    }

    private async Task<string> CallToolAsync(string tool, SeededDraft draft, string authKey, bool byNumberZero)
    {
        Guid? cardId = byNumberZero ? null : draft.CardId;
        long? cardNumber = byNumberZero ? 0 : null;
        Guid? boardId = byNumberZero ? draft.BoardId : null;

        return tool switch
        {
            "get_card" => await Tool<CardTools>().GetCardAsync(authKey, cardId, cardNumber, boardId, commentsLimit: 10),
            "move_card" => await Tool<CardTools>().MoveCardAsync(authKey, draft.OtherLaneId, cardId, cardNumber, index: 0, boardId: boardId),
            "update_card" => await Tool<CardTools>().UpdateCardAsync(authKey, cardId, cardNumber, name: "Changed by someone else", boardId: boardId),
            "archive_card" => await Tool<ArchiveTools>().ArchiveCardAsync(authKey, cardId, cardNumber, boardId),
            "restore_card" => await Tool<ArchiveTools>().RestoreCardAsync(authKey, draft.OtherLaneId, cardId, cardNumber, boardId),
            "duplicate_card" => await Tool<DuplicateCardTools>().DuplicateCardAsync(authKey, cardId, cardNumber, boardId, laneId: draft.OtherLaneId),
            "get_card_history" => await Tool<HistoryTools>().GetCardHistoryAsync(authKey, cardId, cardNumber, boardId),
            "add_comment" => await Tool<CommentTools>().AddCommentAsync(authKey, "Someone else's comment", cardId, cardNumber, boardId),
            "update_comment" => await Tool<CommentTools>().UpdateCommentAsync(authKey, draft.CommentId, "Someone else's edit"),
            "delete_comment" => await Tool<CommentTools>().DeleteCommentAsync(authKey, draft.CommentId),
            "upload_attachment" => await Tool<AttachmentTools>().UploadAttachmentAsync(authKey, "someone-else.bin", "AQID", cardId, cardNumber, boardId: boardId),
            "download_attachment" => await Tool<AttachmentTools>().DownloadAttachmentAsync(authKey, draft.AttachmentId),
            "delete_attachment" => await Tool<AttachmentTools>().DeleteAttachmentAsync(authKey, draft.AttachmentId),
            "add_label_to_card" => await Tool<LabelTools>().AddLabelToCardAsync(authKey, cardId, cardNumber, draft.LabelOffDraft, boardId: boardId),
            "remove_label_from_card" => await Tool<LabelTools>().RemoveLabelFromCardAsync(authKey, cardId, cardNumber, draft.LabelOnDraft, boardId: boardId),
            "bulk_archive_cards" => await Tool<BulkCardTools>().BulkArchiveCardsAsync(authKey, cardIds: draft.CardId.ToString()),
            "bulk_restore_cards" => await Tool<BulkCardTools>().BulkRestoreCardsAsync(authKey, draft.OtherLaneId, cardIds: draft.CardId.ToString()),
            "bulk_update_cards" => await Tool<BulkCardTools>().BulkUpdateCardsAsync(authKey, cardIds: draft.CardId.ToString(), laneId: draft.OtherLaneId),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "No call is set up for this tool."),
        };
    }

    private T Tool<T>() where T : class
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);

        return ActivatorUtilities.CreateInstance<T>(scope.ServiceProvider);
    }

    // A fresh board with two lanes and two labels. A HumanUser starts a draft in the first lane through
    // the create dialog's own calls, with one label, then comments on it and attaches a file to it.
    private async Task<SeededDraft> SeedDraftAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var creator = await TestAuthHelper.CreateUserAsync(_client, _factory, $"Draft creator {suffix}", UserRole.HumanUser);
        var other = await TestAuthHelper.CreateUserAsync(_client, _factory, $"Someone else {suffix}", UserRole.HumanUser);

        TestAuthHelper.SetAdminAuth(_client, _factory);
        var boardId = await PostForIdAsync("/api/v1/boards", new { name = $"Draft visibility {suffix}" });
        var lane = await PostForIdAsync($"/api/v1/boards/{boardId}/lanes", new { name = "Lane 1" });
        var otherLane = await PostForIdAsync($"/api/v1/boards/{boardId}/lanes", new { name = "Lane 2" });
        var labelOn = await PostForIdAsync($"/api/v1/boards/{boardId}/labels", new { name = "On", color = "#111111" });
        var labelOff = await PostForIdAsync($"/api/v1/boards/{boardId}/labels", new { name = "Off", color = "#222222" });

        TestAuthHelper.SetAuth(_client, creator.AuthKey);
        var cardId = await PostForIdAsync($"/api/v1/boards/{boardId}/cards/temp", new { name = "Draft", descriptionMarkdown = "Unsaved", laneId = lane, labelIds = new[] { labelOn } });
        var commentId = await PostForIdAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "The creator's note" });

        var upload = await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", AttachmentContent("creator.bin"));
        upload.EnsureSuccessStatusCode();
        var attachmentId = (await upload.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();

        return new SeededDraft(boardId, cardId, otherLane, labelOn, labelOff, commentId, attachmentId, creator.AuthKey, other.AuthKey, _factory.AdminAuthKey);
    }

    private async Task<Guid> PostForIdAsync(string path, object body)
    {
        var response = await _client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    // Everything someone else could have changed: the draft itself, its label, comment and
    // attachment, and the number of cards on the board (a duplicate would add one).
    private async Task<DraftState> ReadDraftAsync(SeededDraft draft)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var card = await db.Cards
            .AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == draft.CardId);

        var labels = await db.CardLabels
            .Where(cl => cl.CardId == draft.CardId)
                .Select(cl => cl.LabelId)
                    .ToListAsync();

        var comments = await db.Comments
            .Where(c => c.CardId == draft.CardId)
                .Select(c => c.ContentMarkdown)
                    .ToListAsync();

        var attachments = await db.Attachments.CountAsync(a => a.CardId == draft.CardId);
        var boardCards = await db.Cards.CountAsync(c => c.BoardId == draft.BoardId);

        return new DraftState
        (
            card is not null,
            card?.IsTemp ?? false,
            card?.Name,
            card?.DescriptionMarkdown,
            card?.LaneId,
            card?.Position,
            string.Join(',', labels.Order()),
            string.Join('|', comments.Order(StringComparer.Ordinal)),
            attachments,
            boardCards
        );
    }

    // Everything a caller can see of a response, with the ids it was sent for taken out: the status,
    // the headers present (and the deprecation link's value), and the body.
    private static async Task<string> DescribeAsync(HttpResponseMessage response, SeededDraft sentFor)
    {
        var headers = response.Headers
            .Concat(response.Content.Headers)
                .Select(header => string.Equals(header.Key, "Link", StringComparison.OrdinalIgnoreCase) ? $"Link={sentFor.Anonymize(string.Join(',', header.Value))}" : header.Key)
                .Order(StringComparer.Ordinal);

        return $"{response.StatusCode} [{string.Join(' ', headers)}] {sentFor.Anonymize(await response.Content.ReadAsStringAsync())}";
    }

    private static MultipartFormDataContent AttachmentContent(string fileName)
    {
        var bytes = new ByteArrayContent([1, 2, 3]);
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        return new MultipartFormDataContent { { bytes, "file", fileName } };
    }

    private static TheoryData<string, string> Cross(string[] surfaces, string[] callers)
    {
        TheoryData<string, string> data = [];
        foreach (var surface in surfaces)
        {
            foreach (var caller in callers)
            {
                data.Add(surface, caller);
            }
        }

        return data;
    }

    [GeneratedRegex(@"^/api/v\d+/(cards|comments|attachments)/\{id")]
    private static partial Regex IdRoute { get; }

    private sealed record SeededDraft
    (
        Guid BoardId,
        Guid CardId,
        Guid OtherLaneId,
        Guid LabelOnDraft,
        Guid LabelOffDraft,
        Guid CommentId,
        Guid AttachmentId,
        string CreatorKey,
        string OtherKey,
        string AdminKey
    )
    {
        public string KeyOf(string caller) => caller switch
        {
            "creator" => CreatorKey,
            "other" => OtherKey,
            "admin" => AdminKey,
            _ => throw new ArgumentOutOfRangeException(nameof(caller), caller, "Unknown caller."),
        };

        // The same board, with a card, comment and attachment id that name nothing.
        public SeededDraft WithAbsentIds() => this with { CardId = Guid.NewGuid(), CommentId = Guid.NewGuid(), AttachmentId = Guid.NewGuid() };

        public string Anonymize(string text) => text
            .Replace(CardId.ToString(), "<card>", StringComparison.OrdinalIgnoreCase)
            .Replace(CommentId.ToString(), "<comment>", StringComparison.OrdinalIgnoreCase)
            .Replace(AttachmentId.ToString(), "<attachment>", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record DraftState
    (
        bool Exists,
        bool IsTemp,
        string? Name,
        string? Description,
        Guid? LaneId,
        int? Position,
        string Labels,
        string Comments,
        int Attachments,
        int BoardCards
    );
}
