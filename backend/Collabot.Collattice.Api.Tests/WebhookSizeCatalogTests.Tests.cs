using System.Net.Http.Json;
using System.Text.Json;
using Collabot.Collattice.Api.Auth;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Mcp;
using Collabot.Collattice.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Catalog tests for the size family: size.created / size.renamed / size.reordered /
// size.deleted across REST + MCP. A structural twin of the lane catalog tests: update_size splits by
// axis (name → renamed, ordinal → reordered) and co-fires both through ONE SSE bell; reorder_sizes
// raises exactly ONE size.reordered carrying the board's FULL new order (the one-bell reorder coalesce
// contract). Each site rings the same single SSE bell it always did, so the SSE wire stays
// byte-for-byte unchanged. The CapturingWebhookSink IS the observable (no HTTP delivery here).
public sealed class WebhookSizeCatalogTests : IClassFixture<WebhookTestFactory>, IDisposable
{
    private readonly WebhookTestFactory _factory;
    private readonly HttpClient _client;
    private readonly List<IServiceScope> _scopes = [];

    public WebhookSizeCatalogTests(WebhookTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        // Size CRUD is admin-or-agent-admin; the seeded admin acts for every REST mutation here.
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

    // ── size.created ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RestCreateSize_FiresSizeCreated_WithSizeResource()
    {
        var sink = Sink;
        sink.Clear();

        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name = "XXL" });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var sizeId = created.GetProperty("id").GetGuid();

        sink.Captured.Select(e => e.EventType).ShouldBe(["size.created"]);
        var size = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("size");
        size.GetProperty("id").GetGuid().ShouldBe(sizeId);
        size.GetProperty("boardId").GetGuid().ShouldBe(_factory.DefaultBoardId);
        size.GetProperty("name").GetString().ShouldBe("XXL");
        size.GetProperty("ordinal").GetInt32().ShouldBe(created.GetProperty("ordinal").GetInt32());
    }

    [Fact]
    public async Task McpCreateSize_FiresSizeCreated()
    {
        var sink = Sink;
        var tools = CreateSizeTools();
        sink.Clear();

        var result = await tools.CreateSizeAsync(CollatticeApiFactory.TestAdminAuthKey, _factory.DefaultBoardId, "Mcp Size");
        result.ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["size.created"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("size").GetProperty("name").GetString().ShouldBe("Mcp Size");
    }

    // ── size.renamed (name axis) ──────────────────────────────────────────────────

    [Fact]
    public async Task RestUpdateSizeName_FiresSizeRenamed()
    {
        var sink = Sink;
        var sizeId = await CreateSizeAsync("rename-me");
        sink.Clear();

        var response = await _client.PatchAsJsonAsync($"/api/v1/sizes/{sizeId}", new { name = "renamed" });
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["size.renamed"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("size").GetProperty("name").GetString().ShouldBe("renamed");
    }

    [Fact]
    public async Task McpUpdateSizeName_FiresSizeRenamed()
    {
        var sink = Sink;
        var tools = CreateSizeTools();
        var sizeId = await CreateSizeAsync("mcp-rename-me");
        sink.Clear();

        (await tools.UpdateSizeAsync(CollatticeApiFactory.TestAdminAuthKey, sizeId, name: "mcp-renamed")).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["size.renamed"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("size").GetProperty("name").GetString().ShouldBe("mcp-renamed");
    }

    // ── size.reordered (ordinal axis) — single-size move carries the FULL new order ─

    [Fact]
    public async Task RestUpdateSizeOrdinal_FiresSizeReordered_WithFullNewOrder()
    {
        var sink = Sink;
        var sizeId = await CreateSizeAsync("mover");
        var target = await NextFreeOrdinalAsync();   // a free, highest ordinal → the size moves to the end
        sink.Clear();

        var response = await _client.PatchAsJsonAsync($"/api/v1/sizes/{sizeId}", new { ordinal = target });
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["size.reordered"]);
        var sizes = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("sizes");

        // The event carries the board's full new order (every size), and the moved size now sits last
        // (its ordinal is the highest).
        var expected = await SizeIdsInOrderAsync();
        var actual = sizes.EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList();
        actual.ShouldBe(expected);
        actual[^1].ShouldBe(sizeId);
    }

    [Fact]
    public async Task McpUpdateSizeOrdinal_FiresSizeReordered_WithFullNewOrder()
    {
        var sink = Sink;
        var tools = CreateSizeTools();
        var sizeId = await CreateSizeAsync("mcp-mover");
        var target = await NextFreeOrdinalAsync();
        sink.Clear();

        (await tools.UpdateSizeAsync(CollatticeApiFactory.TestAdminAuthKey, sizeId, ordinal: target)).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["size.reordered"]);
        var actual = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("sizes")
            .EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList();
        actual.ShouldBe(await SizeIdsInOrderAsync());
    }

    // ── update_size co-fire (name + ordinal) — two events, ONE bell ───────────────

    [Fact]
    public async Task RestUpdateSizeNameAndOrdinal_CoFiresRenamedAndReordered_OneSseBell()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var sizeId = await CreateSizeAsync("cofire");
        var target = await NextFreeOrdinalAsync();
        sink.Clear();

        var reader = broadcaster.Subscribe(_factory.DefaultBoardId);
        try
        {
            var response = await _client.PatchAsJsonAsync($"/api/v1/sizes/{sizeId}", new { name = "cofired", ordinal = target });
            response.EnsureSuccessStatusCode();

            // One event per changed axis, in name-then-ordinal order.
            sink.Captured.Select(e => e.EventType).ShouldBe(["size.renamed", "size.reordered"]);

            // …but EXACTLY ONE SSE bell (the byte-identical coalesce) for the two-axis change.
            var signals = DrainChannel(reader);
            signals.Count.ShouldBe(1);
            signals[0].ShouldBe("board-updated");
        }
        finally
        {
            broadcaster.Unsubscribe(_factory.DefaultBoardId, reader);
        }
    }

    [Fact]
    public async Task McpUpdateSizeNameAndOrdinal_CoFiresRenamedAndReordered()
    {
        var sink = Sink;
        var tools = CreateSizeTools();
        var sizeId = await CreateSizeAsync("mcp-cofire");
        var target = await NextFreeOrdinalAsync();
        sink.Clear();

        (await tools.UpdateSizeAsync(CollatticeApiFactory.TestAdminAuthKey, sizeId, name: "mcp-cofired", ordinal: target)).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["size.renamed", "size.reordered"]);
    }

    [Fact]
    public async Task UpdateSizeNoChange_EmitsNoWebhook_StillRingsOneBell()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var sizeId = await CreateSizeAsync("steady");
        sink.Clear();

        var reader = broadcaster.Subscribe(_factory.DefaultBoardId);
        try
        {
            // Re-send the identical name — the per-axis no-op guard suppresses size.renamed.
            var response = await _client.PatchAsJsonAsync($"/api/v1/sizes/{sizeId}", new { name = "steady" });
            response.EnsureSuccessStatusCode();

            sink.Captured.ShouldBeEmpty();

            // The SSE bell still rings exactly once (byte-identical) — preserving the prior
            // "every PATCH rings one bell" behaviour for an all-no-op edit.
            var signals = DrainChannel(reader);
            signals.Count.ShouldBe(1);
            signals[0].ShouldBe("board-updated");
        }
        finally
        {
            broadcaster.Unsubscribe(_factory.DefaultBoardId, reader);
        }
    }

    [Fact]
    public async Task UpdateSizeSameOrdinal_EmitsNoSizeReordered_OnEitherSurface()
    {
        var sink = Sink;
        var tools = CreateSizeTools();
        var sizeId = await CreateSizeAsync("parked");
        var ordinal = await SizeOrdinalAsync(sizeId);
        sink.Clear();

        // Re-send the size's own ordinal — the per-axis no-op guard suppresses size.reordered on REST
        // and MCP alike (a changed ordinal is covered by the reorder tests above).
        var response = await _client.PatchAsJsonAsync($"/api/v1/sizes/{sizeId}", new { ordinal });
        response.EnsureSuccessStatusCode();

        (await tools.UpdateSizeAsync(CollatticeApiFactory.TestAdminAuthKey, sizeId, ordinal: ordinal)).ShouldNotContain("Error");

        sink.Captured.ShouldBeEmpty();
    }

    // ── size.reordered (bulk reorder) — exactly ONE event, the FULL new order ──────

    [Fact]
    public async Task RestReorderSizes_FiresOneSizeReordered_FullNewOrder()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var current = await SizeIdsInOrderAsync();
        var reversed = current.AsEnumerable().Reverse().ToList();
        sink.Clear();

        var reader = broadcaster.Subscribe(_factory.DefaultBoardId);
        try
        {
            var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes/reorder", new { sizeIds = reversed });
            response.EnsureSuccessStatusCode();

            // ONE size.reordered (never N), carrying the board's full new order in the requested
            // sequence, with dense ordinals 0..n-1 assigned by the server.
            sink.Captured.Select(e => e.EventType).ShouldBe(["size.reordered"]);
            var sizes = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("sizes");
            sizes.EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList().ShouldBe(reversed);
            sizes.EnumerateArray().Select(s => s.GetProperty("ordinal").GetInt32()).ToList().ShouldBe([.. Enumerable.Range(0, reversed.Count)]);

            // Exactly one SSE bell for the whole reorder (the one-bell coalesce contract).
            var signals = DrainChannel(reader);
            signals.Count.ShouldBe(1);
            signals[0].ShouldBe("board-updated");
        }
        finally
        {
            broadcaster.Unsubscribe(_factory.DefaultBoardId, reader);
        }
    }

    [Fact]
    public async Task McpReorderSizes_FiresOneSizeReordered_FullNewOrder()
    {
        var sink = Sink;
        var tools = CreateSizeTools();
        var current = await SizeIdsInOrderAsync();
        var reversed = current.AsEnumerable().Reverse().ToList();
        sink.Clear();

        var csv = string.Join(',', reversed);
        (await tools.ReorderSizesAsync(CollatticeApiFactory.TestAdminAuthKey, _factory.DefaultBoardId, csv)).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["size.reordered"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("sizes")
            .EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList().ShouldBe(reversed);
    }

    // ── size.deleted ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RestDeleteSize_FiresSizeDeleted_FromCapturedState()
    {
        var sink = Sink;
        var sizeId = await CreateSizeAsync("delete-me");
        sink.Clear();

        var response = await _client.DeleteAsync($"/api/v1/sizes/{sizeId}");
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["size.deleted"]);
        var size = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("size");
        size.GetProperty("id").GetGuid().ShouldBe(sizeId);
        size.GetProperty("name").GetString().ShouldBe("delete-me");
    }

    [Fact]
    public async Task McpDeleteSize_FiresSizeDeleted()
    {
        var sink = Sink;
        var tools = CreateSizeTools();
        var sizeId = await CreateSizeAsync("mcp-delete-me");
        sink.Clear();

        (await tools.DeleteSizeAsync(CollatticeApiFactory.TestAdminAuthKey, sizeId)).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["size.deleted"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("size").GetProperty("id").GetGuid().ShouldBe(sizeId);
    }

    [Fact]
    public async Task CreateSize_RingsExactlyOneSseBell()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        sink.Clear();

        var reader = broadcaster.Subscribe(_factory.DefaultBoardId);
        try
        {
            var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name = "bell-size" });
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

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private SizeTools CreateSizeTools()
    {
        var db = NewScopedDb();
        return new SizeTools(db, new McpAuthService(new UserResolver(db)), Broadcaster());
    }

    private BoardDbContext NewScopedDb()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<BoardDbContext>();
    }

    private BoardEventBroadcaster Broadcaster() => _factory.Services.GetRequiredService<BoardEventBroadcaster>();

    // Create a size with an omitted ordinal — the server auto-assigns one above every current
    // ordinal, so the insert can't collide with the unique (BoardId, Ordinal) index.
    private async Task<Guid> CreateSizeAsync(string name)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    // A free ordinal above every current size — a PATCH to a taken ordinal is a 409, so moving a
    // size "to the end" targets the next ordinal past the current max.
    private async Task<int> NextFreeOrdinalAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var max = await db.CardSizes
            .Where(s => s.BoardId == _factory.DefaultBoardId)
                .MaxAsync(s => (int?)s.Ordinal);

        return (max ?? -1) + 1;
    }

    private async Task<int> SizeOrdinalAsync(Guid sizeId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        return await db.CardSizes
            .Where(s => s.Id == sizeId)
                .Select(s => s.Ordinal)
                    .SingleAsync();
    }

    private async Task<List<Guid>> SizeIdsInOrderAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.CardSizes
            .Where(s => s.BoardId == _factory.DefaultBoardId)
            .OrderBy(s => s.Ordinal)
                .Select(s => s.Id)
                    .ToListAsync();
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
