using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class CardHistoryEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private static JsonSerializerOptions JsonOptions => TestAuthHelper.JsonOptions;

    [Fact]
    public async Task PatchDescription_FirstEdit_RecordsThePriorValueWithEditorAndTimestamp()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Capture", "original text");

        // Act
        await PatchDescriptionAsync(cardId, "replacement text");

        // Assert — two revisions: the value that was already there, then the one that replaced it.
        var entries = await GetTrailEntriesAsync(cardId);
        entries.Length.ShouldBe(2);

        var newest = entries[0];
        newest.GetProperty("revision").GetInt32().ShouldBe(2);
        newest.GetProperty("value").GetString().ShouldBe("replacement text");
        newest.GetProperty("editedByName").GetString().ShouldBe("Admin");
        newest.GetProperty("editedAtUtc").ValueKind.ShouldNotBe(JsonValueKind.Null);

        var oldest = entries[1];
        oldest.GetProperty("revision").GetInt32().ShouldBe(1);
        oldest.GetProperty("value").GetString().ShouldBe("original text");
        oldest.GetProperty("diff").GetString().ShouldBe(string.Empty);

        // The trail's oldest value predates recording, so its observed provenance stays unknown.
        // The card's creator appears only in the separate inferred field, never in these.
        oldest.GetProperty("editedByUserId").ValueKind.ShouldBe(JsonValueKind.Null);
        oldest.GetProperty("editedAtUtc").ValueKind.ShouldBe(JsonValueKind.Null);

        // Nothing was lost and the card still carries the new text.
        var card = await GetCardAsync(cardId);
        card.GetProperty("card").GetProperty("descriptionMarkdown").GetString().ShouldBe("replacement text");
    }

    [Fact]
    public async Task OldestRevision_NamesTheCardCreatorAsAnInferredEditor_NotWhoeverTriggeredTheCapture()
    {
        // Arrange — one user creates the card and a different user makes the first edit, so an
        // inference that credited the first capture's trigger would name the wrong person.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var creator = await TestAuthHelper.CreateUserAsync(_client, _factory, "History Card Creator", UserRole.AgentUser);

        using var creatorClient = _factory.CreateClient();
        TestAuthHelper.SetAuth(creatorClient, creator.AuthKey);
        var cardId = await CreateCardAsync(creatorClient, "History Inferred Creator", "as the creator wrote it");

        await PatchDescriptionAsync(cardId, "edited by the admin");

        // Act
        var entries = await GetTrailEntriesAsync(cardId);

        // Assert — the inference names the creator, says it is an inference, and leaves the
        // observed attribution exactly as null as it was.
        var oldest = entries[1];
        oldest.GetProperty("editedByUserId").ValueKind.ShouldBe(JsonValueKind.Null);
        oldest.GetProperty("editedByName").ValueKind.ShouldBe(JsonValueKind.Null);
        oldest.GetProperty("editedAtUtc").ValueKind.ShouldBe(JsonValueKind.Null);

        var inferred = oldest.GetProperty("inferredEditor");
        inferred.GetProperty("userId").GetGuid().ShouldBe(creator.Id);
        inferred.GetProperty("name").GetString().ShouldBe("History Card Creator");
        inferred.GetProperty("basis").GetString().ShouldBe("creator");

        // The observed revision is untouched: its own editor, and no inferred one beside it.
        var newest = entries[0];
        newest.GetProperty("editedByName").GetString().ShouldBe("Admin");
        newest.TryGetProperty("inferredEditor", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task GetHistory_InferredEditor_IsNeverWrittenIntoTheStoredRevision()
    {
        // The inference is resolved on read. The stored row keeps recording only what was
        // observed, so reading the trail must leave the oldest row's author and time null.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Inferred Read Only", "before");
        await PatchDescriptionAsync(cardId, "after");

        // Act — read it, so there is something that could have written back.
        var entries = await GetTrailEntriesAsync(cardId);
        entries[1].GetProperty("inferredEditor").GetProperty("name").GetString().ShouldBe("Admin");

        // Assert
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var oldestRow = await db.CardFieldHistories.SingleAsync(h => h.CardId == cardId && h.Revision == 1);
        oldestRow.EditedByUserId.ShouldBeNull();
        oldestRow.EditedAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task GetHistory_PageStoppingShortOfTheOldestRevision_CarriesNoInferredEditor()
    {
        // Arrange — four revisions; the newest page of two holds only observed revisions.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("History Inferred Paging", 4);

        // Act
        var newestPage = await GetTrailAsync(cardId, "limit=2");
        var oldestPage = await GetTrailAsync(cardId, "offset=2&limit=2");

        // Assert — only the oldest revision carries the inference, wherever it lands.
        newestPage.GetProperty("entries").EnumerateArray()
            .Any(e => e.TryGetProperty("inferredEditor", out _))
                .ShouldBeFalse();

        var oldestPageEntries = oldestPage.GetProperty("entries").EnumerateArray().ToArray();
        oldestPageEntries[0].TryGetProperty("inferredEditor", out _).ShouldBeFalse();
        oldestPageEntries[1].GetProperty("inferredEditor").GetProperty("basis").GetString().ShouldBe("creator");
    }

    [Fact]
    public async Task EditDescriptionTwice_DiffFormat_ReturnsAUnifiedDiffPerRevisionNewestFirst()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Diff", "alpha\nbeta");

        await PatchDescriptionAsync(cardId, "alpha\ngamma");
        await PatchDescriptionAsync(cardId, "alpha\ngamma\ndelta");

        // Act
        var entries = await GetTrailEntriesAsync(cardId, "format=diff");

        // Assert — newest first, one diff per edit, and no full snapshots on the wire.
        entries.Select(e => e.GetProperty("revision").GetInt32()).ShouldBe([3, 2, 1]);
        entries.Any(e => e.TryGetProperty("value", out _)).ShouldBeFalse();

        entries[0].GetProperty("diff").GetString().ShouldBe("@@ -1,2 +1,3 @@\n alpha\n gamma\n+delta\n");
        entries[1].GetProperty("diff").GetString().ShouldBe("@@ -1,2 +1,2 @@\n alpha\n-beta\n+gamma\n");
        entries[2].GetProperty("diff").GetString().ShouldBe(string.Empty);
    }

    [Fact]
    public async Task GetHistory_FormatFull_ReturnsWholeValuesAndOmitsDiffs()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Full", "before");
        await PatchDescriptionAsync(cardId, "after");

        // Act
        var entries = await GetTrailEntriesAsync(cardId, "format=full");

        // Assert
        entries[0].GetProperty("value").GetString().ShouldBe("after");
        entries[1].GetProperty("value").GetString().ShouldBe("before");
        entries.Any(e => e.TryGetProperty("diff", out _)).ShouldBeFalse();
    }

    [Fact]
    public async Task GetHistory_NoFormatParameter_DefaultsToBoth()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Default Format", "before");
        await PatchDescriptionAsync(cardId, "after");

        // Act — no format parameter
        var entries = await GetTrailEntriesAsync(cardId);

        // Assert
        entries[0].GetProperty("value").GetString().ShouldBe("after");
        entries[0].GetProperty("diff").GetString()!.ShouldContain("+after");
    }

    [Fact]
    public async Task GetHistory_ArbitraryPair_ReturnsTheDiffBetweenTwoRevisions()
    {
        // Arrange — three versions
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Pair", "one");
        await PatchDescriptionAsync(cardId, "two");
        await PatchDescriptionAsync(cardId, "three");

        // Act — skip the middle revision
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/history?from=1&to=3");

        // Assert
        response.EnsureSuccessStatusCode();
        var pair = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        pair.GetProperty("from").GetInt32().ShouldBe(1);
        pair.GetProperty("to").GetInt32().ShouldBe(3);
        pair.GetProperty("diff").GetString().ShouldBe("@@ -1,1 +1,1 @@\n-one\n+three\n");
        pair.GetProperty("fromValue").GetString().ShouldBe("one");
        pair.GetProperty("toValue").GetString().ShouldBe("three");
    }

    [Fact]
    public async Task ArbitraryPair_ReversedOrder_ReturnsTheUndoingDiff()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Reverse Pair", "one");
        await PatchDescriptionAsync(cardId, "two");

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/history?from=2&to=1");

        // Assert
        response.EnsureSuccessStatusCode();
        var pair = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        pair.GetProperty("diff").GetString().ShouldBe("@@ -1,1 +1,1 @@\n-two\n+one\n");
    }

    [Fact]
    public async Task PatchDescription_SameValue_RecordsNoEntry()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History No-Op", "unchanged");

        // Act — save the description exactly as it already is
        await PatchDescriptionAsync(cardId, "unchanged");

        // Assert
        var entries = await GetTrailEntriesAsync(cardId);
        entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoOpDescriptionSave_AfterARealEdit_AddsNoFurtherEntry()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History No-Op After Edit", "first");
        await PatchDescriptionAsync(cardId, "second");

        // Act
        await PatchDescriptionAsync(cardId, "second");

        // Assert
        var entries = await GetTrailEntriesAsync(cardId);
        entries.Length.ShouldBe(2);
    }

    [Fact]
    public async Task PatchCard_LaneMoveOnly_RecordsNoEntry()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Lane Only", "untouched");
        var targetLaneId = await TestDataHelper.GetLaneIdByIndexAsync(_client, _factory.DefaultBoardId, 1);

        // Act — a lane move carries no description at all
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { laneId = targetLaneId });
        response.EnsureSuccessStatusCode();

        // Assert
        var entries = await GetTrailEntriesAsync(cardId);
        entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task PatchDescription_ArchivedCard_RejectsTheEditAndKeepsExistingHistoryReadable()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Archived", "before archiving");
        await PatchDescriptionAsync(cardId, "edited before archiving");

        var archiveResponse = await _client.PostAsync($"/api/v1/cards/{cardId}/archive", null);
        archiveResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Act
        var patchResponse = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { descriptionMarkdown = "not allowed" });

        // Assert — the edit is refused and accrues nothing, but the trail is still readable.
        patchResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var entries = await GetTrailEntriesAsync(cardId);
        entries.Length.ShouldBe(2);
        entries[0].GetProperty("value").GetString().ShouldBe("edited before archiving");
    }

    [Fact]
    public async Task GetHistory_NeverEditedCard_ReturnsAnEmptyTrail()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Never Edited", "as created");

        // Act
        var entries = await GetTrailEntriesAsync(cardId);

        // Assert — no synthetic first entry; the current text is still on the card.
        entries.ShouldBeEmpty();

        var card = await GetCardAsync(cardId);
        card.GetProperty("card").GetProperty("descriptionMarkdown").GetString().ShouldBe("as created");
    }

    [Fact]
    public async Task History_WithoutAuthentication_IsDeniedLikeTheCardRead()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Unauthorized", "secret");

        using var anonymous = _factory.CreateClient();

        // Act
        var historyResponse = await anonymous.GetAsync($"/api/v1/cards/{cardId}/history");
        var cardResponse = await anonymous.GetAsync($"/api/v1/cards/{cardId}");

        // Assert — history is exactly as reachable as the card it belongs to.
        historyResponse.StatusCode.ShouldBe(cardResponse.StatusCode);
        historyResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetHistory_NonAdminUser_CanReadTheTrail()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Agent Read", "before");
        await PatchDescriptionAsync(cardId, "after");

        var agent = await TestAuthHelper.CreateUserAsync(_client, _factory, "History Reader", UserRole.AgentUser);

        using var agentClient = _factory.CreateClient();
        TestAuthHelper.SetAuth(agentClient, agent.AuthKey);

        // Act
        var response = await agentClient.GetAsync($"/api/v1/cards/{cardId}/history");

        // Assert — reading history needs no role beyond reading the card.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var trail = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        trail.GetProperty("entries").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task History_ForAnUnknownCard_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{Guid.NewGuid()}/history");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task History_WithAnUnknownField_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Bad Field", "text");

        // Act — a typo must not read as "this card has no history"
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/history?field=descriptoin");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task History_WithAnUnknownFormat_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Bad Format", "text");

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/history?format=patch");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task History_WithFromButNoTo_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Half Pair", "text");
        await PatchDescriptionAsync(cardId, "edited");

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/history?from=1");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task History_PairNamingAMissingRevision_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Missing Revision", "text");
        await PatchDescriptionAsync(cardId, "edited");

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/history?from=1&to=99");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchDescription_RestAndMcpEdits_ShareOneTrailWithPerEditAttribution()
    {
        // Arrange — the two description write paths must record through the same seam, so an edit
        // from either surface continues the same trail rather than starting a parallel one.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Cross Surface", "genesis");
        var agent = await TestAuthHelper.CreateUserAsync(_client, _factory, "History Agent", UserRole.AgentUser);

        await PatchDescriptionAsync(cardId, "edited over rest");

        // Act — second edit through MCP, as a different user
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var cardTools = new CardTools(db, authService, broadcaster);

        await cardTools.UpdateCardAsync(agent.AuthKey, cardId: cardId, descriptionMarkdown: "edited over mcp");

        // Assert — one continuous trail, each revision attributed to whoever made that edit.
        var entries = await GetTrailEntriesAsync(cardId, "format=full");
        entries.Select(e => e.GetProperty("revision").GetInt32()).ShouldBe([3, 2, 1]);
        entries[0].GetProperty("editedByName").GetString().ShouldBe("History Agent");
        entries[1].GetProperty("editedByName").GetString().ShouldBe("Admin");
        entries[2].GetProperty("editedByName").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task DeleteCard_WithHistory_CascadesToItsHistoryRows()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Cascade", "before");
        await PatchDescriptionAsync(cardId, "after");

        // Act
        var deleteResponse = await _client.DeleteAsync($"/api/v1/cards/{cardId}");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Assert — asserted against the rows themselves, not a route's status code.
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var remaining = await db.CardFieldHistories.CountAsync(h => h.CardId == cardId);
        remaining.ShouldBe(0);
    }

    [Fact]
    public async Task GetHistory_GenesisRevision_RoundTripsItsNullTimestampThroughThePersistenceLayer()
    {
        // Arrange — the shared DateTimeOffset value converter is applied to a nullable column here
        // for the first time in the model; a null that failed to round-trip would surface as a
        // read-time exception rather than a wrong answer.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Null Timestamp", "before");
        await PatchDescriptionAsync(cardId, "after");

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var rows = await db.CardFieldHistories
            .Where(h => h.CardId == cardId)
            .OrderBy(h => h.Revision)
                .ToListAsync();

        // Assert
        rows[0].EditedAtUtc.ShouldBeNull();
        rows[0].EditedByUserId.ShouldBeNull();
        rows[1].EditedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetHistory_NoPagingParameters_ReturnsEverythingAndReportsNoLimit()
    {
        // The behaviour a caller written before paging existed must keep seeing.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("History Unpaged", 6);

        // Act
        var trail = await GetTrailAsync(cardId);

        // Assert
        trail.GetProperty("entries").GetArrayLength().ShouldBe(6);
        trail.GetProperty("totalCount").GetInt32().ShouldBe(6);
        trail.GetProperty("offset").GetInt32().ShouldBe(0);
        trail.GetProperty("limit").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetHistory_WithALimit_ReturnsTheNewestPageAndTheWholeTrailsCount()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("History First Page", 6);

        // Act
        var trail = await GetTrailAsync(cardId, "limit=2");

        // Assert — a page is taken from the newest end, and totalCount still describes all of it.
        var entries = trail.GetProperty("entries").EnumerateArray().ToArray();
        entries.Select(e => e.GetProperty("revision").GetInt32()).ShouldBe([6, 5]);
        trail.GetProperty("totalCount").GetInt32().ShouldBe(6);
        trail.GetProperty("offset").GetInt32().ShouldBe(0);
        trail.GetProperty("limit").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task GetHistory_WalkedTwoAtATime_PagesCoverTheWholeTrailWithoutGapsOrOverlap()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("History Walk", 7);

        // Act — walk it two at a time.
        List<int> walked = [];
        for (var offset = 0; offset < 8; offset += 2)
        {
            var page = await GetTrailAsync(cardId, string.Create(CultureInfo.InvariantCulture, $"offset={offset}&limit=2"));
            walked.AddRange(page.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("revision").GetInt32()));
        }

        // Assert
        walked.ShouldBe([7, 6, 5, 4, 3, 2, 1]);
    }

    [Fact]
    public async Task GetHistory_OldestEntryOnATruncatedPage_DiffsAgainstItsRealPredecessor()
    {
        // The failure this guards: a paged read that only fetches its own page has nothing older
        // than the page's last entry, so that entry would come back with an empty diff and be
        // indistinguishable from the trail's genuinely un-diffable first revision. The diff must
        // be identical whether the entry arrives on a page or in the whole trail.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Page Boundary", "one");
        await PatchDescriptionAsync(cardId, "two");
        await PatchDescriptionAsync(cardId, "three");
        await PatchDescriptionAsync(cardId, "four");

        // Act — the page holds revisions 3 and 2; revision 2 is its oldest and its predecessor
        // (revision 1) falls outside the page. That entry is where the hazard lives, not the
        // page's newest, whose predecessor is on the page anyway and would be right regardless.
        var page = await GetTrailAsync(cardId, "offset=1&limit=2");
        var pagedEntries = page.GetProperty("entries").EnumerateArray().ToArray();

        // Assert
        pagedEntries.Select(e => e.GetProperty("revision").GetInt32()).ShouldBe([3, 2]);

        var oldestOnPage = pagedEntries[1];
        oldestOnPage.GetProperty("revision").GetInt32().ShouldBe(2);
        oldestOnPage.GetProperty("diff").GetString().ShouldBe("@@ -1,1 +1,1 @@\n-one\n+two\n");
        oldestOnPage.GetProperty("diff").GetString().ShouldNotBe(string.Empty);

        // The same revision, read unpaged, carries the same diff — paging must not change it.
        var whole = await GetTrailEntriesAsync(cardId);
        var unpagedRevisionTwo = whole.Single(e => e.GetProperty("revision").GetInt32() == 2);
        unpagedRevisionTwo.GetProperty("diff").GetString().ShouldBe(oldestOnPage.GetProperty("diff").GetString());
    }

    [Fact]
    public async Task GetHistory_FirstRevisionLandingOnAPage_StillHasAnEmptyDiff()
    {
        // The other side of the boundary: reaching the true oldest revision through paging must
        // still report "nothing older to compare against" rather than inventing a predecessor.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("History Page Tail", 4);

        // Act
        var page = await GetTrailAsync(cardId, "offset=2&limit=2");

        // Assert
        var entries = page.GetProperty("entries").EnumerateArray().ToArray();
        entries.Select(e => e.GetProperty("revision").GetInt32()).ShouldBe([2, 1]);
        entries[1].GetProperty("diff").GetString().ShouldBe(string.Empty);
        entries[1].GetProperty("editedByUserId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetHistory_OutOfRangePagingValues_ClampLikeTheRestOfTheApi()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("History Clamp", 3);

        // Act & Assert — a negative offset is zero, and a limit outside 1..200 clamps into it.
        var negativeOffset = await GetTrailAsync(cardId, "offset=-5");
        negativeOffset.GetProperty("offset").GetInt32().ShouldBe(0);
        negativeOffset.GetProperty("entries").GetArrayLength().ShouldBe(3);

        var zeroLimit = await GetTrailAsync(cardId, "limit=0");
        zeroLimit.GetProperty("limit").GetInt32().ShouldBe(1);
        zeroLimit.GetProperty("entries").GetArrayLength().ShouldBe(1);

        var hugeLimit = await GetTrailAsync(cardId, "limit=9999");
        hugeLimit.GetProperty("limit").GetInt32().ShouldBe(200);
        hugeLimit.GetProperty("entries").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task GetHistory_OffsetWithNoLimit_ReturnsTheRestOfTheTrailDownToItsFirstRevision()
    {
        // An offset with no limit is the one paged read whose page runs to the end of the trail, so
        // its oldest entry has no predecessor because there genuinely is none — not because one
        // fell off a page edge. The empty diff it carries is the true one, and the entries have to
        // match the tail of the unpaged read exactly.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("History Offset No Limit", 5);

        // Act
        var trail = await GetTrailAsync(cardId, "offset=2");

        // Assert
        var entries = trail.GetProperty("entries").EnumerateArray().ToArray();
        entries.Select(e => e.GetProperty("revision").GetInt32()).ShouldBe([3, 2, 1]);
        trail.GetProperty("totalCount").GetInt32().ShouldBe(5);
        trail.GetProperty("offset").GetInt32().ShouldBe(2);
        trail.GetProperty("limit").ValueKind.ShouldBe(JsonValueKind.Null);

        entries[2].GetProperty("diff").GetString().ShouldBe(string.Empty);
        entries[0].GetProperty("diff").GetString().ShouldNotBe(string.Empty);
        entries[1].GetProperty("diff").GetString().ShouldNotBe(string.Empty);

        var whole = await GetTrailEntriesAsync(cardId);
        entries
            .Select(e => e.GetProperty("diff").GetString())
            .ShouldBe(whole[2..].Select(e => e.GetProperty("diff").GetString()));
    }

    [Fact]
    public async Task GetHistory_OffsetPastTheEnd_ReturnsNoEntriesAndTheRealCount()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("History Past End", 3);

        // Act
        var trail = await GetTrailAsync(cardId, "offset=50&limit=10");

        // Assert
        trail.GetProperty("entries").GetArrayLength().ShouldBe(0);
        trail.GetProperty("totalCount").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task GetHistory_FromToWithPaging_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("History Pair Paging", 3);

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/history?from=1&to=3&limit=1");

        // Assert — a pair comparison is one object; a caller who thinks they paged it is wrong.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var message = await response.Content.ReadAsStringAsync();
        message.ShouldContain("do not apply to a from/to comparison");
    }

    [Fact]
    public async Task CardDetail_CarriesTheDescriptionHistoryCount_AndItAgreesWithTheTrail()
    {
        // The count exists so a consumer can decide whether a history affordance is worth offering
        // without a second call. If it disagreed with the trail it would be worse than absent — a
        // reader has no way to tell which of the two lied.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateRevisionsAsync("History Count", 5);

        // Act
        var card = await GetCardAsync(cardId);
        var trail = await GetTrailAsync(cardId);

        // Assert
        var count = card.GetProperty("descriptionHistoryCount").GetInt32();
        count.ShouldBe(5);
        count.ShouldBe(trail.GetProperty("totalCount").GetInt32());
        count.ShouldBe(trail.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task GetCard_NeverEditedCard_ReportsZeroHistory()
    {
        // The common case at launch, and the one the count exists to let a consumer detect: no
        // affordance should be offered for a card with nothing to show.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Count Empty", "never edited");

        // Act
        var card = await GetCardAsync(cardId);

        // Assert
        card.GetProperty("descriptionHistoryCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task CardDetail_HistoryCount_GoesFromZeroToTwoOnTheFirstEdit()
    {
        // Never one: a card's first edit records the value that was already there as well as the
        // one replacing it. A consumer rendering "N revisions" needs to expect that jump.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("History Count First Edit", "before");
        (await GetCardAsync(cardId)).GetProperty("descriptionHistoryCount").GetInt32().ShouldBe(0);

        // Act
        await PatchDescriptionAsync(cardId, "after");

        // Assert
        (await GetCardAsync(cardId)).GetProperty("descriptionHistoryCount").GetInt32().ShouldBe(2);
    }

    private async Task<Guid> CreateRevisionsAsync(string name, int revisions)
    {
        // One edit seeds two revisions, so N revisions take N-1 edits.
        var cardId = await CreateCardAsync(name, "v1");

        for (var version = 2; version <= revisions; version++)
        {
            await PatchDescriptionAsync(cardId, string.Create(CultureInfo.InvariantCulture, $"v{version}"));
        }

        return cardId;
    }

    private async Task<JsonElement> GetTrailAsync(Guid cardId, string? query = null)
    {
        var suffix = query is null ? string.Empty : $"?{query}";
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/history{suffix}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
    }

    private Task<Guid> CreateCardAsync(string name, string descriptionMarkdown) =>
        CreateCardAsync(_client, name, descriptionMarkdown);

    private async Task<Guid> CreateCardAsync(HttpClient client, string name, string descriptionMarkdown)
    {
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, _factory.DefaultBoardId);
        var response = await client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name, laneId, descriptionMarkdown }
        );
        response.EnsureSuccessStatusCode();
        var card = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return card.GetProperty("id").GetGuid();
    }

    private async Task PatchDescriptionAsync(Guid cardId, string descriptionMarkdown)
    {
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { descriptionMarkdown });
        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> GetCardAsync(Guid cardId)
    {
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
    }

    private async Task<JsonElement[]> GetTrailEntriesAsync(Guid cardId, string? query = null)
    {
        var suffix = query is null ? string.Empty : $"?{query}";
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/history{suffix}");
        response.EnsureSuccessStatusCode();
        var trail = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return [.. trail.GetProperty("entries").EnumerateArray()];
    }
}
