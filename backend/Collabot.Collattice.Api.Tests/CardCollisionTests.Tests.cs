using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Collision awareness on card description edits: a caller that passes back the revision it read gets
// an exact answer about whether its write replaced another user's edit; a caller that passes nothing
// gets a best-effort recency signal. Awareness only — every case below still saves (last write wins),
// and there is no conflict status to handle.
//
// The detector is exercised two ways: directly, for the branch matrix that a round-trip cannot pin
// deterministically (the approximate window, self-exclusion), and through both write surfaces, for the
// wiring and the response shape — because "attached at the two write sites, never through the shared
// builder" is a claim about where the field appears on the wire, not just about the detector's logic.
public class CardCollisionTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
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

    // ── The detector's branch matrix (direct) ───────────────────────────────

    [Fact]
    public async Task Detect_ExactBaselineBehindHead_ReportsCollisionNamingTheHeadEditor()
    {
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Exact Author", UserRole.HumanUser);
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Exact Rival", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");

        // author edits (trail seeds to revision 2), then rival edits (revision 3). An author who read
        // at revision 2 is now a revision behind.
        await PatchDescriptionAsAsync(author.AuthKey, cardId, "author wording");
        await PatchDescriptionAsAsync(rival.AuthKey, cardId, "rival wording");

        var db = ResolveDb();
        var collision = await CardCollisionDetector.DetectAsync(db, cardId, CardHistoryHelper.DescriptionField, baselineRevision: 2, priorEditorId: Guid.Empty, priorEditedAtUtc: default, actingUserId: author.Id);

        collision.ShouldNotBeNull();
        collision.Kind.ShouldBe(CardCollisionDetector.ExactKind);
        collision.Field.ShouldBe(CardHistoryHelper.DescriptionField);
        collision.Actor.UserId.ShouldBe(rival.Id);
        collision.Actor.Name.ShouldBe("Exact Rival");
    }

    [Fact]
    public async Task Detect_ExactBaselineBehindHead_ButTheHeadEditorIsTheCaller_ReportsNoCollision()
    {
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Self Exact Author", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");

        // The same user edits twice — its own edits seed revision 2 then revision 3 — so the head is
        // the caller's own most recent edit. A caller that read at revision 2 and then writes again on
        // that stale baseline sees the head ahead of its baseline (the exact check clears), but the head
        // editor is itself: it overwrote nobody. A batching bot that caches descriptionHistoryCount once
        // and reuses it across several edits is exactly this caller. Deleting the self-exclusion guard
        // makes this fail — the detector then names the caller as the user it overwrote.
        await PatchDescriptionAsAsync(author.AuthKey, cardId, "author first");  // trail now at revision 2
        await PatchDescriptionAsAsync(author.AuthKey, cardId, "author second"); // trail now at revision 3

        var db = ResolveDb();
        var collision = await CardCollisionDetector.DetectAsync(db, cardId, CardHistoryHelper.DescriptionField, baselineRevision: 2, priorEditorId: Guid.Empty, priorEditedAtUtc: default, actingUserId: author.Id);

        collision.ShouldBeNull();
    }

    [Fact]
    public async Task Detect_ExactBaselineAtHead_ReportsNoCollision()
    {
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "AtHead Author", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");
        await PatchDescriptionAsAsync(author.AuthKey, cardId, "author wording");

        // The trail is at revision 2 and the author read revision 2 — nobody edited in between.
        var db = ResolveDb();
        var collision = await CardCollisionDetector.DetectAsync(db, cardId, CardHistoryHelper.DescriptionField, baselineRevision: 2, priorEditorId: Guid.Empty, priorEditedAtUtc: default, actingUserId: author.Id);

        collision.ShouldBeNull();
    }

    [Fact]
    public async Task Detect_ExactAgainstAnUntouchedDescription_ReportsNoCollision()
    {
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "NoTrail Author", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");

        // No description edit has happened, so the trail is empty (count zero) and a baseline of zero
        // matches. Detection must not invent a collision, and must not fault on the absent head.
        var db = ResolveDb();
        var collision = await CardCollisionDetector.DetectAsync(db, cardId, CardHistoryHelper.DescriptionField, baselineRevision: 0, priorEditorId: Guid.Empty, priorEditedAtUtc: default, actingUserId: author.Id);

        collision.ShouldBeNull();
    }

    [Fact]
    public async Task Detect_ApproximateAnotherUserWithinWindow_ReportsCollision()
    {
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Approx Author", UserRole.HumanUser);
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Approx Rival", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");

        // No baseline — the card was last touched by someone else a moment ago.
        var db = ResolveDb();
        var collision = await CardCollisionDetector.DetectAsync(db, cardId, CardHistoryHelper.DescriptionField, baselineRevision: null, priorEditorId: rival.Id, priorEditedAtUtc: DateTimeOffset.UtcNow, actingUserId: author.Id);

        collision.ShouldNotBeNull();
        collision.Kind.ShouldBe(CardCollisionDetector.ApproximateKind);
        collision.Field.ShouldBeNull();
        collision.Actor.UserId.ShouldBe(rival.Id);
        collision.Actor.Name.ShouldBe("Approx Rival");
    }

    [Fact]
    public async Task Detect_ApproximatePriorEditByTheSameUser_ReportsNoCollision()
    {
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Approx Self", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");

        // My own recent edit is sequential self-editing, not a collision.
        var db = ResolveDb();
        var collision = await CardCollisionDetector.DetectAsync(db, cardId, CardHistoryHelper.DescriptionField, baselineRevision: null, priorEditorId: author.Id, priorEditedAtUtc: DateTimeOffset.UtcNow, actingUserId: author.Id);

        collision.ShouldBeNull();
    }

    [Fact]
    public async Task Detect_ApproximatePriorEditOlderThanTheWindow_ReportsNoCollision()
    {
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Approx Stale Author", UserRole.HumanUser);
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Approx Stale Rival", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");

        // Another user edited, but long enough ago that it reads as ordinary sequential editing rather
        // than concurrent work — the deliberately short window is what draws that line.
        var stale = DateTimeOffset.UtcNow - CardCollisionDetector.ApproximateWindow - TimeSpan.FromSeconds(5);
        var db = ResolveDb();
        var collision = await CardCollisionDetector.DetectAsync(db, cardId, CardHistoryHelper.DescriptionField, baselineRevision: null, priorEditorId: rival.Id, priorEditedAtUtc: stale, actingUserId: author.Id);

        collision.ShouldBeNull();
    }

    // ── The write surfaces (REST PATCH) ─────────────────────────────────────

    [Fact]
    public async Task RestPatch_StaleBaseline_ReturnsExactCollision_AndStillSaves()
    {
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Patch Author", UserRole.HumanUser);
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Patch Rival", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");

        await PatchDescriptionAsAsync(author.AuthKey, cardId, "author wording"); // trail now at revision 2
        await PatchDescriptionAsAsync(rival.AuthKey, cardId, "rival wording");   // trail now at revision 3

        // The author writes believing the description is still at the revision 2 it read.
        TestAuthHelper.SetAuth(_client, author.AuthKey);
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { descriptionMarkdown = "author final", expectedDescriptionRevision = 2 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);

        // Awareness, not blocking: the write landed (last one wins) and the collision rides alongside.
        root.GetProperty("descriptionMarkdown").GetString().ShouldBe("author final");

        var collision = root.GetProperty("collision");
        collision.GetProperty("kind").GetString().ShouldBe(CardCollisionDetector.ExactKind);
        collision.GetProperty("field").GetString().ShouldBe(CardHistoryHelper.DescriptionField);
        collision.GetProperty("actor").GetProperty("userId").GetGuid().ShouldBe(rival.Id);
        collision.GetProperty("actor").GetProperty("name").GetString().ShouldBe("Patch Rival");
    }

    [Fact]
    public async Task RestPatch_CurrentBaseline_OmitsTheCollisionField()
    {
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Current Author", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");
        await PatchDescriptionAsAsync(author.AuthKey, cardId, "author wording"); // trail at revision 2

        TestAuthHelper.SetAuth(_client, author.AuthKey);
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { descriptionMarkdown = "author again", expectedDescriptionRevision = 2 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);

        // No overlap, so no collision object — and the enriched card fields are still at the top level.
        root.TryGetProperty("collision", out _).ShouldBeFalse();
        root.GetProperty("descriptionMarkdown").GetString().ShouldBe("author again");
        root.GetProperty("sizeName").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RestPatch_NoBaseline_RecentOtherEditor_ReturnsApproximateCollision()
    {
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Loose Author", UserRole.HumanUser);
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Loose Rival", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");

        // Rival edits; the author edits moments later without passing a baseline.
        await PatchDescriptionAsAsync(rival.AuthKey, cardId, "rival wording");

        TestAuthHelper.SetAuth(_client, author.AuthKey);
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { descriptionMarkdown = "author wording" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);

        var collision = root.GetProperty("collision");
        collision.GetProperty("kind").GetString().ShouldBe(CardCollisionDetector.ApproximateKind);
        collision.TryGetProperty("field", out var field).ShouldBeTrue();
        (field.ValueKind == JsonValueKind.Null).ShouldBeTrue();
        collision.GetProperty("actor").GetProperty("name").GetString().ShouldBe("Loose Rival");
    }

    [Fact]
    public async Task RestPatch_LaneMoveOnly_NeverReportsACollision()
    {
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Mover Author", UserRole.HumanUser);
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Mover Rival", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");
        await PatchDescriptionAsAsync(rival.AuthKey, cardId, "rival wording");

        var otherLaneId = await TestDataHelper.GetLaneIdByIndexAsync(_client, _factory.DefaultBoardId, 1);

        // A move that does not touch the description gets no collision detection at all, even with a
        // recent other-user edit present — collision awareness is gated on editing the lit field.
        TestAuthHelper.SetAuth(_client, author.AuthKey);
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { laneId = otherLaneId });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        root.TryGetProperty("collision", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task RestPatch_BaselineReadFromTheCardDetail_RoundTripsToAnExactCollision()
    {
        // The documented usage is "pass back the descriptionHistoryCount you read from the card." Every
        // other exact-path test passes a literal baseline; this one reads the count off the read surface
        // and feeds THAT into the write, pinning that the count a caller reads and the revision ordinal
        // the detector compares against are one and the same number — the coupling the documented usage
        // rests on. A drift between the read count and the write baseline would misfire here.
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "RoundTrip Author", UserRole.HumanUser);
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "RoundTrip Rival", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");
        await PatchDescriptionAsAsync(author.AuthKey, cardId, "author wording"); // history count now 2

        // Read the baseline the way an integrator would — off GET /cards/{id}, not as a literal.
        TestAuthHelper.SetAuth(_client, author.AuthKey);
        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/cards/{cardId}", TestAuthHelper.JsonOptions);
        var baseline = detail.GetProperty("descriptionHistoryCount").GetInt32();

        // Someone else edits after that read, moving the description a revision on.
        await PatchDescriptionAsAsync(rival.AuthKey, cardId, "rival wording"); // history count now 3

        // Feed the count that was read straight back as the baseline.
        TestAuthHelper.SetAuth(_client, author.AuthKey);
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { descriptionMarkdown = "author final", expectedDescriptionRevision = baseline });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);

        var collision = root.GetProperty("collision");
        collision.GetProperty("kind").GetString().ShouldBe(CardCollisionDetector.ExactKind);
        collision.GetProperty("actor").GetProperty("userId").GetGuid().ShouldBe(rival.Id);
        collision.GetProperty("actor").GetProperty("name").GetString().ShouldBe("RoundTrip Rival");
    }

    // ── The write surfaces (MCP update_card) ────────────────────────────────

    [Fact]
    public async Task McpUpdateCard_StaleBaseline_ReturnsExactCollision_MatchingRest()
    {
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Mcp Author", UserRole.HumanUser);
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Mcp Rival", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");
        await PatchDescriptionAsAsync(author.AuthKey, cardId, "author wording"); // revision 2
        await PatchDescriptionAsAsync(rival.AuthKey, cardId, "rival wording");   // revision 3

        var (_, tools) = CreateTools();
        var result = await tools.UpdateCardAsync(author.AuthKey, cardId, descriptionMarkdown: "author final", expectedDescriptionRevision: 2);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Same flattened shape as REST: enriched card fields at the top, collision beside them.
        root.GetProperty("descriptionMarkdown").GetString().ShouldBe("author final");
        root.GetProperty("sizeName").GetString().ShouldNotBeNullOrWhiteSpace();

        var collision = root.GetProperty("collision");
        collision.GetProperty("kind").GetString().ShouldBe(CardCollisionDetector.ExactKind);
        collision.GetProperty("field").GetString().ShouldBe(CardHistoryHelper.DescriptionField);
        collision.GetProperty("actor").GetProperty("userId").GetGuid().ShouldBe(rival.Id);
        collision.GetProperty("actor").GetProperty("name").GetString().ShouldBe("Mcp Rival");
    }

    [Fact]
    public async Task McpUpdateCard_NoBaseline_RecentOtherEditor_ReturnsApproximateCollision()
    {
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Mcp Approx Author", UserRole.HumanUser);
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Mcp Approx Rival", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");
        await PatchDescriptionAsAsync(rival.AuthKey, cardId, "rival wording");

        var (_, tools) = CreateTools();
        var result = await tools.UpdateCardAsync(author.AuthKey, cardId, descriptionMarkdown: "author wording");

        using var doc = JsonDocument.Parse(result);
        var collision = doc.RootElement.GetProperty("collision");
        collision.GetProperty("kind").GetString().ShouldBe(CardCollisionDetector.ApproximateKind);
        collision.GetProperty("actor").GetProperty("name").GetString().ShouldBe("Mcp Approx Rival");
    }

    [Fact]
    public async Task McpUpdateCard_OnlyExpectedRevision_IsStillANoOp()
    {
        var cardId = await CreateCardAsync("original");

        // The baseline is not a change. Passing it alone must not trip a phantom update.
        var (_, tools) = CreateTools();
        var result = await tools.UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId, expectedDescriptionRevision: 1);

        result.ShouldBe("No changes specified.");
    }

    // ── The structural safeguard ────────────────────────────────────────────

    [Fact]
    public async Task Collision_JustReported_NeverAppearsInListOrSearchPayloads()
    {
        // Collision lives on CardUpdateResult, which only the two write responses build. The shared
        // CardSummary that feeds card lists and cross-board search has no member for it, so it cannot
        // leak there — this proves it on the wire right after a collision has just been reported.
        var author = await TestAuthHelper.CreateUserAsync(_client, _factory, "Leak Author", UserRole.HumanUser);
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Leak Rival", UserRole.HumanUser);
        var cardId = await CreateCardAsync("original");
        await PatchDescriptionAsAsync(author.AuthKey, cardId, "author wording");
        await PatchDescriptionAsAsync(rival.AuthKey, cardId, "rival wording");

        TestAuthHelper.SetAuth(_client, author.AuthKey);
        var patch = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { descriptionMarkdown = "author final", expectedDescriptionRevision = 2 });
        var patched = await patch.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        patched.TryGetProperty("collision", out _).ShouldBeTrue(); // it was reported on the write

        TestAuthHelper.SetAdminAuth(_client, _factory);

        var list = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/boards/{_factory.DefaultBoardId}/cards", TestAuthHelper.JsonOptions);
        foreach (var item in list.GetProperty("items").EnumerateArray())
        {
            item.TryGetProperty("collision", out _).ShouldBeFalse();
        }

        var search = await _client.GetFromJsonAsync<JsonElement>("/api/v1/search/cards?q=author", TestAuthHelper.JsonOptions);
        foreach (var group in search.EnumerateArray())
        {
            foreach (var card in group.GetProperty("cards").EnumerateArray())
            {
                card.TryGetProperty("collision", out _).ShouldBeFalse();
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private BoardDbContext ResolveDb()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<BoardDbContext>();
    }

    private (BoardDbContext Db, CardTools Tools) CreateTools()
    {
        var db = ResolveDb();
        var broadcaster = _factory.Services.GetRequiredService<BoardEventBroadcaster>();
        var auth = new McpAuthService(new UserResolver(db));
        return (db, new CardTools(db, auth, broadcaster));
    }

    private async Task<Guid> CreateCardAsync(string descriptionMarkdown)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new { name = "Collision Card", laneId, descriptionMarkdown });
        response.EnsureSuccessStatusCode();
        var card = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return card.GetProperty("id").GetGuid();
    }

    private async Task PatchDescriptionAsAsync(string userKey, Guid cardId, string descriptionMarkdown)
    {
        TestAuthHelper.SetAuth(_client, userKey);
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { descriptionMarkdown });
        response.EnsureSuccessStatusCode();
    }
}
