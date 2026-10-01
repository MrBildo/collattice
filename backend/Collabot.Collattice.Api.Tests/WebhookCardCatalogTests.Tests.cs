using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Catalog tests for the card family: card.updated / card.archived /
// card.restored / card.labeled / card.unlabeled across REST + MCP + bulk + prune, the
// multi-axis co-fire rule, the archive/restore-never-card.moved fence, and the
// one-SSE-bell coalesce for a co-fire. The CapturingWebhookSink IS the observable (no
// HTTP delivery here), alongside the SSE-byte-equivalence safety property.
// Sealed so the plain IDisposable shape below is complete; no derived class can add state to dispose.
public sealed class WebhookCardCatalogTests(WebhookTestFactory factory) : IClassFixture<WebhookTestFactory>, IDisposable
{
    private readonly WebhookTestFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly List<IServiceScope> _scopes = [];

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

    // ── card.updated — content-only change (name / description / size) ───────────

    [Fact]
    public async Task RestPatchNameOnly_FiresCardUpdated_NotCardMoved()
    {
        var sink = Sink;
        var (laneA, _) = await GetTwoLanesAsync();
        var cardId = await CreateCardInLaneViaRestAsync(laneA, "Before");
        sink.Clear();

        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { name = "After" });
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["card.updated"]);
        var wire = Serialize(sink.Captured[0]);
        wire.GetProperty("data").GetProperty("card").GetProperty("name").GetString().ShouldBe("After");
        wire.GetProperty("data").GetProperty("laneName").GetString().ShouldBe(await LaneNameAsync(laneA));
    }

    [Fact]
    public async Task UpdateCard_OverMcpSizeOnly_FiresCardUpdated()
    {
        var sink = Sink;
        var (laneA, _) = await GetTwoLanesAsync();
        var tools = CreateCardTools();
        var cardId = await CreateCardViaMcpAsync(tools, laneA, "Sizable");
        sink.Clear();

        var result = await tools.UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: cardId, sizeName: "XL");
        result.ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["card.updated"]);
    }

    [Fact]
    public async Task RestPatchNameUnchanged_EmitsNoWebhook_StillRingsOneBell()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var (laneA, _) = await GetTwoLanesAsync();
        var cardId = await CreateCardInLaneViaRestAsync(laneA, "Same");
        sink.Clear();

        var reader = broadcaster.Subscribe(_factory.DefaultBoardId);
        try
        {
            // Re-send the identical name — the per-axis no-op guard suppresses card.updated.
            var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { name = "Same" });
            response.EnsureSuccessStatusCode();

            sink.Captured.ShouldBeEmpty();

            // The SSE bell still rings exactly once (byte-identical) — an all-no-op PATCH
            // preserves the prior "every PATCH rings one bell" behaviour.
            var signals = DrainChannel(reader);
            signals.Count.ShouldBe(1);
            signals[0].ShouldBe("board-updated");
        }
        finally
        {
            broadcaster.Unsubscribe(_factory.DefaultBoardId, reader);
        }
    }

    // ── Multi-axis co-fire — the headline rule ──────────────────────────────────

    [Fact]
    public async Task McpUpdateCard_NameLaneLabel_CoFiresUpdatedMovedLabeled_OneSseBell()
    {
        var sink = Sink;
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var (laneA, laneB) = await GetTwoLanesAsync();
        var tools = CreateCardTools();
        var cardId = await CreateCardViaMcpAsync(tools, laneA, "CoFire");
        var labelId = await SeedLabelAsync("co-fire-label", "#abcdef");
        sink.Clear();

        var reader = broadcaster.Subscribe(_factory.DefaultBoardId);
        try
        {
            // One call changing content (name) + lane + labels (add one) — three changed
            // axes, three webhook events, exactly ONE SSE bell.
            var result = await tools.UpdateCardAsync
            (
                CollatticeApiFactory.TestAdminAuthKey,
                cardId: cardId,
                name: "CoFire Renamed",
                laneId: laneB,
                labelIds: labelId.ToString()
            );
            result.ShouldNotContain("Error");

            sink.Captured.Select(e => e.EventType).Order().ShouldBe(["card.labeled", "card.moved", "card.updated"]);

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
    public async Task RestPatch_NameLaneLabel_CoFiresUpdatedMovedLabeled()
    {
        var sink = Sink;
        var (laneA, laneB) = await GetTwoLanesAsync();
        var cardId = await CreateCardInLaneViaRestAsync(laneA, "Patch CoFire");
        var labelId = await SeedLabelAsync("patch-cofire-label", "#123456");
        sink.Clear();

        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new
        {
            name = "Patch CoFire Renamed",
            laneId = laneB,
            labelIds = new[] { labelId },
        });
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).Order().ShouldBe(["card.labeled", "card.moved", "card.updated"]);
    }

    [Fact]
    public async Task McpUpdateCard_LabelReplace_FiresUnlabeledThenLabeled()
    {
        var sink = Sink;
        var (laneA, _) = await GetTwoLanesAsync();
        var tools = CreateCardTools();
        var cardId = await CreateCardViaMcpAsync(tools, laneA, "Relabel");
        var oldLabel = await SeedLabelAsync("old-label", "#111111");
        var newLabel = await SeedLabelAsync("new-label", "#222222");
        (await tools.UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: cardId, labelIds: oldLabel.ToString())).ShouldNotContain("Error");
        sink.Clear();

        // Replace {old} with {new} — one removal + one add, no card.updated (no content change).
        var result = await tools.UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: cardId, labelIds: newLabel.ToString());
        result.ShouldNotContain("Error");

        var byType = sink.Captured.Select(e => e.EventType).Order().ToList();
        byType.ShouldBe(["card.labeled", "card.unlabeled"]);
        sink.Captured.ShouldNotContain(e => e.EventType == "card.updated");
    }

    // ── card.labeled / card.unlabeled — dedicated label endpoints ───────────────

    [Fact]
    public async Task RestAddLabel_FiresCardLabeled_WithEmbeddedLabelResource()
    {
        var sink = Sink;
        var (laneA, _) = await GetTwoLanesAsync();
        var cardId = await CreateCardInLaneViaRestAsync(laneA, "Labelled");
        var labelId = await SeedLabelAsync("rest-label", "#00ff00");
        sink.Clear();

        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId });
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["card.labeled"]);
        var label = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("label");
        label.GetProperty("id").GetGuid().ShouldBe(labelId);
        label.GetProperty("name").GetString().ShouldBe("rest-label");
        label.GetProperty("color").GetString().ShouldBe("#00ff00");
    }

    [Fact]
    public async Task RemoveLabel_OverRest_FiresCardUnlabeled()
    {
        var sink = Sink;
        var (laneA, _) = await GetTwoLanesAsync();
        var cardId = await CreateCardInLaneViaRestAsync(laneA, "Unlabelled");
        var labelId = await SeedLabelAsync("rest-unlabel", "#ff0000");
        (await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId })).EnsureSuccessStatusCode();
        sink.Clear();

        var response = await _client.DeleteAsync($"/api/v1/cards/{cardId}/labels/{labelId}");
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["card.unlabeled"]);
        Serialize(sink.Captured[0]).GetProperty("data").GetProperty("label").GetProperty("id").GetGuid().ShouldBe(labelId);
    }

    [Fact]
    public async Task AddThenRemoveLabel_OverMcp_FireLabeledThenUnlabeled()
    {
        var sink = Sink;
        var (laneA, _) = await GetTwoLanesAsync();
        var cardTools = CreateCardTools();
        var labelTools = CreateLabelTools();
        var cardId = await CreateCardViaMcpAsync(cardTools, laneA, "Mcp Label");
        var labelId = await SeedLabelAsync("mcp-label", "#abc123");
        sink.Clear();

        (await labelTools.AddLabelToCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: cardId, labelId: labelId)).ShouldNotContain("Error");
        (await labelTools.RemoveLabelFromCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: cardId, labelId: labelId)).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["card.labeled", "card.unlabeled"]);
    }

    // ── card.archived / card.restored — MCP archive_card / restore_card ──────────

    [Fact]
    public async Task McpArchiveAndRestore_FireArchivedAndRestored_NeverMoved()
    {
        var sink = Sink;
        var (laneA, laneB) = await GetTwoLanesAsync();
        var tools = CreateCardTools();
        var archiveTools = CreateArchiveTools();
        var cardId = await CreateCardViaMcpAsync(tools, laneA, "Mcp Archive");
        sink.Clear();

        (await archiveTools.ArchiveCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: cardId)).ShouldNotContain("Error");
        (await archiveTools.RestoreCardAsync(CollatticeApiFactory.TestAdminAuthKey, laneB, cardId: cardId)).ShouldNotContain("Error");

        sink.Captured.Select(e => e.EventType).ShouldBe(["card.archived", "card.restored"]);
        sink.Captured.ShouldNotContain(e => e.EventType == "card.moved");

        // card.archived: the internal archive-lane GUID is dropped from the embedded card (an
        // implementation detail of no use to a consumer), while laneName + isArchived stay.
        var archivedData = Serialize(sink.Captured[0]).GetProperty("data");
        var archivedCard = archivedData.GetProperty("card");
        archivedCard.GetProperty("isArchived").GetBoolean().ShouldBeTrue();
        archivedCard.TryGetProperty("laneId", out _).ShouldBeFalse("card.archived must not leak the archive-lane GUID");
        archivedData.GetProperty("laneName").GetString().ShouldNotBeNullOrEmpty();

        // card.restored: the card is back in a real target lane, so its laneId rides as normal.
        var restoredCard = Serialize(sink.Captured[1]).GetProperty("data").GetProperty("card");
        restoredCard.GetProperty("isArchived").GetBoolean().ShouldBeFalse();
        restoredCard.TryGetProperty("laneId", out var restoredLaneId).ShouldBeTrue();
        restoredLaneId.GetGuid().ShouldBe(laneB);
    }

    // ── Bulk — one event per card, never card.moved for archive/restore ─────────

    [Fact]
    public async Task BulkArchiveThenRestore_OverMcp_FireOneArchivedAndOneRestoredPerCard()
    {
        var sink = Sink;
        var (laneA, laneB) = await GetTwoLanesAsync();
        var tools = CreateCardTools();
        var bulk = CreateBulkTools();
        var card1 = await CreateCardViaMcpAsync(tools, laneA, "Bulk Arch 1");
        var card2 = await CreateCardViaMcpAsync(tools, laneA, "Bulk Arch 2");
        sink.Clear();

        var archiveResult = await bulk.BulkArchiveCardsAsync(CollatticeApiFactory.TestAdminAuthKey, cardIds: $"{card1},{card2}");
        archiveResult.ShouldNotContain("\"failed\":2");
        sink.Captured.Count.ShouldBe(2);
        sink.Captured.ShouldAllBe(e => e.EventType == "card.archived");

        sink.Clear();
        var restoreResult = await bulk.BulkRestoreCardsAsync(CollatticeApiFactory.TestAdminAuthKey, laneB, cardIds: $"{card1},{card2}");
        restoreResult.ShouldNotContain("\"failed\":2");
        sink.Captured.Count.ShouldBe(2);
        sink.Captured.ShouldAllBe(e => e.EventType == "card.restored");
    }

    [Fact]
    public async Task BulkUpdate_SizeAndLabels_CoFiresUpdatedAndLabeledPerCard()
    {
        var sink = Sink;
        var (laneA, _) = await GetTwoLanesAsync();
        var tools = CreateCardTools();
        var bulk = CreateBulkTools();
        var card1 = await CreateCardViaMcpAsync(tools, laneA, "Bulk Upd 1");
        var card2 = await CreateCardViaMcpAsync(tools, laneA, "Bulk Upd 2");
        var labelId = await SeedLabelAsync("bulk-label", "#445566");
        sink.Clear();

        // Uniform size change + label add across two cards → card.updated ×2 + card.labeled ×2.
        var result = await bulk.BulkUpdateCardsAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardIds: $"{card1},{card2}",
            sizeName: "XL",
            labelIds: labelId.ToString()
        );
        result.ShouldNotContain("\"failed\":2");

        sink.Captured.Count(e => e.EventType == "card.updated").ShouldBe(2);
        sink.Captured.Count(e => e.EventType == "card.labeled").ShouldBe(2);
        sink.Captured.ShouldNotContain(e => e.EventType == "card.moved");
    }

    // ── Prune-archive — one card.archived per pruned card ───────────────────────

    [Fact]
    public async Task PruneArchive_OverRest_FiresOneCardArchivedPerCard()
    {
        var sink = Sink;
        var lane = await CreateEmptyLaneAsync("Prune Lane REST");
        var tools = CreateCardTools();
        await CreateCardViaMcpAsync(tools, lane, "Prune 1");
        await CreateCardViaMcpAsync(tools, lane, "Prune 2");
        sink.Clear();

        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/prune", new
        {
            laneIds = new[] { lane },
            action = "archive",
        });
        response.EnsureSuccessStatusCode();

        sink.Captured.Count.ShouldBe(2);
        sink.Captured.ShouldAllBe(e => e.EventType == "card.archived");
    }

    [Fact]
    public async Task PruneArchive_OverMcp_FiresOneCardArchivedPerCard()
    {
        var sink = Sink;
        var lane = await CreateEmptyLaneAsync("Prune Lane MCP");
        var tools = CreateCardTools();
        var pruneTools = CreatePruneTools();
        await CreateCardViaMcpAsync(tools, lane, "Prune MCP 1");
        await CreateCardViaMcpAsync(tools, lane, "Prune MCP 2");
        sink.Clear();

        var result = await pruneTools.PruneAsync(CollatticeApiFactory.TestAdminAuthKey, _factory.DefaultBoardId, laneIds: lane.ToString());
        result.ShouldNotContain("Error");

        sink.Captured.Count.ShouldBe(2);
        sink.Captured.ShouldAllBe(e => e.EventType == "card.archived");
    }

    // ── card.deleted — the two silent delete surfaces (REST delete + prune delete) ──

    [Fact]
    public async Task RestDeleteCard_FiresCardDeleted_FatSummaryBuiltBeforeDelete()
    {
        var sink = Sink;
        var (laneA, _) = await GetTwoLanesAsync();
        var cardId = await CreateCardInLaneViaRestAsync(laneA, "Doomed");
        var labelId = await SeedLabelAsync("delete-label", "#654321");
        (await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId })).EnsureSuccessStatusCode();
        sink.Clear();

        var response = await _client.DeleteAsync($"/api/v1/cards/{cardId}");
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["card.deleted"]);

        // The fat summary is state-at-occurrence, built BEFORE the row is removed — so the label it
        // carried is present (a build-after-delete would enrich a now-deleted row and blank it). The
        // card was not archived, so its real laneId rides.
        var card = Serialize(sink.Captured[0]).GetProperty("data").GetProperty("card");
        card.GetProperty("isArchived").GetBoolean().ShouldBeFalse();
        card.GetProperty("laneId").GetGuid().ShouldBe(laneA);
        card.GetProperty("labels")
            .EnumerateArray()
                .Select(l => l.GetProperty("id").GetGuid())
                    .ShouldContain(labelId);
    }

    [Fact]
    public async Task RestDeleteArchivedCard_FiresCardDeleted_BlanksArchiveLaneGuid()
    {
        var sink = Sink;
        var (laneA, _) = await GetTwoLanesAsync();
        var cardId = await CreateCardInLaneViaRestAsync(laneA, "Archived then deleted");
        (await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null)).EnsureSuccessStatusCode();
        sink.Clear();

        // Delete is allowed on an archived card. Its lane is the hidden archive lane, so the internal
        // GUID is blanked exactly as card.archived does — isArchived + laneName carry the state.
        var response = await _client.DeleteAsync($"/api/v1/cards/{cardId}");
        response.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldBe(["card.deleted"]);
        var data = Serialize(sink.Captured[0]).GetProperty("data");
        var card = data.GetProperty("card");
        card.GetProperty("isArchived").GetBoolean().ShouldBeTrue();
        card.TryGetProperty("laneId", out _).ShouldBeFalse("card.deleted of an archived card must not leak the archive-lane GUID");
        data.GetProperty("laneName").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task PruneDelete_OverRest_FiresOneCardDeletedPerCard()
    {
        var sink = Sink;
        var lane = await CreateEmptyLaneAsync("Prune Delete Lane");
        var tools = CreateCardTools();
        await CreateCardViaMcpAsync(tools, lane, "Prune Del 1");
        await CreateCardViaMcpAsync(tools, lane, "Prune Del 2");
        sink.Clear();

        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/prune", new
        {
            laneIds = new[] { lane },
            action = "delete",
        });
        response.EnsureSuccessStatusCode();

        sink.Captured.Count.ShouldBe(2);
        sink.Captured.ShouldAllBe(e => e.EventType == "card.deleted");
    }

    [Fact]
    public async Task CancelCard_TempCard_FiresNoCardDeleted()
    {
        var sink = Sink;
        var (laneA, _) = await GetTwoLanesAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var tempResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards/temp", new { name = "Temp doomed", laneId = laneA });
        tempResponse.EnsureSuccessStatusCode();
        var tempId = (await tempResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();
        sink.Clear();

        // A temp card never fired card.created (that fires on finalize), so cancelling it fires no
        // card.deleted — a delete for something that never announced its existence is incoherent.
        (await _client.PostAsync($"/api/v1/cards/{tempId}/cancel", null)).EnsureSuccessStatusCode();

        sink.Captured.ShouldNotContain(e => e.EventType == "card.deleted");
    }

    [Fact]
    public async Task BoardDelete_CascadingArchivedCard_FiresBoardDeleted_NotCardDeleted()
    {
        var sink = Sink;
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // A board delete requires zero non-archive lanes, so the only card rows it removes are
        // archived ones — that cascade is covered by board.deleted, deliberately NOT a per-card
        // card.deleted. Build a fresh board, archive a card into its hidden archive lane, drop the
        // now-empty regular lane, then delete the board.
        var boardResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = $"Cascade {Guid.NewGuid():N}" });
        boardResponse.EnsureSuccessStatusCode();
        var boardId = (await boardResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();

        var laneResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes", new { name = "Work", position = 0 });
        laneResponse.EnsureSuccessStatusCode();
        var laneId = (await laneResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();

        var cardResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/cards", new { name = "To cascade", laneId });
        cardResponse.EnsureSuccessStatusCode();
        var cardId = (await cardResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();

        (await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null)).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/v1/lanes/{laneId}")).EnsureSuccessStatusCode();
        sink.Clear();

        var deleteResponse = await _client.DeleteAsync($"/api/v1/boards/{boardId}");
        deleteResponse.EnsureSuccessStatusCode();

        sink.Captured.Select(e => e.EventType).ShouldContain("board.deleted");
        sink.Captured.ShouldNotContain(e => e.EventType == "card.deleted");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private CardTools CreateCardTools()
    {
        var db = NewScopedDb();
        return new CardTools(db, new McpAuthService(new UserResolver(db)), Broadcaster());
    }

    private ArchiveTools CreateArchiveTools()
    {
        var db = NewScopedDb();
        return new ArchiveTools(db, new McpAuthService(new UserResolver(db)), Broadcaster());
    }

    private LabelTools CreateLabelTools()
    {
        var db = NewScopedDb();
        return new LabelTools(db, new McpAuthService(new UserResolver(db)), Broadcaster());
    }

    private BulkCardTools CreateBulkTools()
    {
        var db = NewScopedDb();
        return new BulkCardTools(db, new McpAuthService(new UserResolver(db)), Broadcaster(), _factory.Services.GetRequiredService<IWebhookSink>());
    }

    private PruneTools CreatePruneTools()
    {
        var db = NewScopedDb();
        return new PruneTools(db, new McpAuthService(new UserResolver(db)), Broadcaster(), _factory.Services.GetRequiredService<IWebhookSink>());
    }

    private BoardDbContext NewScopedDb()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<BoardDbContext>();
    }

    private BoardEventBroadcaster Broadcaster() => _factory.Services.GetRequiredService<BoardEventBroadcaster>();

    private async Task<(Guid LaneA, Guid LaneB)> GetTwoLanesAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var lanes = await db.Lanes
            .Where(l => l.BoardId == _factory.DefaultBoardId && !l.IsArchiveLane)
            .OrderBy(l => l.Position)
                .Select(l => l.Id)
                    .ToListAsync();

        return (lanes[0], lanes[1]);
    }

    private async Task<Guid> CreateEmptyLaneAsync(string name)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var maxPosition = await db.Lanes
            .Where(l => l.BoardId == _factory.DefaultBoardId && !l.IsArchiveLane)
                .MaxAsync(l => (int?)l.Position) ?? -1;
        var lane = new Lane { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = name, Position = maxPosition + 1 };
        db.Lanes.Add(lane);
        await db.SaveChangesAsync();
        return lane.Id;
    }

    private async Task<Guid> SeedLabelAsync(string name, string color)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var label = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = name, Color = color };
        db.Labels.Add(label);
        await db.SaveChangesAsync();
        return label.Id;
    }

    private async Task<Guid> CreateCardInLaneViaRestAsync(Guid laneId, string name)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new { name, laneId });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateCardViaMcpAsync(CardTools tools, Guid laneId, string name)
    {
        var result = await tools.CreateCardAsync(CollatticeApiFactory.TestAdminAuthKey, name, laneId);
        result.ShouldNotContain("Error");
        return JsonDocument.Parse(result).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<string> LaneNameAsync(Guid laneId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.Lanes.Where(l => l.Id == laneId).Select(l => l.Name).FirstAsync();
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
