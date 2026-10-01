using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The two prune MCP tools (prune_preview, prune). Both gate
// via RequireAdminLevelAsync and share PruneFilter with the REST PruneEndpoints,
// so this file exercises:
//   - the role-gate matrix per tool (Administrator + AgentAdministrator succeed on
//     the positive path; HumanUser + AgentUser are rejected — both admin roles AND
//     both non-admin roles are covered on positive AND negative paths),
//   - filter semantics mirrored from REST (at-least-one-filter, lane/label/olderThan
//     match, includeArchived default-exclude),
//   - the archive-only contract (prune archives, never deletes; no action param),
//   - CSV / JSON-array GUID parsing and malformed-GUID rejection.
public class McpPruneToolTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly List<IServiceScope> _scopes = [];
    private const string _adminPrivilegeError = "Error: This operation requires administrator privileges.";
    private const string _noFilterError = "Error: At least one filter is required (olderThan, laneIds, or labelIds).";

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private (BoardDbContext Db, PruneTools Prune) CreateTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = new McpAuthService(new UserResolver(db));
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var webhookSink = scope.ServiceProvider.GetRequiredService<IWebhookSink>();
        return (db, new PruneTools(db, auth, broadcaster, webhookSink));
    }

    private async Task<string> AuthKeyForAsync(UserRole role)
    {
        if (role == UserRole.Administrator)
        {
            return _factory.AdminAuthKey;
        }

        using var setupClient = _factory.CreateClient();
        var user = await TestAuthHelper.CreateUserAsync
        (
            setupClient,
            _factory,
            $"prunetool-{role}-{Guid.NewGuid():N}",
            role
        );
        return user.AuthKey;
    }

    private static int _nextLanePosition = 50_000;
    private static int NextLanePosition() => Interlocked.Increment(ref _nextLanePosition);

    // Each test creates its own board so card-match counts are isolated from the
    // shared default board's fixtures and from sibling tests running in parallel.
    private async Task<(Guid BoardId, Guid LaneId, Guid SizeId, Guid ArchiveLaneId)> CreateBoardAsync(BoardDbContext db)
    {
        var boardId = Guid.NewGuid();
        var board = new Board
        {
            Id = boardId,
            Name = $"Prune Board {boardId:N}",
            Slug = $"prune-board-{boardId:N}",
        };
        db.Boards.Add(board);

        var lane = new Lane { Id = Guid.NewGuid(), BoardId = boardId, Name = "Work", Position = NextLanePosition() };
        var archiveLane = new Lane { Id = Guid.NewGuid(), BoardId = boardId, Name = "Archive", Position = int.MaxValue, IsArchiveLane = true };
        db.Lanes.AddRange(lane, archiveLane);

        var size = new CardSize { Id = Guid.NewGuid(), BoardId = boardId, Name = "M", Ordinal = 0 };
        db.CardSizes.Add(size);

        await db.SaveChangesAsync();
        return (boardId, lane.Id, size.Id, archiveLane.Id);
    }

    // Card numbers are unique per board. A monotonic counter guarantees distinct values; a random
    // pick collided on the (BoardId, Number) index (birthday paradox), which read as an intermittent
    // CI failure on unchanged code.
    private static int _nextCardNumber = 500_000;
    private static int NextCardNumber() => Interlocked.Increment(ref _nextCardNumber);

    private async Task<CardItem> AddCardAsync
    (
        BoardDbContext db,
        Guid boardId,
        Guid laneId,
        Guid sizeId,
        DateTimeOffset? lastUpdated = null
    )
    {
        var adminId = await db.Users.Where(u => u.Role == UserRole.Administrator).Select(u => u.Id).FirstAsync();
        var stamp = lastUpdated ?? DateTimeOffset.UtcNow;
        var card = new CardItem
        {
            Id = Guid.NewGuid(),
            BoardId = boardId,
            LaneId = laneId,
            SizeId = sizeId,
            Name = "Fixture Card",
            Number = NextCardNumber(),
            Position = 0,
            CreatedByUserId = adminId,
            CreatedAtUtc = stamp,
            LastUpdatedByUserId = adminId,
            LastUpdatedAtUtc = stamp,
        };
        db.Cards.Add(card);
        await db.SaveChangesAsync();
        return card;
    }

    // prune_preview — role gate (positive: both admin roles)

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task PrunePreview_AdminLevel_ReturnsMatches(UserRole role)
    {
        var (db, prune) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var (boardId, laneId, sizeId, _) = await CreateBoardAsync(db);
        await AddCardAsync(db, boardId, laneId, sizeId);
        await AddCardAsync(db, boardId, laneId, sizeId);

        var result = await prune.PrunePreviewAsync(authKey, boardId, laneIds: laneId.ToString());

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        json.GetProperty("matchCount").GetInt32().ShouldBe(2);
        json.GetProperty("cards").GetArrayLength().ShouldBe(2);
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task PrunePreview_NonAdmin_ReturnsError(UserRole role)
    {
        var (db, prune) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var (boardId, laneId, sizeId, _) = await CreateBoardAsync(db);
        await AddCardAsync(db, boardId, laneId, sizeId);

        var result = await prune.PrunePreviewAsync(authKey, boardId, laneIds: laneId.ToString());

        result.ShouldBe(_adminPrivilegeError);
    }

    [Fact]
    public async Task PrunePreview_NoChangesPersisted()
    {
        var (db, prune) = CreateTools();
        var (boardId, laneId, sizeId, _) = await CreateBoardAsync(db);
        var card = await AddCardAsync(db, boardId, laneId, sizeId);

        await prune.PrunePreviewAsync(_factory.AdminAuthKey, boardId, laneIds: laneId.ToString());

        db.ChangeTracker.Clear();
        (await db.Cards.FindAsync(card.Id))!.LaneId.ShouldBe(laneId, "preview must not move cards");
    }

    // prune — role gate (positive: both admin roles; archives, never deletes)

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task Prune_AdminLevel_ArchivesMatches(UserRole role)
    {
        var (db, prune) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var (boardId, laneId, sizeId, archiveLaneId) = await CreateBoardAsync(db);
        var cardA = await AddCardAsync(db, boardId, laneId, sizeId);
        var cardB = await AddCardAsync(db, boardId, laneId, sizeId);

        var result = await prune.PruneAsync(authKey, boardId, laneIds: laneId.ToString());

        JsonSerializer.Deserialize<JsonElement>(result).GetProperty("archivedCount").GetInt32().ShouldBe(2);
        db.ChangeTracker.Clear();
        // Archived, not deleted — both rows survive and now sit in the archive lane.
        (await db.Cards.FindAsync(cardA.Id))!.LaneId.ShouldBe(archiveLaneId);
        (await db.Cards.FindAsync(cardB.Id))!.LaneId.ShouldBe(archiveLaneId);
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task Prune_NonAdmin_ReturnsErrorAndDoesNotArchive(UserRole role)
    {
        var (db, prune) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var (boardId, laneId, sizeId, _) = await CreateBoardAsync(db);
        var card = await AddCardAsync(db, boardId, laneId, sizeId);

        var result = await prune.PruneAsync(authKey, boardId, laneIds: laneId.ToString());

        result.ShouldBe(_adminPrivilegeError);
        db.ChangeTracker.Clear();
        (await db.Cards.FindAsync(card.Id))!.LaneId.ShouldBe(laneId, "non-admin prune must not move cards");
    }

    [Fact]
    public async Task Prune_OnlyMovesMatchingCards()
    {
        var (db, prune) = CreateTools();
        var (boardId, laneId, sizeId, archiveLaneId) = await CreateBoardAsync(db);
        var otherLane = new Lane { Id = Guid.NewGuid(), BoardId = boardId, Name = "Other", Position = NextLanePosition() };
        db.Lanes.Add(otherLane);
        await db.SaveChangesAsync();

        var matched = await AddCardAsync(db, boardId, laneId, sizeId);
        var unmatched = await AddCardAsync(db, boardId, otherLane.Id, sizeId);

        var result = await prune.PruneAsync(_factory.AdminAuthKey, boardId, laneIds: laneId.ToString());

        JsonSerializer.Deserialize<JsonElement>(result).GetProperty("archivedCount").GetInt32().ShouldBe(1);
        db.ChangeTracker.Clear();
        (await db.Cards.FindAsync(matched.Id))!.LaneId.ShouldBe(archiveLaneId);
        (await db.Cards.FindAsync(unmatched.Id))!.LaneId.ShouldBe(otherLane.Id, "non-matching cards stay put");
    }

    // Filter semantics — mirrored from REST PruneFilter

    [Fact]
    public async Task Prune_NoFilter_ReturnsError()
    {
        var (db, prune) = CreateTools();
        var (boardId, laneId, sizeId, _) = await CreateBoardAsync(db);
        var card = await AddCardAsync(db, boardId, laneId, sizeId);

        var result = await prune.PruneAsync(_factory.AdminAuthKey, boardId);

        result.ShouldBe(_noFilterError);
        db.ChangeTracker.Clear();
        (await db.Cards.FindAsync(card.Id))!.LaneId.ShouldBe(laneId, "a no-filter prune must move nothing");
    }

    [Fact]
    public async Task PrunePreview_NoFilter_ReturnsError()
    {
        var (db, prune) = CreateTools();
        var (boardId, _, _, _) = await CreateBoardAsync(db);

        var result = await prune.PrunePreviewAsync(_factory.AdminAuthKey, boardId);

        result.ShouldBe(_noFilterError);
    }

    [Fact]
    public async Task PrunePreview_OlderThan_MatchesOnlyOlderCards()
    {
        var (db, prune) = CreateTools();
        var (boardId, laneId, sizeId, _) = await CreateBoardAsync(db);
        await AddCardAsync(db, boardId, laneId, sizeId, lastUpdated: DateTimeOffset.UtcNow.AddDays(-30));
        await AddCardAsync(db, boardId, laneId, sizeId, lastUpdated: DateTimeOffset.UtcNow);

        var result = await prune.PrunePreviewAsync(_factory.AdminAuthKey, boardId, olderThan: DateTimeOffset.UtcNow.AddDays(-7));

        JsonSerializer.Deserialize<JsonElement>(result).GetProperty("matchCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task PrunePreview_LabelFilter_MatchesLabeledCards()
    {
        var (db, prune) = CreateTools();
        var (boardId, laneId, sizeId, _) = await CreateBoardAsync(db);
        var label = new Label { Id = Guid.NewGuid(), BoardId = boardId, Name = "Stale", Color = "#888888" };
        db.Labels.Add(label);
        var labeled = await AddCardAsync(db, boardId, laneId, sizeId);
        await AddCardAsync(db, boardId, laneId, sizeId);
        db.CardLabels.Add(new CardLabel { CardId = labeled.Id, LabelId = label.Id });
        await db.SaveChangesAsync();

        var result = await prune.PrunePreviewAsync(_factory.AdminAuthKey, boardId, labelIds: label.Id.ToString());

        JsonSerializer.Deserialize<JsonElement>(result).GetProperty("matchCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task PrunePreview_ExcludesArchivedByDefault()
    {
        var (db, prune) = CreateTools();
        var (boardId, laneId, sizeId, archiveLaneId) = await CreateBoardAsync(db);
        await AddCardAsync(db, boardId, laneId, sizeId);
        await AddCardAsync(db, boardId, archiveLaneId, sizeId);

        var olderThan = DateTimeOffset.UtcNow.AddDays(1);
        var resultDefault = await prune.PrunePreviewAsync(_factory.AdminAuthKey, boardId, olderThan: olderThan);
        var resultIncluded = await prune.PrunePreviewAsync(_factory.AdminAuthKey, boardId, olderThan: olderThan, includeArchived: true);

        JsonSerializer.Deserialize<JsonElement>(resultDefault).GetProperty("matchCount").GetInt32().ShouldBe(1);
        JsonSerializer.Deserialize<JsonElement>(resultIncluded).GetProperty("matchCount").GetInt32().ShouldBe(2);
    }

    // CSV / JSON-array GUID parsing + malformed rejection

    [Fact]
    public async Task PrunePreview_JsonArrayLaneIds_Matches()
    {
        var (db, prune) = CreateTools();
        var (boardId, laneId, sizeId, _) = await CreateBoardAsync(db);
        var otherLane = new Lane { Id = Guid.NewGuid(), BoardId = boardId, Name = "Other", Position = NextLanePosition() };
        db.Lanes.Add(otherLane);
        await db.SaveChangesAsync();
        await AddCardAsync(db, boardId, laneId, sizeId);
        await AddCardAsync(db, boardId, otherLane.Id, sizeId);

        var jsonArray = $"[\"{laneId}\",\"{otherLane.Id}\"]";
        var result = await prune.PrunePreviewAsync(_factory.AdminAuthKey, boardId, laneIds: jsonArray);

        JsonSerializer.Deserialize<JsonElement>(result).GetProperty("matchCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task PrunePreview_MalformedLaneId_ReturnsError()
    {
        var (db, prune) = CreateTools();
        var (boardId, _, _, _) = await CreateBoardAsync(db);

        var result = await prune.PrunePreviewAsync(_factory.AdminAuthKey, boardId, laneIds: "not-a-guid");

        result.ShouldBe("Error: Invalid ID format: 'not-a-guid'. Expected a GUID.");
    }

    [Fact]
    public async Task Prune_MalformedLabelId_ReturnsErrorAndDoesNotArchive()
    {
        var (db, prune) = CreateTools();
        var (boardId, laneId, sizeId, _) = await CreateBoardAsync(db);
        var card = await AddCardAsync(db, boardId, laneId, sizeId);

        var result = await prune.PruneAsync(_factory.AdminAuthKey, boardId, labelIds: "abc,def");

        result.ShouldBe("Error: Invalid ID format: 'abc'. Expected a GUID.");
        db.ChangeTracker.Clear();
        (await db.Cards.FindAsync(card.Id))!.LaneId.ShouldBe(laneId);
    }

    // Non-UTC olderThan normalisation

    [Fact]
    public async Task PrunePreview_NonUtcOlderThan_NormalisesToUtcBeforeComparing()
    {
        // The model-wide DateTimeOffset value converter writes every
        // DateTimeOffset as a normalised-UTC ISO-8601 string (.ToUniversalTime()).
        // A non-UTC olderThan value must therefore also be normalised before the
        // TEXT comparison, which the LINQ form achieves automatically (the converter
        // runs on both sides). The prior FromSqlInterpolated workaround called
        // .ToString("O") without .ToUniversalTime(), which would compare the stored
        // UTC string against a non-UTC literal and produce wrong results.
        //
        // Concretely: a card at 03:00 UTC should match an olderThan cutoff of
        // 05:00 UTC regardless of whether that cutoff is expressed in UTC or as an
        // equivalent non-UTC offset (e.g. 00:00-05:00 = 05:00 UTC). The old code
        // would have missed the card when the cutoff was expressed as 00:00-05:00
        // because "2026-...T03:00Z" > "2026-...T00:00-05:00" as a raw string
        // comparison.
        var (db, prune) = CreateTools();
        var (boardId, laneId, sizeId, _) = await CreateBoardAsync(db);

        // Card last updated at 03:00 UTC
        var cardUtcStamp = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);

        await AddCardAsync(db, boardId, laneId, sizeId, lastUpdated: cardUtcStamp);

        // Cutoff is 05:00 UTC expressed as 00:00 at UTC-5 — same instant, different offset.
        // The card at 03:00 UTC is before this cutoff and should be matched.
        var cutoffNonUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-5));

        var result = await prune.PrunePreviewAsync(_factory.AdminAuthKey, boardId, olderThan: cutoffNonUtc);

        JsonSerializer.Deserialize<JsonElement>(result).GetProperty("matchCount").GetInt32().ShouldBe(1);
    }

    // Board existence

    [Fact]
    public async Task PrunePreview_UnknownBoard_ReturnsError()
    {
        var (_, prune) = CreateTools();

        var result = await prune.PrunePreviewAsync(_factory.AdminAuthKey, Guid.NewGuid(), laneIds: Guid.NewGuid().ToString());

        result.ShouldBe("Error: Board not found.");
    }

    [Fact]
    public async Task Prune_UnknownBoard_ReturnsError()
    {
        var (_, prune) = CreateTools();

        var result = await prune.PruneAsync(_factory.AdminAuthKey, Guid.NewGuid(), laneIds: Guid.NewGuid().ToString());

        result.ShouldBe("Error: Board not found.");
    }
}
