using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Catalog tests for the resource-lifecycle families: comment.* / label.* / attachment.*
// across REST + MCP. Each family rings the same single SSE board bell it always did PLUS one
// webhook event — so the SSE wire stays byte-for-byte unchanged while the catalog grows. The
// CapturingWebhookSink IS the observable (no HTTP delivery here), alongside the
// SSE-byte-equivalence safety property.
public sealed class WebhookResourceCatalogTests : IClassFixture<WebhookTestFactory>, IDisposable
{
    private readonly WebhookTestFactory _factory;
    private readonly HttpClient _client;
    private readonly List<IServiceScope> _scopes = [];

    public WebhookResourceCatalogTests(WebhookTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        // Every REST mutation in this fixture acts as the seeded admin (label CRUD needs
        // admin-or-agent-admin; comment / attachment need any authenticated user).
        TestAuthHelper.SetAdminAuth(_client, _factory);
    }

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }
    }

    private CapturingWebhookSink Sink
    {
        get
        {
            _factory.Sink.Clear();
            return _factory.Sink;
        }
    }

    // ── comment.created / .updated / .deleted ────────────────────────────────────

    [Fact]
    public async Task RestAddComment_FiresCommentCreated_WithCardRefAndAuthor()
    {
        var sink = Sink;
        var (cardId, cardNumber) = await CreateCardAsync("Commentable");
        sink.Clear();

        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "first note" });
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["comment.created"]);
        var wire = Serialize(sink.Captured[0]);
        var comment = wire.GetProperty("data").GetProperty("comment");
        comment.GetProperty("contentMarkdown").GetString().ShouldBe("first note");
        comment.GetProperty("cardId").GetGuid().ShouldBe(cardId);
        comment.GetProperty("cardNumber").GetInt64().ShouldBe(cardNumber);
        comment.GetProperty("authorName").GetString().ShouldNotBeNullOrEmpty();
        // The author is the acting user for a self-authored comment, so the two agree here.
        comment.GetProperty("authorName").GetString().ShouldBe(wire.GetProperty("actor").GetProperty("name").GetString());

        var card = wire.GetProperty("data").GetProperty("card");
        card.GetProperty("id").GetGuid().ShouldBe(cardId);
        card.GetProperty("number").GetInt64().ShouldBe(cardNumber);
    }

    [Fact]
    public async Task RestUpdateComment_FiresCommentUpdated()
    {
        var sink = Sink;
        var (cardId, _) = await CreateCardAsync("Editable comment");
        var commentId = await AddCommentViaRestAsync(cardId, "before edit");
        sink.Clear();

        var response = await _client.PatchAsJsonAsync($"/api/v1/comments/{commentId}", new { contentMarkdown = "after edit" });
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["comment.updated"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("comment").GetProperty("contentMarkdown").GetString().ShouldBe("after edit");
    }

    [Fact]
    public async Task RestDeleteComment_FiresCommentDeleted_FromCapturedState()
    {
        var sink = Sink;
        var (cardId, _) = await CreateCardAsync("Deletable comment");
        var commentId = await AddCommentViaRestAsync(cardId, "doomed");
        sink.Clear();

        var response = await _client.DeleteAsync($"/api/v1/comments/{commentId}");
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["comment.deleted"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("comment").GetProperty("id").GetGuid().ShouldBe(commentId);
    }

    [Fact]
    public async Task McpAddComment_FiresCommentCreated()
    {
        var sink = Sink;
        var (cardId, _) = await CreateCardAsync("Mcp Commentable");
        var tools = CreateCommentTools();
        sink.Clear();

        (await tools.AddCommentAsync(CollatticeApiFactory.TestAdminAuthKey, "mcp note", cardId: cardId)).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["comment.created"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("comment").GetProperty("contentMarkdown").GetString().ShouldBe("mcp note");
    }

    [Fact]
    public async Task McpUpdateComment_FiresCommentUpdated()
    {
        var sink = Sink;
        var (cardId, _) = await CreateCardAsync("Mcp Editable");
        var tools = CreateCommentTools();
        var addResult = await tools.AddCommentAsync(CollatticeApiFactory.TestAdminAuthKey, "v1", cardId: cardId);
        var commentId = JsonDocument.Parse(addResult).RootElement.GetProperty("id").GetGuid();
        sink.Clear();

        (await tools.UpdateCommentAsync(CollatticeApiFactory.TestAdminAuthKey, commentId, contentMarkdown: "v2")).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["comment.updated"]);
    }

    [Fact]
    public async Task McpDeleteComment_FiresCommentDeleted()
    {
        var sink = Sink;
        var (cardId, _) = await CreateCardAsync("Mcp Deletable");
        var tools = CreateCommentTools();
        var addResult = await tools.AddCommentAsync(CollatticeApiFactory.TestAdminAuthKey, "bye", cardId: cardId);
        var commentId = JsonDocument.Parse(addResult).RootElement.GetProperty("id").GetGuid();
        sink.Clear();

        (await tools.DeleteCommentAsync(CollatticeApiFactory.TestAdminAuthKey, commentId)).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["comment.deleted"]);
    }

    [Fact]
    public async Task AddComment_RingsExactlyOneSseBell()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var (cardId, _) = await CreateCardAsync("Bell comment");
        sink.Clear();

        var reader = broadcaster.Subscribe(_factory.DefaultBoardId);
        try
        {
            var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "ding" });
            response.EnsureSuccessStatusCode();

            sink.Captured.Count.ShouldBe(1);
            var signals = DrainChannel(reader);
            signals.Count.ShouldBe(1);
            signals[0].ShouldBe("board-updated");
        }
        finally
        {
            broadcaster.Unsubscribe(_factory.DefaultBoardId, reader);
        }
    }

    // ── label.created / .updated / .deleted (resource lifecycle) ──────────────────

    [Fact]
    public async Task RestCreateLabel_FiresLabelCreated_WithLabelResource()
    {
        var sink = Sink;
        sink.Clear();

        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels", new { name = "urgent", color = "#ff0000" });
        response.EnsureSuccessStatusCode();
        var labelId = (await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();

        sink.Captured.Select(e => e.EventType).ShouldBe(["label.created"]);
        var label = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("label");
        label.GetProperty("id").GetGuid().ShouldBe(labelId);
        label.GetProperty("boardId").GetGuid().ShouldBe(_factory.DefaultBoardId);
        label.GetProperty("name").GetString().ShouldBe("urgent");
        label.GetProperty("color").GetString().ShouldBe("#ff0000");
    }

    [Fact]
    public async Task RestUpdateLabel_FiresLabelUpdated()
    {
        var sink = Sink;
        var labelId = await CreateLabelViaRestAsync("rename-me", "#111111");
        sink.Clear();

        var response = await _client.PatchAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels/{labelId}", new { name = "renamed", color = "#222222" });
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["label.updated"]);
        var label = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("label");
        label.GetProperty("name").GetString().ShouldBe("renamed");
        label.GetProperty("color").GetString().ShouldBe("#222222");
    }

    [Fact]
    public async Task RestDeleteLabel_FiresLabelDeleted_FromCapturedState()
    {
        var sink = Sink;
        var labelId = await CreateLabelViaRestAsync("delete-me", "#333333");
        sink.Clear();

        var response = await _client.DeleteAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels/{labelId}");
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["label.deleted"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("label").GetProperty("id").GetGuid().ShouldBe(labelId);
    }

    [Fact]
    public async Task McpCreateLabel_FiresLabelCreated()
    {
        var sink = Sink;
        var tools = CreateLabelTools();
        sink.Clear();

        var result = await tools.CreateLabelAsync(CollatticeApiFactory.TestAdminAuthKey, _factory.DefaultBoardId, "mcp-label", "#0000ff");
        result.ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["label.created"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("label").GetProperty("name").GetString().ShouldBe("mcp-label");
    }

    [Fact]
    public async Task McpUpdateLabel_FiresLabelUpdated()
    {
        var sink = Sink;
        var tools = CreateLabelTools();
        var created = await tools.CreateLabelAsync(CollatticeApiFactory.TestAdminAuthKey, _factory.DefaultBoardId, "mcp-edit", "#444444");
        var labelId = JsonDocument.Parse(created).RootElement.GetProperty("id").GetGuid();
        sink.Clear();

        (await tools.UpdateLabelAsync(CollatticeApiFactory.TestAdminAuthKey, labelId, name: "mcp-edited")).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["label.updated"]);
    }

    [Fact]
    public async Task McpDeleteLabel_FiresLabelDeleted()
    {
        var sink = Sink;
        var tools = CreateLabelTools();
        var created = await tools.CreateLabelAsync(CollatticeApiFactory.TestAdminAuthKey, _factory.DefaultBoardId, "mcp-del", "#555555");
        var labelId = JsonDocument.Parse(created).RootElement.GetProperty("id").GetGuid();
        sink.Clear();

        (await tools.DeleteLabelAsync(CollatticeApiFactory.TestAdminAuthKey, labelId)).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["label.deleted"]);
    }

    [Fact]
    public async Task CreateLabel_RingsExactlyOneSseBell()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        sink.Clear();

        var reader = broadcaster.Subscribe(_factory.DefaultBoardId);
        try
        {
            var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels", new { name = "bell-label", color = "#abcdef" });
            response.EnsureSuccessStatusCode();

            sink.Captured.Count.ShouldBe(1);
            var signals = DrainChannel(reader);
            signals.Count.ShouldBe(1);
            signals[0].ShouldBe("board-updated");
        }
        finally
        {
            broadcaster.Unsubscribe(_factory.DefaultBoardId, reader);
        }
    }

    // ── attachment.created / .deleted (metadata only — never bytes) ───────────────

    [Fact]
    public async Task RestUploadAttachment_FiresAttachmentCreated_MetadataOnly()
    {
        var sink = Sink;
        var (cardId, cardNumber) = await CreateCardAsync("Attachable");
        sink.Clear();

        var attachmentId = await UploadAttachmentViaRestAsync(cardId, "note.txt", "text/plain", "hello bytes");

        sink.Captured.Select(e => e.EventType).ShouldBe(["attachment.created"]);
        var data = Serialize(sink.Captured[0]).GetProperty("data");
        var attachment = data.GetProperty("attachment");
        attachment.GetProperty("id").GetGuid().ShouldBe(attachmentId);
        attachment.GetProperty("cardId").GetGuid().ShouldBe(cardId);
        attachment.GetProperty("fileName").GetString().ShouldBe("note.txt");
        attachment.GetProperty("contentType").GetString().ShouldBe("text/plain");
        attachment.GetProperty("sizeBytes").GetInt64().ShouldBe(Encoding.UTF8.GetByteCount("hello bytes"));
        data.GetProperty("card").GetProperty("number").GetInt64().ShouldBe(cardNumber);
    }

    [Fact]
    public async Task AttachmentEvent_CarriesNoFileBytes()
    {
        var sink = Sink;
        var (cardId, _) = await CreateCardAsync("No-bytes attach");
        sink.Clear();

        await UploadAttachmentViaRestAsync(cardId, "secret.bin", "application/octet-stream", "raw payload");

        // Metadata only: the wire must never carry the file bytes under any key.
        var attachment = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("attachment");
        attachment.TryGetProperty("payload", out _).ShouldBeFalse();
        attachment.TryGetProperty("base64Content", out _).ShouldBeFalse();
        attachment.TryGetProperty("content", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task RestDeleteAttachment_FiresAttachmentDeleted_FromCapturedState()
    {
        var sink = Sink;
        var (cardId, _) = await CreateCardAsync("Detachable");
        var attachmentId = await UploadAttachmentViaRestAsync(cardId, "drop.txt", "text/plain", "remove me");
        sink.Clear();

        var response = await _client.DeleteAsync($"/api/v1/attachments/{attachmentId}");
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["attachment.deleted"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("attachment").GetProperty("id").GetGuid().ShouldBe(attachmentId);
    }

    [Fact]
    public async Task McpUploadAttachment_FiresAttachmentCreated()
    {
        var sink = Sink;
        var (cardId, _) = await CreateCardAsync("Mcp Attachable");
        var tools = CreateAttachmentTools();
        sink.Clear();

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("mcp bytes"));
        var result = await tools.UploadAttachmentAsync(CollatticeApiFactory.TestAdminAuthKey, "mcp.txt", base64, cardId: cardId, contentType: "text/plain");
        result.ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["attachment.created"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("attachment").GetProperty("fileName").GetString().ShouldBe("mcp.txt");
    }

    [Fact]
    public async Task McpDeleteAttachment_FiresAttachmentDeleted()
    {
        var sink = Sink;
        var (cardId, _) = await CreateCardAsync("Mcp Detachable");
        var tools = CreateAttachmentTools();
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("gone"));
        var uploadResult = await tools.UploadAttachmentAsync(CollatticeApiFactory.TestAdminAuthKey, "gone.txt", base64, cardId: cardId, contentType: "text/plain");
        var attachmentId = JsonDocument.Parse(uploadResult).RootElement.GetProperty("id").GetGuid();
        sink.Clear();

        (await tools.DeleteAttachmentAsync(CollatticeApiFactory.TestAdminAuthKey, attachmentId)).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["attachment.deleted"]);
    }

    [Fact]
    public async Task UploadAttachment_RingsExactlyOneSseBell()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var (cardId, _) = await CreateCardAsync("Bell attach");
        sink.Clear();

        var reader = broadcaster.Subscribe(_factory.DefaultBoardId);
        try
        {
            await UploadAttachmentViaRestAsync(cardId, "bell.txt", "text/plain", "ring");

            sink.Captured.Count.ShouldBe(1);
            var signals = DrainChannel(reader);
            signals.Count.ShouldBe(1);
            signals[0].ShouldBe("board-updated");
        }
        finally
        {
            broadcaster.Unsubscribe(_factory.DefaultBoardId, reader);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private CommentTools CreateCommentTools()
    {
        var db = NewScopedDb();
        return new CommentTools(db, new McpAuthService(new UserResolver(db)), Broadcaster());
    }

    private LabelTools CreateLabelTools()
    {
        var db = NewScopedDb();
        return new LabelTools(db, new McpAuthService(new UserResolver(db)), Broadcaster());
    }

    private AttachmentTools CreateAttachmentTools()
    {
        var db = NewScopedDb();
        return new AttachmentTools(db, new McpAuthService(new UserResolver(db)), Broadcaster(), _factory.Services.GetRequiredService<IOptions<AttachmentSettings>>());
    }

    private BoardDbContext NewScopedDb()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<BoardDbContext>();
    }

    private BoardEventBroadcaster Broadcaster() => _factory.Services.GetRequiredService<BoardEventBroadcaster>();

    private async Task<Guid> GetFirstLaneAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.Lanes
            .Where(l => l.BoardId == _factory.DefaultBoardId && !l.IsArchiveLane)
            .OrderBy(l => l.Position)
                .Select(l => l.Id)
                    .FirstAsync();
    }

    private async Task<(Guid Id, long Number)> CreateCardAsync(string name)
    {
        var laneId = await GetFirstLaneAsync();
        var db = NewScopedDb();
        var tools = new CardTools(db, new McpAuthService(new UserResolver(db)), Broadcaster());
        var result = await tools.CreateCardAsync(CollatticeApiFactory.TestAdminAuthKey, name, laneId);
        result.ShouldNotContain("Error");
        var root = JsonDocument.Parse(result).RootElement;
        return (root.GetProperty("id").GetGuid(), root.GetProperty("number").GetInt64());
    }

    private async Task<Guid> AddCommentViaRestAsync(Guid cardId, string text)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = text });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateLabelViaRestAsync(string name, string color)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels", new { name, color });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    private async Task<Guid> UploadAttachmentViaRestAsync(Guid cardId, string fileName, string contentType, string body)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);

        var response = await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", form);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    private static JsonElement Serialize(BoardEvent boardEvent)
    {
        var json = JsonSerializer.Serialize(boardEvent, JsonSerializerOptions.Web);
        return JsonDocument.Parse(json).RootElement;
    }

    private static List<string> DrainChannel(System.Threading.Channels.ChannelReader<string> reader)
    {
        var items = new List<string>();
        while (reader.TryRead(out var item))
        {
            items.Add(item);
        }

        return items;
    }
}
