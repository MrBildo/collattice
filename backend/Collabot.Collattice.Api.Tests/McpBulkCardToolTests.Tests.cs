using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The three bulk card tools (bulk_archive_cards,
// bulk_restore_cards, bulk_update_cards). All three are all-roles (gate via
// RequireUserAsync, like the per-card analogs they batch) and follow the
// two-phase contract:
//   - Phase 1 pre-validation fails loud with a single "Error: ..." string and
//     performs NO mutations (invalid GUID, cross-board restore, archive-lane
//     update target, cross-board label, ref-shape edge cases).
//   - Phase 2 per-card execution is best-effort with a per-item envelope, a
//     SINGLE SaveChanges, and ONE broadcast per affected board (deduplicated).
//
// The broadcaster is a concrete singleton with no interface seam; this suite
// probes broadcast counts by subscribing to the board's channel before the bulk
// call and draining it after — one drained message == one PublishBoardUpdated.
public class McpBulkCardToolTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly List<IServiceScope> _scopes = [];

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private (BoardDbContext Db, BulkCardTools Bulk, ArchiveTools Archive, BoardEventBroadcaster Broadcaster) CreateTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = new McpAuthService(new UserResolver(db));
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var webhookSink = scope.ServiceProvider.GetRequiredService<IWebhookSink>();
        return (db, new BulkCardTools(db, auth, broadcaster, webhookSink), new ArchiveTools(db, auth, broadcaster), broadcaster);
    }

    private static int _nextLanePosition = 70_000;
    private static int NextLanePosition() => Interlocked.Increment(ref _nextLanePosition);

    // Card numbers are unique per board. A monotonic counter guarantees distinct values; a random
    // pick collided on the (BoardId, Number) index at ~0.2% per 50-card seed (birthday paradox),
    // which read as an intermittent CI failure on unchanged code.
    private static int _nextCardNumber = 500_000;
    private static int NextCardNumber() => Interlocked.Increment(ref _nextCardNumber);

    // Each test gets its own board so broadcast counts and card-state assertions
    // are isolated from the shared default board and from parallel siblings.
    private async Task<TestBoard> CreateBoardAsync(BoardDbContext db)
    {
        var boardId = Guid.NewGuid();
        var board = new Board { Id = boardId, Name = $"Bulk Board {boardId:N}", Slug = $"bulk-board-{boardId:N}" };
        db.Boards.Add(board);

        var lane = new Lane { Id = Guid.NewGuid(), BoardId = boardId, Name = "Work", Position = NextLanePosition() };
        var laneB = new Lane { Id = Guid.NewGuid(), BoardId = boardId, Name = "Done", Position = NextLanePosition() };
        var archiveLane = new Lane { Id = Guid.NewGuid(), BoardId = boardId, Name = "Archive", Position = int.MaxValue, IsArchiveLane = true };
        db.Lanes.AddRange(lane, laneB, archiveLane);

        var size = new CardSize { Id = Guid.NewGuid(), BoardId = boardId, Name = "M", Ordinal = 0 };
        var sizeL = new CardSize { Id = Guid.NewGuid(), BoardId = boardId, Name = "L", Ordinal = 1 };
        db.CardSizes.AddRange(size, sizeL);

        await db.SaveChangesAsync();
        return new TestBoard(boardId, lane.Id, laneB.Id, archiveLane.Id, size.Id, sizeL.Id);
    }

    private async Task<CardItem> AddCardAsync(BoardDbContext db, TestBoard board, Guid? laneId = null)
    {
        var adminId = await db.Users.Where(u => u.Role == UserRole.Administrator).Select(u => u.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow;
        var card = new CardItem
        {
            Id = Guid.NewGuid(),
            BoardId = board.BoardId,
            LaneId = laneId ?? board.LaneId,
            SizeId = board.SizeId,
            Name = "Fixture Card",
            Number = NextCardNumber(),
            Position = 0,
            CreatedByUserId = adminId,
            CreatedAtUtc = now,
            LastUpdatedByUserId = adminId,
            LastUpdatedAtUtc = now,
        };
        db.Cards.Add(card);
        await db.SaveChangesAsync();
        return card;
    }

    private async Task ArchiveDirectAsync(BoardDbContext db, CardItem card, Guid archiveLaneId)
    {
        card.LaneId = archiveLaneId;
        await db.SaveChangesAsync();
    }

    private static string Csv(IEnumerable<Guid> ids) => string.Join(',', ids);

    // Drains a subscriber channel and returns the message count. One message ==
    // one PublishBoardUpdated against that board.
    private static int DrainCount(System.Threading.Channels.ChannelReader<string> reader)
    {
        var count = 0;
        while (reader.TryRead(out _))
        {
            count++;
        }

        return count;
    }

    private static JsonElement Parse(string result) => JsonSerializer.Deserialize<JsonElement>(result);

    // ── Phase 1 pre-validation — fail loud, no mutations ─────────────────────

    [Fact]
    public async Task BulkArchive_InvalidGuid_ReturnsErrorAndNoMutations()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var card = await AddCardAsync(db, board);

        var result = await bulk.BulkArchiveCardsAsync(_factory.AdminAuthKey, cardIds: $"{card.Id},not-a-guid");

        result.ShouldStartWith("Error:");
        result.ShouldContain("not-a-guid");
        db.ChangeTracker.Clear();
        (await db.Cards.FindAsync(card.Id))!.LaneId.ShouldBe(board.LaneId, "pre-validation failure must perform no mutations");
    }

    [Fact]
    public async Task BulkArchive_MissingCard_ReturnsErrorAndNoMutations()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var card = await AddCardAsync(db, board);
        var ghost = Guid.NewGuid();

        var result = await bulk.BulkArchiveCardsAsync(_factory.AdminAuthKey, cardIds: $"{card.Id},{ghost}");

        result.ShouldStartWith("Error:");
        result.ShouldContain(ghost.ToString());
        db.ChangeTracker.Clear();
        (await db.Cards.FindAsync(card.Id))!.LaneId.ShouldBe(board.LaneId);
    }

    [Fact]
    public async Task BulkRestore_CrossBoardTargetLane_ReturnsErrorAndNoMutations()
    {
        var (db, bulk, archive, _) = CreateTools();
        var boardA = await CreateBoardAsync(db);
        var boardB = await CreateBoardAsync(db);
        var card = await AddCardAsync(db, boardA);
        await ArchiveDirectAsync(db, card, boardA.ArchiveLaneId);
        db.ChangeTracker.Clear();

        // Restore to a lane on board B while the card is on board A.
        var result = await bulk.BulkRestoreCardsAsync(_factory.AdminAuthKey, boardB.LaneId, cardIds: card.Id.ToString());

        result.ShouldStartWith("Error:");
        result.ShouldContain("target lane's board");
        db.ChangeTracker.Clear();
        (await db.Cards.FindAsync(card.Id))!.LaneId.ShouldBe(boardA.ArchiveLaneId, "cross-board restore must not move the card");
    }

    [Fact]
    public async Task BulkRestore_TargetIsArchiveLane_ReturnsError()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var card = await AddCardAsync(db, board);
        await ArchiveDirectAsync(db, card, board.ArchiveLaneId);
        db.ChangeTracker.Clear();

        var result = await bulk.BulkRestoreCardsAsync(_factory.AdminAuthKey, board.ArchiveLaneId, cardIds: card.Id.ToString());

        result.ShouldStartWith("Error:");
        result.ShouldContain("archive lane");
    }

    [Fact]
    public async Task BulkUpdate_LaneIdIsArchiveLane_ReturnsErrorAndNoMutations()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var card = await AddCardAsync(db, board);

        var result = await bulk.BulkUpdateCardsAsync(_factory.AdminAuthKey, cardIds: card.Id.ToString(), laneId: board.ArchiveLaneId);

        result.ShouldStartWith("Error:");
        result.ShouldContain("archive lane");
        db.ChangeTracker.Clear();
        (await db.Cards.FindAsync(card.Id))!.LaneId.ShouldBe(board.LaneId);
    }

    [Fact]
    public async Task BulkUpdate_CrossBoardLabel_ReturnsErrorAndNoMutations()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var otherBoard = await CreateBoardAsync(db);
        var card = await AddCardAsync(db, board);

        // Label belongs to a different board than the card.
        var label = new Label { Id = Guid.NewGuid(), BoardId = otherBoard.BoardId, Name = "X", Color = "red" };
        db.Labels.Add(label);
        await db.SaveChangesAsync();

        var result = await bulk.BulkUpdateCardsAsync(_factory.AdminAuthKey, cardIds: card.Id.ToString(), labelIds: label.Id.ToString());

        result.ShouldStartWith("Error:");
        db.ChangeTracker.Clear();
        (await db.CardLabels.AnyAsync(cl => cl.CardId == card.Id)).ShouldBeFalse("cross-board label must not be assigned");
    }

    [Fact]
    public async Task BulkUpdate_CardsOnDifferentBoards_ReturnsError()
    {
        var (db, bulk, _, _) = CreateTools();
        var boardA = await CreateBoardAsync(db);
        var boardB = await CreateBoardAsync(db);
        var cardA = await AddCardAsync(db, boardA);
        var cardB = await AddCardAsync(db, boardB);

        var result = await bulk.BulkUpdateCardsAsync(_factory.AdminAuthKey, cardIds: Csv([cardA.Id, cardB.Id]), sizeName: "L");

        result.ShouldStartWith("Error:");
        result.ShouldContain("same board");
    }

    // ── Card-ref edge cases ──────────────────────────────────────────────────

    [Fact]
    public async Task BulkArchive_EmptyRefs_ReturnsError()
    {
        var (_, bulk, _, _) = CreateTools();
        var result = await bulk.BulkArchiveCardsAsync(_factory.AdminAuthKey);
        result.ShouldBe("Error: no card refs provided.");
    }

    [Fact]
    public async Task BulkArchive_MixedRefs_ReturnsError()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var card = await AddCardAsync(db, board);

        var result = await bulk.BulkArchiveCardsAsync(_factory.AdminAuthKey, cardIds: card.Id.ToString(), cardNumbers: card.Number.ToString());

        result.ShouldBe("Error: provide cardIds OR cardNumbers, not both.");
    }

    [Fact]
    public async Task BulkArchive_NumbersWithoutBoard_ReturnsError()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var card = await AddCardAsync(db, board);

        var result = await bulk.BulkArchiveCardsAsync(_factory.AdminAuthKey, cardNumbers: card.Number.ToString());

        result.ShouldStartWith("Error:");
        result.ShouldContain("boardId or boardSlug");
    }

    [Fact]
    public async Task BulkArchive_ByCardNumbers_WithBoardSlug_Works()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var slug = await db.Boards.Where(b => b.Id == board.BoardId).Select(b => b.Slug).FirstAsync();
        var cardA = await AddCardAsync(db, board);
        var cardB = await AddCardAsync(db, board);

        var result = await bulk.BulkArchiveCardsAsync
        (
            _factory.AdminAuthKey,
            cardNumbers: $"{cardA.Number.ToString(CultureInfo.InvariantCulture)},{cardB.Number.ToString(CultureInfo.InvariantCulture)}",
            boardSlug: slug
        );

        Parse(result).GetProperty("succeeded").GetInt32().ShouldBe(2);
        db.ChangeTracker.Clear();
        (await db.Cards.FindAsync(cardA.Id))!.LaneId.ShouldBe(board.ArchiveLaneId);
        (await db.Cards.FindAsync(cardB.Id))!.LaneId.ShouldBe(board.ArchiveLaneId);
    }

    // ── Auth gate (all-roles) ────────────────────────────────────────────────

    [Fact]
    public async Task BulkArchive_InvalidAuthKey_ReturnsError()
    {
        var (_, bulk, _, _) = CreateTools();
        var result = await bulk.BulkArchiveCardsAsync("bogus-key", cardIds: Guid.NewGuid().ToString());
        result.ShouldBe("Error: Invalid or inactive auth key.");
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task BulkArchive_NonAdminRoles_Succeed(UserRole role)
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var card = await AddCardAsync(db, board);

        using var setupClient = _factory.CreateClient();
        var user = await TestAuthHelper.CreateUserAsync(setupClient, _factory, $"bulk-{role}-{Guid.NewGuid():N}", role);

        var result = await bulk.BulkArchiveCardsAsync(user.AuthKey, cardIds: card.Id.ToString());

        Parse(result).GetProperty("succeeded").GetInt32().ShouldBe(1, "bulk tools are all-roles");
    }

    // ── Phase 2 per-card execution ───────────────────────────────────────────

    [Fact]
    public async Task BulkArchive_FiftyValidCards_AllOk()
    {
        var (db, bulk, _, broadcaster) = CreateTools();
        var board = await CreateBoardAsync(db);
        List<Guid> ids = [];
        for (var i = 0; i < 50; i++)
        {
            ids.Add((await AddCardAsync(db, board)).Id);
        }

        var reader = broadcaster.Subscribe(board.BoardId);
        var result = await bulk.BulkArchiveCardsAsync(_factory.AdminAuthKey, cardIds: Csv(ids));

        var json = Parse(result);
        json.GetProperty("totalRequested").GetInt32().ShouldBe(50);
        json.GetProperty("succeeded").GetInt32().ShouldBe(50);
        json.GetProperty("failed").GetInt32().ShouldBe(0);
        json.GetProperty("results").GetArrayLength().ShouldBe(50);
        foreach (var r in json.GetProperty("results").EnumerateArray())
        {
            r.GetProperty("status").GetString().ShouldBe("ok");
        }

        // Exactly ONE broadcast for the single affected board.
        DrainCount(reader).ShouldBe(1, "one deduplicated broadcast per affected board");
    }

    [Fact]
    public async Task BulkArchive_OneAlreadyArchived_RestSucceedWithPerCardError()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        List<Guid> ids = [];
        CardItem? alreadyArchived = null;
        for (var i = 0; i < 50; i++)
        {
            var c = await AddCardAsync(db, board);
            ids.Add(c.Id);
            if (i == 10)
            {
                alreadyArchived = c;
            }
        }

        await ArchiveDirectAsync(db, alreadyArchived!, board.ArchiveLaneId);
        db.ChangeTracker.Clear();

        var result = await bulk.BulkArchiveCardsAsync(_factory.AdminAuthKey, cardIds: Csv(ids));

        var json = Parse(result);
        json.GetProperty("succeeded").GetInt32().ShouldBe(49);
        json.GetProperty("failed").GetInt32().ShouldBe(1);

        var errored = json.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("status").GetString() == "error");
        errored.GetProperty("cardId").GetGuid().ShouldBe(alreadyArchived!.Id);
        errored.GetProperty("error").GetString().ShouldBe("Card is already archived.");
    }

    [Fact]
    public async Task BulkArchive_UnsortedInput_ResultsAlignWithInputOrder()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var c1 = await AddCardAsync(db, board);
        var c2 = await AddCardAsync(db, board);
        var c3 = await AddCardAsync(db, board);
        // Deliberate non-sorted order.
        List<Guid> order = [c3.Id, c1.Id, c2.Id];

        var result = await bulk.BulkArchiveCardsAsync(_factory.AdminAuthKey, cardIds: Csv(order));

        var results = Parse(result).GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("cardId").GetGuid())
                .ToList();
        results.ShouldBe(order, "results align 1:1 with input order");
    }

    [Fact]
    public async Task BulkRestore_TwoArchivedCards_MovesArchivedCardsToTargetLane()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var cardA = await AddCardAsync(db, board);
        var cardB = await AddCardAsync(db, board);
        await ArchiveDirectAsync(db, cardA, board.ArchiveLaneId);
        await ArchiveDirectAsync(db, cardB, board.ArchiveLaneId);
        db.ChangeTracker.Clear();

        var result = await bulk.BulkRestoreCardsAsync(_factory.AdminAuthKey, board.LaneB, cardIds: Csv([cardA.Id, cardB.Id]));

        Parse(result).GetProperty("succeeded").GetInt32().ShouldBe(2);
        db.ChangeTracker.Clear();
        (await db.Cards.FindAsync(cardA.Id))!.LaneId.ShouldBe(board.LaneB);
        (await db.Cards.FindAsync(cardB.Id))!.LaneId.ShouldBe(board.LaneB);
    }

    [Fact]
    public async Task BulkRestore_NotArchivedCard_PerCardError()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var archived = await AddCardAsync(db, board);
        var notArchived = await AddCardAsync(db, board);
        await ArchiveDirectAsync(db, archived, board.ArchiveLaneId);
        db.ChangeTracker.Clear();

        var result = await bulk.BulkRestoreCardsAsync(_factory.AdminAuthKey, board.LaneB, cardIds: Csv([archived.Id, notArchived.Id]));

        var json = Parse(result);
        json.GetProperty("succeeded").GetInt32().ShouldBe(1);
        json.GetProperty("failed").GetInt32().ShouldBe(1);
        var errored = json.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("status").GetString() == "error");
        errored.GetProperty("cardId").GetGuid().ShouldBe(notArchived.Id);
        errored.GetProperty("error").GetString().ShouldBe("Card is not archived.");
    }

    [Fact]
    public async Task BulkUpdate_UniformLaneMove_Works()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var cardA = await AddCardAsync(db, board);
        var cardB = await AddCardAsync(db, board);

        var result = await bulk.BulkUpdateCardsAsync(_factory.AdminAuthKey, cardIds: Csv([cardA.Id, cardB.Id]), laneId: board.LaneB);

        Parse(result).GetProperty("succeeded").GetInt32().ShouldBe(2);
        db.ChangeTracker.Clear();
        (await db.Cards.FindAsync(cardA.Id))!.LaneId.ShouldBe(board.LaneB);
        (await db.Cards.FindAsync(cardB.Id))!.LaneId.ShouldBe(board.LaneB);
    }

    [Fact]
    public async Task BulkUpdate_UniformSizeChange_Works()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var cardA = await AddCardAsync(db, board);
        var cardB = await AddCardAsync(db, board);

        var result = await bulk.BulkUpdateCardsAsync(_factory.AdminAuthKey, cardIds: Csv([cardA.Id, cardB.Id]), sizeName: "L");

        Parse(result).GetProperty("succeeded").GetInt32().ShouldBe(2);
        db.ChangeTracker.Clear();
        (await db.Cards.FindAsync(cardA.Id))!.SizeId.ShouldBe(board.SizeL);
        (await db.Cards.FindAsync(cardB.Id))!.SizeId.ShouldBe(board.SizeL);
    }

    [Fact]
    public async Task BulkUpdate_NoFieldsProvided_ReturnsError()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var card = await AddCardAsync(db, board);

        var result = await bulk.BulkUpdateCardsAsync(_factory.AdminAuthKey, cardIds: card.Id.ToString());

        result.ShouldStartWith("Error: No changes specified.");
    }

    [Fact]
    public async Task BulkUpdate_ArchivedCard_PerCardError()
    {
        var (db, bulk, _, _) = CreateTools();
        var board = await CreateBoardAsync(db);
        var live = await AddCardAsync(db, board);
        var archived = await AddCardAsync(db, board);
        await ArchiveDirectAsync(db, archived, board.ArchiveLaneId);
        db.ChangeTracker.Clear();

        var result = await bulk.BulkUpdateCardsAsync(_factory.AdminAuthKey, cardIds: Csv([live.Id, archived.Id]), sizeName: "L");

        var json = Parse(result);
        json.GetProperty("succeeded").GetInt32().ShouldBe(1);
        json.GetProperty("failed").GetInt32().ShouldBe(1);
        var errored = json.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("status").GetString() == "error");
        errored.GetProperty("cardId").GetGuid().ShouldBe(archived.Id);
        errored.GetProperty("error").GetString()!.ShouldContain("Archived");
    }

    [Fact]
    public async Task BulkUpdate_LabelSet_IdempotentReRun_NoChurn()
    {
        var (db, bulk, _, broadcaster) = CreateTools();
        var board = await CreateBoardAsync(db);
        var label = new Label { Id = Guid.NewGuid(), BoardId = board.BoardId, Name = "Tag", Color = "blue" };
        db.Labels.Add(label);
        await db.SaveChangesAsync();

        List<Guid> ids = [];
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await AddCardAsync(db, board)).Id);
        }

        // First application — assigns the label to all 5.
        var first = await bulk.BulkUpdateCardsAsync(_factory.AdminAuthKey, cardIds: Csv(ids), labelIds: label.Id.ToString());
        Parse(first).GetProperty("succeeded").GetInt32().ShouldBe(5);
        db.ChangeTracker.Clear();
        (await db.CardLabels.CountAsync(cl => cl.LabelId == label.Id)).ShouldBe(5);

        // Idempotent re-run — same label set; still 5 ok, no row churn.
        var reader = broadcaster.Subscribe(board.BoardId);
        var second = await bulk.BulkUpdateCardsAsync(_factory.AdminAuthKey, cardIds: Csv(ids), labelIds: label.Id.ToString());
        Parse(second).GetProperty("succeeded").GetInt32().ShouldBe(5);
        db.ChangeTracker.Clear();
        (await db.CardLabels.CountAsync(cl => cl.LabelId == label.Id)).ShouldBe(5, "idempotent re-run must not churn label rows");

        DrainCount(reader).ShouldBe(1, "one broadcast even on a no-net-change re-run");
    }

    // ── Single broadcast per board across multiple boards ────────────────────

    [Fact]
    public async Task BulkArchive_AcrossTwoBoards_BroadcastsOncePerBoard()
    {
        var (db, bulk, _, broadcaster) = CreateTools();
        var boardA = await CreateBoardAsync(db);
        var boardB = await CreateBoardAsync(db);
        var a1 = await AddCardAsync(db, boardA);
        var a2 = await AddCardAsync(db, boardA);
        var b1 = await AddCardAsync(db, boardB);

        var readerA = broadcaster.Subscribe(boardA.BoardId);
        var readerB = broadcaster.Subscribe(boardB.BoardId);

        var result = await bulk.BulkArchiveCardsAsync(_factory.AdminAuthKey, cardIds: Csv([a1.Id, a2.Id, b1.Id]));

        Parse(result).GetProperty("succeeded").GetInt32().ShouldBe(3);
        DrainCount(readerA).ShouldBe(1, "board A archived twice → one broadcast");
        DrainCount(readerB).ShouldBe(1, "board B archived once → one broadcast");
    }

    private sealed record TestBoard(Guid BoardId, Guid LaneId, Guid LaneB, Guid ArchiveLaneId, Guid SizeId, Guid SizeL);
}
