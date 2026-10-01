using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Catalog tests for the board family: board.created / board.renamed / board.deleted across
// REST + MCP (board delete is strict-admin-REST-only — no MCP parity). These are STRUCTURALLY NOVEL:
// board CRUD has no SSE broadcast today, so the family is WEBHOOK-ONLY — it enqueues straight to the
// sink and rings NO board bell, keeping the SSE wire byte-for-byte unchanged. The "no SSE bell" tests
// are the load-bearing new-path invariant. The CapturingWebhookSink IS the observable.
public sealed class WebhookBoardCatalogTests : IClassFixture<WebhookTestFactory>, IDisposable
{
    private readonly WebhookTestFactory _factory;
    private readonly HttpClient _client;
    private readonly List<IServiceScope> _scopes = [];

    public WebhookBoardCatalogTests(WebhookTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        // Board create/rename are admin-or-agent-admin; board delete is strict-admin. The seeded
        // admin satisfies all three.
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

    // ── board.created ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RestCreateBoard_FiresBoardCreated_WithBoardResource()
    {
        var sink = Sink;
        sink.Clear();

        var (id, slug, name) = await CreateBoardViaRestAsync("Created");

        sink.Captured.Select(e => e.EventType).ShouldBe(["board.created"]);
        var wire = Serialize(sink.Captured[0]);
        wire.GetProperty("boardId").GetGuid().ShouldBe(id);
        wire.GetProperty("boardSlug").GetString().ShouldBe(slug);

        var board = wire.GetProperty("data").GetProperty("board");
        board.GetProperty("id").GetGuid().ShouldBe(id);
        board.GetProperty("slug").GetString().ShouldBe(slug);
        board.GetProperty("name").GetString().ShouldBe(name);
    }

    [Fact]
    public async Task McpCreateBoard_FiresBoardCreated()
    {
        var sink = Sink;
        var tools = CreateBoardTools();
        var name = UniqueName("Mcp Created");
        sink.Clear();

        var result = await tools.CreateBoardAsync(CollatticeApiFactory.TestAdminAuthKey, name);
        result.ShouldNotContain("Error");
        var id = JsonDocument.Parse(result).RootElement.GetProperty("id").GetGuid();

        sink.Captured.Select(e => e.EventType).ShouldBe(["board.created"]);
        var board = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("board");
        board.GetProperty("id").GetGuid().ShouldBe(id);
        board.GetProperty("name").GetString().ShouldBe(name);
    }

    // ── board.renamed (slug immutable) ────────────────────────────────────────────

    [Fact]
    public async Task RestRenameBoard_FiresBoardRenamed_SlugUnchanged()
    {
        var sink = Sink;
        var (id, slug, _) = await CreateBoardViaRestAsync("Before");
        sink.Clear();

        var response = await _client.PatchAsJsonAsync($"/api/v1/boards/{id}", new { name = "After Rename" });
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["board.renamed"]);
        var board = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("board");
        board.GetProperty("name").GetString().ShouldBe("After Rename");
        board.GetProperty("slug").GetString().ShouldBe(slug);   // slug is immutable
    }

    [Fact]
    public async Task McpUpdateBoard_FiresBoardRenamed()
    {
        var sink = Sink;
        var tools = CreateBoardTools();
        var (id, _, _) = await CreateBoardViaRestAsync("Mcp Before");
        sink.Clear();

        (await tools.UpdateBoardAsync(CollatticeApiFactory.TestAdminAuthKey, id, "Mcp After")).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["board.renamed"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("board").GetProperty("name").GetString().ShouldBe("Mcp After");
    }

    [Fact]
    public async Task RestRenameBoard_SameName_EmitsNoWebhook()
    {
        var sink = Sink;
        var (id, _, name) = await CreateBoardViaRestAsync("Steady");
        sink.Clear();

        var response = await _client.PatchAsJsonAsync($"/api/v1/boards/{id}", new { name });
        response.EnsureSuccessStatusCode();

        // No-op guard: re-sending the identical name emits nothing (no bell to preserve — board CRUD
        // never rang one).
        sink.Captured.ShouldBeEmpty();
    }

    // ── board.deleted (strict-admin REST only — no MCP parity) ────────────────────

    [Fact]
    public async Task RestDeleteBoard_FiresBoardDeleted_FromCapturedState()
    {
        var sink = Sink;
        // An API-created board has only the hidden archive lane, so it is immediately deletable.
        var (id, slug, name) = await CreateBoardViaRestAsync("Doomed");
        sink.Clear();

        var response = await _client.DeleteAsync($"/api/v1/boards/{id}");
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["board.deleted"]);
        var board = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("board");
        board.GetProperty("id").GetGuid().ShouldBe(id);
        board.GetProperty("slug").GetString().ShouldBe(slug);
        board.GetProperty("name").GetString().ShouldBe(name);
    }

    // ── WEBHOOK-ONLY: board events ring NO SSE bell (the new-path invariant) ───────

    [Fact]
    public async Task RestRenameBoard_RingsNoSseBell_ButFiresWebhook()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var (id, _, _) = await CreateBoardViaRestAsync("Silent Rename");
        sink.Clear();

        var reader = broadcaster.Subscribe(id);
        try
        {
            var response = await _client.PatchAsJsonAsync($"/api/v1/boards/{id}", new { name = "Renamed Silently" });
            response.EnsureSuccessStatusCode();

            // The webhook fired…
            sink.Captured.Select(e => e.EventType).ShouldBe(["board.renamed"]);

            // …but NO SSE bell rang. Board CRUD has no SSE broadcast — the wire is byte-for-byte
            // unchanged (zero signals), which is the whole reason board events are webhook-only.
            DrainChannel(reader).ShouldBeEmpty();
        }
        finally
        {
            broadcaster.Unsubscribe(id, reader);
        }
    }

    [Fact]
    public async Task RestDeleteBoard_RingsNoSseBell_ButFiresWebhook()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var (id, _, _) = await CreateBoardViaRestAsync("Silent Delete");
        sink.Clear();

        var reader = broadcaster.Subscribe(id);
        try
        {
            var response = await _client.DeleteAsync($"/api/v1/boards/{id}");
            response.EnsureSuccessStatusCode();

            sink.Captured.Select(e => e.EventType).ShouldBe(["board.deleted"]);
            DrainChannel(reader).ShouldBeEmpty();
        }
        finally
        {
            broadcaster.Unsubscribe(id, reader);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private BoardTools CreateBoardTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return new BoardTools(db, new McpAuthService(new UserResolver(db)), _factory.Sink);
    }

    private static string UniqueName(string baseName) => $"{baseName} {Guid.NewGuid():N}";

    private async Task<(Guid Id, string Slug, string Name)> CreateBoardViaRestAsync(string baseName)
    {
        var name = UniqueName(baseName);
        var response = await _client.PostAsJsonAsync("/api/v1/boards", new { name });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return (json.GetProperty("id").GetGuid(), json.GetProperty("slug").GetString()!, name);
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
