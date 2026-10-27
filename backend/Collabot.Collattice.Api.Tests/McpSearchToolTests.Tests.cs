using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The MCP search_cards tool closes the gap where REST had the
// cross-board GET /search/cards but MCP search was single-board only (get_cards).
// Both surfaces route through the shared SearchHelper.SearchCardsAsync + the
// CardSummaryBuilder projection, so these tests pin the MCP tool's
// cross-board grouping, the enriched CardSummary return shape, the boardId
// priority ordering, limit clamping, archive exclusion, and auth gating — by
// direct tool-class invocation.
public class McpSearchToolTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly List<IServiceScope> _scopes = [];
    private static int _nextCardPosition = 0;

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private (BoardDbContext Db, SearchTools SearchTools) CreateTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = new McpAuthService(new UserResolver(db));
        return (db, new SearchTools(db, auth));
    }

    private async Task<BoardUser> CreateUserAsync(UserRole role = UserRole.HumanUser)
    {
        using var setupClient = _factory.CreateClient();
        return await TestAuthHelper.CreateUserAsync
        (
            setupClient,
            _factory,
            $"searcher-{role}-{Guid.NewGuid():N}",
            role
        );
    }

    // Creates a card directly on a board's first non-archive (or archive) lane.
    // Returns the card's number so number-search assertions have a handle.
    private async Task<long> CreateCardAsync
    (
        BoardDbContext db,
        Guid boardId,
        BoardUser createdBy,
        string name,
        string description = "",
        bool archived = false
    )
    {
        var lane = await db.Lanes.FirstAsync(l => l.BoardId == boardId && l.IsArchiveLane == archived);
        var defaultSize = await db.CardSizes
            .Where(s => s.BoardId == boardId)
            .OrderBy(s => s.Ordinal)
                .FirstAsync();

        var number = await db.Cards.Where(c => c.BoardId == boardId).Select(c => c.Number).ToListAsync();
        var nextNumber = (number.Count > 0 ? number.Max() : 0) + 1;

        var card = new CardItem
        {
            Id = Guid.NewGuid(),
            BoardId = boardId,
            LaneId = lane.Id,
            SizeId = defaultSize.Id,
            Name = name,
            DescriptionMarkdown = description,
            Number = nextNumber,
            Position = Interlocked.Increment(ref _nextCardPosition),
            CreatedByUserId = createdBy.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastUpdatedByUserId = createdBy.Id,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Cards.Add(card);
        await db.SaveChangesAsync();
        return nextNumber;
    }

    // Creates a second board with one regular lane and the seeded archive lane,
    // plus default sizes — the minimum shape SearchCardsAsync + CardSummaryBuilder need.
    private async Task<Guid> CreateSecondBoardAsync(BoardDbContext db)
    {
        var boardId = Guid.NewGuid();
        db.Boards.Add(new Board
        {
            Id = boardId,
            Name = $"Second Board {Guid.NewGuid():N}",
            Slug = $"second-{Guid.NewGuid():N}",
        });

        db.Lanes.Add(new Lane
        {
            Id = Guid.NewGuid(),
            BoardId = boardId,
            Name = "To Do",
            Position = 0,
            IsArchiveLane = false,
        });
        db.Lanes.Add(new Lane
        {
            Id = Guid.NewGuid(),
            BoardId = boardId,
            Name = "Archive",
            Position = int.MaxValue,
            IsArchiveLane = true,
        });

        db.CardSizes.Add(new CardSize
        {
            Id = Guid.NewGuid(),
            BoardId = boardId,
            Name = "M",
            Ordinal = 0,
        });

        await db.SaveChangesAsync();
        return boardId;
    }

    private static JsonElement[] ParseResults(string result)
    {
        result.ShouldNotStartWith("Error");
        return JsonSerializer.Deserialize<JsonElement[]>(result)!;
    }

    [Fact]
    public async Task SearchCards_ByName_ReturnsBoardGroupedMatch()
    {
        // Arrange
        var (db, searchTools) = CreateTools();
        var user = await CreateUserAsync();
        await CreateCardAsync(db, _factory.DefaultBoardId, user, "McpUniqueNameAlpha269");

        // Act
        var result = await searchTools.SearchCardsAsync(user.AuthKey, "McpUniqueNameAlpha269");

        // Assert
        var results = ParseResults(result);
        results.Length.ShouldBeGreaterThanOrEqualTo(1);
        var group = results.Single(g => g.GetProperty("boardId").GetGuid() == _factory.DefaultBoardId);
        group.GetProperty("boardName").GetString().ShouldNotBeNullOrEmpty();
        group.GetProperty("boardSlug").GetString().ShouldNotBeNullOrEmpty();
        group.GetProperty("cards").EnumerateArray()
            .ShouldContain(c => c.GetProperty("name").GetString() == "McpUniqueNameAlpha269");
    }

    [Fact]
    public async Task SearchCards_TermOnTwoBoards_SpansMultipleBoards()
    {
        // Arrange — same term on two boards; cross-board search returns both groups
        var (db, searchTools) = CreateTools();
        var user = await CreateUserAsync();
        var secondBoardId = await CreateSecondBoardAsync(db);
        await CreateCardAsync(db, _factory.DefaultBoardId, user, "McpCrossBoardTerm269");
        await CreateCardAsync(db, secondBoardId, user, "McpCrossBoardTerm269");

        // Act
        var result = await searchTools.SearchCardsAsync(user.AuthKey, "McpCrossBoardTerm269");

        // Assert
        var results = ParseResults(result);
        var boardIds = results.Select(g => g.GetProperty("boardId").GetGuid()).ToHashSet();
        boardIds.ShouldContain(_factory.DefaultBoardId);
        boardIds.ShouldContain(secondBoardId);
    }

    [Fact]
    public async Task SearchCards_ByCardNumber_ReturnsExactMatch()
    {
        // Arrange
        var (db, searchTools) = CreateTools();
        var user = await CreateUserAsync();
        var number = await CreateCardAsync(db, _factory.DefaultBoardId, user, "McpNumberSearch269");

        // Act — '#N' is the exact card-number lookup
        var result = await searchTools.SearchCardsAsync(user.AuthKey, $"#{number.ToString(CultureInfo.InvariantCulture)}");

        // Assert
        var results = ParseResults(result);
        var group = results.Single(g => g.GetProperty("boardId").GetGuid() == _factory.DefaultBoardId);
        var cards = group.GetProperty("cards");
        cards.GetArrayLength().ShouldBe(1);
        cards[0].GetProperty("number").GetInt64().ShouldBe(number);
    }

    [Fact]
    public async Task SearchCards_EmptyQuery_ReturnsEmptyArray()
    {
        // Arrange
        var (_, searchTools) = CreateTools();
        var user = await CreateUserAsync();

        // Act
        var result = await searchTools.SearchCardsAsync(user.AuthKey, "   ");

        // Assert
        ParseResults(result).ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchCards_NoMatch_ReturnsEmptyArray()
    {
        // Arrange
        var (_, searchTools) = CreateTools();
        var user = await CreateUserAsync();

        // Act
        var result = await searchTools.SearchCardsAsync(user.AuthKey, "zzMcpNonExistentTermZz269");

        // Assert
        ParseResults(result).ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchCards_Limit100_IsClampedToMax50()
    {
        // Arrange
        var (db, searchTools) = CreateTools();
        var user = await CreateUserAsync();
        await CreateCardAsync(db, _factory.DefaultBoardId, user, "McpLimitClampCard269");

        // Act — request limit=100; the tool clamps to 50, so the call still succeeds
        var result = await searchTools.SearchCardsAsync(user.AuthKey, "McpLimitClampCard269", limit: 100);

        // Assert
        var results = ParseResults(result);
        results.Length.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task SearchCards_WithBoardId_PrioritizesThatBoardFirst()
    {
        // Arrange — matching cards on two boards; boardId names the priority board
        var (db, searchTools) = CreateTools();
        var user = await CreateUserAsync();
        var secondBoardId = await CreateSecondBoardAsync(db);
        await CreateCardAsync(db, _factory.DefaultBoardId, user, "McpPriorityTerm269");
        await CreateCardAsync(db, secondBoardId, user, "McpPriorityTerm269");

        // Act — prioritize the second board
        var result = await searchTools.SearchCardsAsync(user.AuthKey, "McpPriorityTerm269", boardId: secondBoardId);

        // Assert — the named board ranks first
        var results = ParseResults(result);
        results.Length.ShouldBe(2);
        results[0].GetProperty("boardId").GetGuid().ShouldBe(secondBoardId);
        results[1].GetProperty("boardId").GetGuid().ShouldBe(_factory.DefaultBoardId);
    }

    [Fact]
    public async Task SearchCards_DefaultOptions_ExcludesArchivedCards()
    {
        // Arrange — one live card and one archived card share the term
        var (db, searchTools) = CreateTools();
        var user = await CreateUserAsync();
        await CreateCardAsync(db, _factory.DefaultBoardId, user, "McpArchiveTerm269 live");
        await CreateCardAsync(db, _factory.DefaultBoardId, user, "McpArchiveTerm269 archived", archived: true);

        // Act
        var result = await searchTools.SearchCardsAsync(user.AuthKey, "McpArchiveTerm269");

        // Assert — only the live card surfaces
        var results = ParseResults(result);
        var group = results.Single(g => g.GetProperty("boardId").GetGuid() == _factory.DefaultBoardId);
        var names = group.GetProperty("cards").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
                .ToList();
        names.ShouldContain("McpArchiveTerm269 live");
        names.ShouldNotContain("McpArchiveTerm269 archived");
    }

    [Fact]
    public async Task SearchCards_WithArchiveBoardId_IncludesThatBoardsArchivedCards()
    {
        // Arrange
        var (db, searchTools) = CreateTools();
        var user = await CreateUserAsync();
        await CreateCardAsync(db, _factory.DefaultBoardId, user, "McpArchiveBoardTerm269 archived", archived: true);

        // Act — name the board whose archived cards should be included
        var result = await searchTools.SearchCardsAsync
        (
            user.AuthKey,
            "McpArchiveBoardTerm269",
            archiveBoardId: _factory.DefaultBoardId
        );

        // Assert — the archived card now surfaces
        var results = ParseResults(result);
        var group = results.Single(g => g.GetProperty("boardId").GetGuid() == _factory.DefaultBoardId);
        group.GetProperty("cards").EnumerateArray()
            .ShouldContain(c => c.GetProperty("name").GetString() == "McpArchiveBoardTerm269 archived");
    }

    [Fact]
    public async Task SearchCards_MatchingCard_CardSummaryCarriesEnrichedFields()
    {
        // Arrange
        var (db, searchTools) = CreateTools();
        var user = await CreateUserAsync();
        await CreateCardAsync(db, _factory.DefaultBoardId, user, "McpFieldCheck269");

        // Act
        var result = await searchTools.SearchCardsAsync(user.AuthKey, "McpFieldCheck269");

        // Assert — the shared CardSummary projection shape rides through MCP
        var results = ParseResults(result);
        var card = results
            .Single(g => g.GetProperty("boardId").GetGuid() == _factory.DefaultBoardId)
            .GetProperty("cards")[0];
        card.TryGetProperty("id", out _).ShouldBeTrue();
        card.TryGetProperty("number", out _).ShouldBeTrue();
        card.TryGetProperty("name", out _).ShouldBeTrue();
        card.TryGetProperty("sizeName", out _).ShouldBeTrue();
        card.TryGetProperty("labels", out _).ShouldBeTrue();
        card.TryGetProperty("commentCount", out _).ShouldBeTrue();
        card.TryGetProperty("attachmentCount", out _).ShouldBeTrue();
        card.TryGetProperty("isArchived", out _).ShouldBeTrue();
        card.TryGetProperty("latestComment", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task SearchCards_InvalidAuthKey_ReturnsError()
    {
        // Arrange
        var (_, searchTools) = CreateTools();

        // Act
        var result = await searchTools.SearchCardsAsync("not-a-real-key", "anything");

        // Assert
        result.ShouldStartWith("Error");
    }
}
