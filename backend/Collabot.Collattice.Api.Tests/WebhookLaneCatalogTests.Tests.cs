using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Catalog tests for the lane family: lane.created / lane.renamed / lane.reordered /
// lane.deleted across REST + MCP. update_lane splits by axis (name → renamed, position → reordered)
// and co-fires both through ONE SSE bell; reorder_lanes raises exactly ONE lane.reordered carrying
// the board's FULL new left-to-right order (the one-bell reorder coalesce contract). Each site rings the
// same single SSE bell it always did, so the SSE wire stays byte-for-byte unchanged. The
// CapturingWebhookSink IS the observable (no HTTP delivery here).
// Sealed so the plain IDisposable shape below is complete; no derived class can add state to dispose.
public sealed class WebhookLaneCatalogTests : IClassFixture<WebhookTestFactory>, IDisposable
{
    private readonly WebhookTestFactory _factory;
    private readonly HttpClient _client;
    private readonly List<IServiceScope> _scopes = [];

    public WebhookLaneCatalogTests(WebhookTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        // Lane CRUD is admin-or-agent-admin; the seeded admin acts for every REST mutation here.
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

    // ── lane.created ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RestCreateLane_FiresLaneCreated_WithLaneResource()
    {
        var sink = Sink;
        var position = await NextFreePositionAsync();
        sink.Clear();

        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", new { name = "Review", position });
        response.EnsureSuccessStatusCode();
        var laneId = (await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();

        sink.Captured.Select(e => e.EventType).ShouldBe(["lane.created"]);
        var lane = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("lane");
        lane.GetProperty("id").GetGuid().ShouldBe(laneId);
        lane.GetProperty("boardId").GetGuid().ShouldBe(_factory.DefaultBoardId);
        lane.GetProperty("name").GetString().ShouldBe("Review");
        lane.GetProperty("position").GetInt32().ShouldBe(position);
    }

    [Fact]
    public async Task CreateLane_OverMcp_FiresLaneCreated()
    {
        var sink = Sink;
        var tools = CreateLaneTools();
        var position = await NextFreePositionAsync();
        sink.Clear();

        var result = await tools.CreateLaneAsync(CollatticeApiFactory.TestAdminAuthKey, _factory.DefaultBoardId, "Mcp Lane", position);
        result.ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["lane.created"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("lane").GetProperty("name").GetString().ShouldBe("Mcp Lane");
    }

    // ── lane.renamed (name axis) ──────────────────────────────────────────────────

    [Fact]
    public async Task UpdateLaneName_OverRest_FiresLaneRenamed()
    {
        var sink = Sink;
        var laneId = await CreateLaneAsync("rename-me");
        sink.Clear();

        var response = await _client.PatchAsJsonAsync($"/api/v1/lanes/{laneId}", new { name = "renamed" });
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["lane.renamed"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("lane").GetProperty("name").GetString().ShouldBe("renamed");
    }

    [Fact]
    public async Task UpdateLaneName_OverMcp_FiresLaneRenamed()
    {
        var sink = Sink;
        var tools = CreateLaneTools();
        var laneId = await CreateLaneAsync("mcp-rename-me");
        sink.Clear();

        (await tools.UpdateLaneAsync(CollatticeApiFactory.TestAdminAuthKey, laneId, name: "mcp-renamed")).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["lane.renamed"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("lane").GetProperty("name").GetString().ShouldBe("mcp-renamed");
    }

    // ── lane.reordered (position axis) — single-lane move carries the FULL new order ─

    [Fact]
    public async Task RestUpdateLanePosition_FiresLaneReordered_WithFullNewOrder()
    {
        var sink = Sink;
        var laneId = await CreateLaneAsync("mover");
        var target = await NextFreePositionAsync();   // a free, highest position → the lane moves to the end
        sink.Clear();

        var response = await _client.PatchAsJsonAsync($"/api/v1/lanes/{laneId}", new { position = target });
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["lane.reordered"]);
        var lanes = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("lanes");

        // The event carries the board's full new left-to-right order (every non-archive lane),
        // and the moved lane now sits last (its position is the highest).
        var expected = await NonArchiveLaneIdsInOrderAsync();
        var actual = lanes.EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList();
        actual.ShouldBe(expected);
        actual[^1].ShouldBe(laneId);
    }

    [Fact]
    public async Task McpUpdateLanePosition_FiresLaneReordered_WithFullNewOrder()
    {
        var sink = Sink;
        var tools = CreateLaneTools();
        var laneId = await CreateLaneAsync("mcp-mover");
        var target = await NextFreePositionAsync();
        sink.Clear();

        (await tools.UpdateLaneAsync(CollatticeApiFactory.TestAdminAuthKey, laneId, position: target)).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["lane.reordered"]);
        var actual = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("lanes")
            .EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList();
        actual.ShouldBe(await NonArchiveLaneIdsInOrderAsync());
    }

    // ── update_lane co-fire (name + position) — two events, ONE bell ──────────────

    [Fact]
    public async Task RestUpdateLaneNameAndPosition_CoFiresRenamedAndReordered_OneSseBell()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var laneId = await CreateLaneAsync("cofire");
        var target = await NextFreePositionAsync();
        sink.Clear();

        var reader = broadcaster.Subscribe(_factory.DefaultBoardId);
        try
        {
            var response = await _client.PatchAsJsonAsync($"/api/v1/lanes/{laneId}", new { name = "cofired", position = target });
            response.EnsureSuccessStatusCode();

            // One event per changed axis, in name-then-position order.
            sink.Captured.Select(e => e.EventType).ShouldBe(["lane.renamed", "lane.reordered"]);

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
    public async Task UpdateLaneNameAndPosition_OverMcp_CoFiresRenamedAndReordered()
    {
        var sink = Sink;
        var tools = CreateLaneTools();
        var laneId = await CreateLaneAsync("mcp-cofire");
        var target = await NextFreePositionAsync();
        sink.Clear();

        (await tools.UpdateLaneAsync(CollatticeApiFactory.TestAdminAuthKey, laneId, name: "mcp-cofired", position: target)).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["lane.renamed", "lane.reordered"]);
    }

    [Fact]
    public async Task UpdateLaneNoChange_EmitsNoWebhook_StillRingsOneBell()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var laneId = await CreateLaneAsync("steady");
        sink.Clear();

        var reader = broadcaster.Subscribe(_factory.DefaultBoardId);
        try
        {
            // Re-send the identical name — the per-axis no-op guard suppresses lane.renamed.
            var response = await _client.PatchAsJsonAsync($"/api/v1/lanes/{laneId}", new { name = "steady" });
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
    public async Task UpdateLaneSamePosition_EmitsNoLaneReordered_OnEitherSurface()
    {
        var sink = Sink;
        var tools = CreateLaneTools();
        var laneId = await CreateLaneAsync("parked");
        var position = await LanePositionAsync(laneId);
        sink.Clear();

        // Re-send the lane's own position — the per-axis no-op guard suppresses lane.reordered on
        // REST and MCP alike (a changed position is covered by the reorder tests above).
        var response = await _client.PatchAsJsonAsync($"/api/v1/lanes/{laneId}", new { position });
        response.EnsureSuccessStatusCode();

        (await tools.UpdateLaneAsync(CollatticeApiFactory.TestAdminAuthKey, laneId, position: position)).ShouldNotContain("Error");

        sink.Captured.ShouldBeEmpty();
    }

    // ── lane.reordered (bulk reorder) — exactly ONE event, the FULL new order ──────

    [Fact]
    public async Task RestReorderLanes_FiresOneLaneReordered_FullNewOrder()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var current = await NonArchiveLaneIdsInOrderAsync();
        var reversed = current.AsEnumerable().Reverse().ToList();
        sink.Clear();

        var reader = broadcaster.Subscribe(_factory.DefaultBoardId);
        try
        {
            var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes/reorder", new { laneIds = reversed });
            response.EnsureSuccessStatusCode();

            // ONE lane.reordered (never N), carrying the board's full new order in the requested
            // sequence, with dense positions 0..n-1 assigned by the server.
            sink.Captured.Select(e => e.EventType).ShouldBe(["lane.reordered"]);
            var lanes = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("lanes");
            lanes.EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList().ShouldBe(reversed);
            lanes.EnumerateArray().Select(l => l.GetProperty("position").GetInt32()).ToList().ShouldBe([.. Enumerable.Range(0, reversed.Count)]);

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
    public async Task McpReorderLanes_FiresOneLaneReordered_FullNewOrder()
    {
        var sink = Sink;
        var tools = CreateLaneTools();
        var current = await NonArchiveLaneIdsInOrderAsync();
        var reversed = current.AsEnumerable().Reverse().ToList();
        sink.Clear();

        var csv = string.Join(',', reversed);
        (await tools.ReorderLanesAsync(CollatticeApiFactory.TestAdminAuthKey, _factory.DefaultBoardId, csv)).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["lane.reordered"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("lanes")
            .EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList().ShouldBe(reversed);
    }

    // ── lane.deleted ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RestDeleteLane_FiresLaneDeleted_FromCapturedState()
    {
        var sink = Sink;
        var laneId = await CreateLaneAsync("delete-me");
        sink.Clear();

        var response = await _client.DeleteAsync($"/api/v1/lanes/{laneId}");
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["lane.deleted"]);
        var lane = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("lane");
        lane.GetProperty("id").GetGuid().ShouldBe(laneId);
        lane.GetProperty("name").GetString().ShouldBe("delete-me");
    }

    [Fact]
    public async Task DeleteLane_OverMcp_FiresLaneDeleted()
    {
        var sink = Sink;
        var tools = CreateLaneTools();
        var laneId = await CreateLaneAsync("mcp-delete-me");
        sink.Clear();

        (await tools.DeleteLaneAsync(CollatticeApiFactory.TestAdminAuthKey, laneId)).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["lane.deleted"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("lane").GetProperty("id").GetGuid().ShouldBe(laneId);
    }

    [Fact]
    public async Task CreateLane_SubscribedToBoardStream_RingsExactlyOneSseBell()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var position = await NextFreePositionAsync();
        sink.Clear();

        var reader = broadcaster.Subscribe(_factory.DefaultBoardId);
        try
        {
            var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", new { name = "bell-lane", position });
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

    private LaneTools CreateLaneTools()
    {
        var db = NewScopedDb();
        return new LaneTools(db, new McpAuthService(new UserResolver(db)), Broadcaster());
    }

    private BoardDbContext NewScopedDb()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<BoardDbContext>();
    }

    private BoardEventBroadcaster Broadcaster() => _factory.Services.GetRequiredService<BoardEventBroadcaster>();

    private async Task<Guid> CreateLaneAsync(string name)
    {
        var position = await NextFreePositionAsync();
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", new { name, position });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    // A free position above every current non-archive lane — create-lane has no collision guard, so
    // the unique (BoardId, Position) index would 500 a colliding insert.
    private async Task<int> NextFreePositionAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var max = await db.Lanes
            .Where(l => l.BoardId == _factory.DefaultBoardId && !l.IsArchiveLane)
                .MaxAsync(l => (int?)l.Position);

        return (max ?? -1) + 1;
    }

    private async Task<int> LanePositionAsync(Guid laneId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        return await db.Lanes
            .Where(l => l.Id == laneId)
                .Select(l => l.Position)
                    .SingleAsync();
    }

    private async Task<List<Guid>> NonArchiveLaneIdsInOrderAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.Lanes
            .Where(l => l.BoardId == _factory.DefaultBoardId && !l.IsArchiveLane)
            .OrderBy(l => l.Position)
                .Select(l => l.Id)
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
