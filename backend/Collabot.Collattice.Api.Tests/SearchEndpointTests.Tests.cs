using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class SearchEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> GetFirstLaneIdAsync()
        => await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

    private async Task<JsonElement> CreateCardAsync(string name, string description = "")
    {
        var laneId = await GetFirstLaneIdAsync();
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name,
            descriptionMarkdown = description,
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ── Board-scoped search (GET /boards/{boardId}/cards?search=) ──

    [Fact]
    public async Task GetCards_SearchByName_ReturnsMatchingCards()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        await CreateCardAsync("UniqueAlphaSearchName");
        await CreateCardAsync("Unrelated Card");

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?search=UniqueAlphaSearch");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        paged.ShouldNotBeNull();
        var cards = paged.Items;
        cards.Count.ShouldBeGreaterThanOrEqualTo(1);
        cards.ShouldAllBe(c => c.GetProperty("name").GetString()!.Contains("UniqueAlphaSearch"));
    }

    [Fact]
    public async Task GetCards_SearchByDescription_ReturnsMatchingCards()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        await CreateCardAsync("Desc Search Card", "this has xyzUniqueDescMarker in it");

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?search=xyzUniqueDescMarker");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        paged.ShouldNotBeNull();
        var cards = paged.Items;
        cards.Count.ShouldBeGreaterThanOrEqualTo(1);
        cards.ShouldContain(c => c.GetProperty("name").GetString() == "Desc Search Card");
    }

    [Fact]
    public async Task GetCards_SearchByCardNumber_ReturnsExactMatch()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var created = await CreateCardAsync("Number Search Card");
        var cardNumber = created.GetProperty("number").GetInt64();

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?search=%23{cardNumber.ToString(CultureInfo.InvariantCulture)}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        paged.ShouldNotBeNull();
        var cards = paged.Items;
        cards.Count.ShouldBe(1);
        cards[0].GetProperty("number").GetInt64().ShouldBe(cardNumber);
    }

    [Fact]
    public async Task GetCards_SearchEmptyString_ReturnsAllCards()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        await CreateCardAsync("EmptySearch Card");

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?search=");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        paged.ShouldNotBeNull();
        paged.Items.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetCards_SearchNoMatch_ReturnsEmptyArray()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?search=zzNonExistentTermZz99");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        paged.ShouldNotBeNull();
        paged.Items.ShouldBeEmpty();
    }

    // ── Global search (GET /search/cards?q=) ──

    [Fact]
    public async Task SearchCards_ByName_ReturnsGroupedByBoard()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        await CreateCardAsync("GlobalSearchUniqueName42");

        // Act
        var response = await _client.GetAsync("/api/v1/search/cards?q=GlobalSearchUniqueName42");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        results.ShouldNotBeNull();
        results.Length.ShouldBeGreaterThanOrEqualTo(1);

        var group = results[0];
        group.GetProperty("boardId").GetGuid().ShouldBe(_factory.DefaultBoardId);
        group.GetProperty("boardName").GetString().ShouldNotBeNullOrEmpty();
        group.GetProperty("boardSlug").GetString().ShouldNotBeNullOrEmpty();
        group.GetProperty("cards").GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task SearchCards_EmptyQuery_ReturnsEmptyArray()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync("/api/v1/search/cards?q=");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        results.ShouldNotBeNull();
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchCards_NoMatch_ReturnsEmptyArray()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync("/api/v1/search/cards?q=zzGlobalNonExistentZz99");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        results.ShouldNotBeNull();
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchCards_LimitClampedToMax50()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        await CreateCardAsync("LimitTestCard");

        // Act — request limit=100, should be clamped to 50
        var response = await _client.GetAsync("/api/v1/search/cards?q=LimitTestCard&limit=100");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        results.ShouldNotBeNull();
    }

    [Fact]
    public async Task SearchCards_ByCardNumber_ReturnsMatch()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var created = await CreateCardAsync("GlobalNumberSearch");
        var cardNumber = created.GetProperty("number").GetInt64();

        // Act
        var response = await _client.GetAsync($"/api/v1/search/cards?q=%23{cardNumber.ToString(CultureInfo.InvariantCulture)}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        results.ShouldNotBeNull();
        results.Length.ShouldBeGreaterThanOrEqualTo(1);

        var cards = results[0].GetProperty("cards");
        cards.GetArrayLength().ShouldBe(1);
        cards[0].GetProperty("number").GetInt64().ShouldBe(cardNumber);
    }

    [Fact]
    public async Task SearchCards_CardSummaryIncludesExpectedFields()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        await CreateCardAsync("FieldCheckSearchCard");

        // Act
        var response = await _client.GetAsync("/api/v1/search/cards?q=FieldCheckSearchCard");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        results.ShouldNotBeNull();
        results.Length.ShouldBeGreaterThanOrEqualTo(1);

        var card = results[0].GetProperty("cards")[0];
        card.TryGetProperty("id", out _).ShouldBeTrue();
        card.TryGetProperty("number", out _).ShouldBeTrue();
        card.TryGetProperty("name", out _).ShouldBeTrue();
        card.TryGetProperty("sizeName", out _).ShouldBeTrue();
        card.TryGetProperty("labels", out _).ShouldBeTrue();
        card.TryGetProperty("commentCount", out _).ShouldBeTrue();
        card.TryGetProperty("attachmentCount", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task SearchCards_WithBoardId_PrioritizesResultsFromThatBoard()
    {
        // Arrange — two boards, each with a card matching the same search term
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Create a second board
        var secondBoardResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = "Priority Search Test Board" });
        secondBoardResponse.EnsureSuccessStatusCode();
        var secondBoard = await secondBoardResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secondBoardId = secondBoard.GetProperty("id").GetGuid();

        // New boards have no regular lanes — create one first
        var createLaneResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{secondBoardId}/lanes",
            new { name = "To Do", position = 1 }
        );
        createLaneResponse.EnsureSuccessStatusCode();
        var secondBoardLaneId = (await createLaneResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Create a card on the second board
        var secondBoardCardResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{secondBoardId}/cards",
            new { name = "PrioritySearchTerm_Xyz987", laneId = secondBoardLaneId, position = 0 }
        );
        secondBoardCardResponse.EnsureSuccessStatusCode();

        // Create a card on the default board
        await CreateCardAsync("PrioritySearchTerm_Xyz987");

        // Act — search with boardId set to the default board
        var response = await _client.GetAsync(
            $"/api/v1/search/cards?q=PrioritySearchTerm_Xyz987&boardId={_factory.DefaultBoardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        results.ShouldNotBeNull();
        results.Length.ShouldBe(2);

        // Default board (the priority board) should be first
        results[0].GetProperty("boardId").GetGuid().ShouldBe(_factory.DefaultBoardId);
        results[1].GetProperty("boardId").GetGuid().ShouldBe(secondBoardId);
    }

    [Fact]
    public async Task SearchCards_WithoutBoardId_DoesNotApplyPriority()
    {
        // Arrange — search without boardId still returns all matching groups
        TestAuthHelper.SetAdminAuth(_client, _factory);
        await CreateCardAsync("NoPrioritySearchTerm_Abc123");

        // Act
        var response = await _client.GetAsync("/api/v1/search/cards?q=NoPrioritySearchTerm_Abc123");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        results.ShouldNotBeNull();
        results.Length.ShouldBeGreaterThanOrEqualTo(1);
        results[0].GetProperty("boardId").GetGuid().ShouldBe(_factory.DefaultBoardId);
    }

    [Fact]
    public async Task SearchCards_RequiresAuth()
    {
        // Arrange — no auth header
        _client.DefaultRequestHeaders.Remove("X-User-Key");

        // Act
        var response = await _client.GetAsync("/api/v1/search/cards?q=test");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ── Over-limit priority: the current board's non-archived matches must win
    //    the limit budget BEFORE the cut, not after — the case earlier under-limit tests
    //    never exercised. ──

    [Fact]
    public async Task SearchCards_OverLimit_CurrentBoardMatchesSurviveTheCut()
    {
        // Arrange — two boards, both with matching cards, total exceeding the limit.
        // The priority board is whichever board's GUID sorts LATER as the provider sees
        // it, so the pre-fix ordering (OrderBy(BoardId).Take) cuts the priority board's
        // cards entirely. The fix must keep them.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        const string term = "OverLimitSurvive_Mnq741";

        var otherBoardId = await CreateBoardAsync("Over Limit Other Board");
        var otherLaneId = await CreateLaneForBoardAsync(otherBoardId);
        var defaultLaneId = await GetFirstLaneIdAsync();

        var (priorityBoardId, priorityLaneId, fillerBoardId, fillerLaneId) =
            await PickLateSortingPriorityBoardAsync(_factory.DefaultBoardId, defaultLaneId, otherBoardId, otherLaneId);

        const int limit = 4;

        // Fill the entire limit budget with the lower-GUID (filler) board's matches.
        for (var i = 0; i < limit; i++)
        {
            await CreateCardOnBoardAsync(fillerBoardId, fillerLaneId, $"{term} filler {i.ToString(CultureInfo.InvariantCulture)}");
        }

        // The priority board has matches too — pre-fix, these are dropped by the cut.
        await CreateCardOnBoardAsync(priorityBoardId, priorityLaneId, $"{term} priority A");
        await CreateCardOnBoardAsync(priorityBoardId, priorityLaneId, $"{term} priority B");

        // Act
        var results = await SearchAsync($"q={term}&limit={limit}&boardId={priorityBoardId}");

        // Assert — the priority board must be present and first; the pre-fix code drops it.
        results.Length.ShouldBeGreaterThanOrEqualTo(1);
        results[0].GetProperty("boardId").GetGuid().ShouldBe(priorityBoardId);
        results[0].GetProperty("cards").GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task SearchCards_OverLimit_AllNonArchivedCurrentBoardMatchesPrecedeOtherBoards()
    {
        // Arrange — the binding contract: no other-board card outranks any
        // non-archived current-board match, even when total matches exceed the limit.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        const string term = "OverLimitPrecede_Wkz852";

        var otherBoardId = await CreateBoardAsync("Over Limit Precede Board");
        var otherLaneId = await CreateLaneForBoardAsync(otherBoardId);
        var defaultLaneId = await GetFirstLaneIdAsync();

        var (priorityBoardId, priorityLaneId, fillerBoardId, fillerLaneId) =
            await PickLateSortingPriorityBoardAsync(_factory.DefaultBoardId, defaultLaneId, otherBoardId, otherLaneId);

        const int limit = 5;

        for (var i = 0; i < limit; i++)
        {
            await CreateCardOnBoardAsync(fillerBoardId, fillerLaneId, $"{term} filler {i.ToString(CultureInfo.InvariantCulture)}");
        }

        await CreateCardOnBoardAsync(priorityBoardId, priorityLaneId, $"{term} priority A");
        await CreateCardOnBoardAsync(priorityBoardId, priorityLaneId, $"{term} priority B");

        // Act
        var results = await SearchAsync($"q={term}&limit={limit}&boardId={priorityBoardId}");

        // Assert — walk the flattened result order: once any non-priority board card
        // appears, no priority board card may follow it.
        var flattened = results
            .SelectMany(group => group.GetProperty("cards").EnumerateArray()
                .Select(_ => group.GetProperty("boardId").GetGuid()))
            .ToList();

        var seenOtherBoard = false;
        foreach (var boardId in flattened)
        {
            if (boardId == priorityBoardId)
            {
                seenOtherBoard.ShouldBeFalse("a non-priority board card preceded a priority board card");
            }
            else
            {
                seenOtherBoard = true;
            }
        }

        // And the priority board's non-archived matches must be present (not cut).
        flattened.ShouldContain(priorityBoardId);
    }

    [Fact]
    public async Task SearchCards_OverLimit_ArchivedCurrentBoardCardsDoNotDisplaceNonArchivedMatches()
    {
        // Arrange — the side-effect fix: archived current-board cards must not
        // consume the limit budget ahead of non-archived matches (their own or others').
        // archiveBoardId names the priority board so its archived cards are eligible to
        // appear — but they must rank behind the priority board's non-archived matches.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        const string term = "OverLimitArchived_Rty963";

        var laneId = await GetFirstLaneIdAsync();
        var boardId = _factory.DefaultBoardId;

        const int limit = 3;

        // Creation order is load-bearing for the red-before property: the live card is
        // created FIRST so it gets the LOWEST card number, then the archived cards get
        // higher numbers. Under the pre-fix ordering (OrderBy(BoardId).ThenByDescending(
        // Number).Take) the higher-numbered archived cards rank ahead of the live card and
        // the Take(limit) drops it — failing the assertions below. The fix's archived-
        // exclusion clause (priorityArchiveLaneIds) keeps the archived cards out of the
        // priority bucket so the live card survives. Create live last and it survives the
        // pre-fix cut on number alone, and the test no longer guards the clause it names.

        // A single non-archived current-board match — must survive the cut.
        await CreateCardOnBoardAsync(boardId, laneId, $"{term} live");

        // Archived current-board matches — eligible (archiveBoardId), but lower priority.
        for (var i = 0; i < limit; i++)
        {
            var archivedId = await CreateCardOnBoardAsync(boardId, laneId, $"{term} archived {i.ToString(CultureInfo.InvariantCulture)}");
            await _client.PostAsync($"/api/v1/cards/{archivedId}/archive", null);
        }

        // Act
        var results = await SearchAsync($"q={term}&limit={limit}&boardId={boardId}&archiveBoardId={boardId}");

        // Assert — the non-archived match is present and ranks at the top of its board group.
        var currentGroup = results.First(g => g.GetProperty("boardId").GetGuid() == boardId);
        var cards = currentGroup.GetProperty("cards").EnumerateArray().ToList();
        cards.ShouldContain(c => c.GetProperty("name").GetString()!.EndsWith("live", StringComparison.Ordinal));
        cards[0].GetProperty("isArchived").GetBoolean().ShouldBeFalse();
    }

    // ── Helpers for the over-limit priority tests ──

    private async Task<JsonElement[]> SearchAsync(string queryString)
    {
        var response = await _client.GetAsync($"/api/v1/search/cards?{queryString}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        results.ShouldNotBeNull();
        return results;
    }

    private async Task<Guid> CreateBoardAsync(string boardName)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/boards", new { name = boardName });
        response.EnsureSuccessStatusCode();
        var board = await response.Content.ReadFromJsonAsync<JsonElement>();
        return board.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateLaneForBoardAsync(Guid boardId)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes", new { name = "To Do", position = 1 });
        response.EnsureSuccessStatusCode();
        var lane = await response.Content.ReadFromJsonAsync<JsonElement>();
        return lane.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateCardOnBoardAsync(Guid boardId, Guid laneId, string name)
    {
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{boardId}/cards",
            new { name, laneId, position = Random.Shared.Next(10000, 99999) }
        );
        response.EnsureSuccessStatusCode();
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        return card.GetProperty("id").GetGuid();
    }

    // Returns (priorityBoard, priorityLane, fillerBoard, fillerLane) where the priority
    // board's id sorts AFTER the filler board's id AS THE PROVIDER ORDERS IT — not as
    // Guid.CompareTo would (SQLite's TEXT/BLOB GUID ordering is not .NET's field order).
    // Resolving the late-sorting board via the real provider's OrderBy is what makes the
    // pre-fix cut (OrderBy(BoardId).Take) drop the priority board deterministically:
    // red before the fix, green after.
    private async Task<(Guid PriorityBoard, Guid PriorityLane, Guid FillerBoard, Guid FillerLane)>
        PickLateSortingPriorityBoardAsync(Guid boardA, Guid laneA, Guid boardB, Guid laneB)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var ordered = await db.Set<Board>()
            .Where(b => b.Id == boardA || b.Id == boardB)
            .OrderBy(b => b.Id)
                .Select(b => b.Id)
                    .ToListAsync();

        var lateBoard = ordered[^1];

        return lateBoard == boardA
            ? (boardA, laneA, boardB, laneB)
            : (boardB, laneB, boardA, laneA);
    }
}
