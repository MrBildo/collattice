using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Two editors saving one card's description at the same moment both allocate the same revision
// ordinal, and the unique index on it means the second one to reach the database is rejected.
//
// The race is reproduced deterministically rather than by running requests in parallel: the losing
// editor stages its rows, a rival commits in between on its own DbContext, and only then does the
// loser save. That is exactly the interleaving the live race produces, without two threads sharing
// the harness's single in-memory connection — which would fail on connection contention rather
// than on the collision under test, and only sometimes.
public class CardHistoryConcurrencyTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task TwoEditsRacingTheFirstRevision_BothSucceed_AndTheTrailSeedsExactlyOnce()
    {
        // Arrange — a card with no trail yet, so both editors stage the seed row as well as their
        // own. This is the harder half of the race: the seed is the row a naive retry duplicates.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Description Race", "original");
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Race Rival", UserRole.HumanUser);
        var loser = await TestAuthHelper.CreateUserAsync(_client, _factory, "Race Loser", UserRole.HumanUser);

        await using var loserScope = _factory.Services.CreateAsyncScope();
        var loserDb = loserScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var loserCard = await loserDb.Cards.FindAsync(cardId);
        loserCard!.DescriptionMarkdown = "loser text";

        var loserChange = await CardHistoryHelper.StageDescriptionChangeAsync
        (
            loserDb,
            cardId,
            "original",
            "loser text",
            loser.Id
        );

        // The rival reads the same empty trail and commits first, taking revisions 1 and 2.
        await using var rivalScope = _factory.Services.CreateAsyncScope();
        var rivalDb = rivalScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var rivalCard = await rivalDb.Cards.FindAsync(cardId);
        rivalCard!.DescriptionMarkdown = "rival text";

        var rivalChange = await CardHistoryHelper.StageDescriptionChangeAsync
        (
            rivalDb,
            cardId,
            "original",
            "rival text",
            rival.Id
        );

        (await CardHistoryHelper.TrySaveWithRevisionRetryAsync(rivalDb, rivalChange)).ShouldBeTrue();

        // Act — the loser's staged revisions 1 and 2 are both already taken.
        (await CardHistoryHelper.TrySaveWithRevisionRetryAsync(loserDb, loserChange)).ShouldBeTrue();

        // Assert — the losing edit landed instead of erroring, and landed as a real revision.
        await using var readScope = _factory.Services.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var rows = await readDb.CardFieldHistories
            .Where(h => h.CardId == cardId)
            .OrderBy(h => h.Revision)
                .ToListAsync();

        rows.Select(r => r.Revision).ShouldBe([1, 2, 3]);
        rows[0].Value.ShouldBe("original");
        rows[1].Value.ShouldBe("rival text");
        rows[2].Value.ShouldBe("loser text");

        // The load-bearing one. Rebuilding the staged rows re-runs the "is this trail empty?"
        // decision, so the seed is written once. A retry that merely renumbered what it had
        // staged would insert a second copy of "original" at revision 3, and the trail would
        // read as though the description had reverted before being changed again.
        rows.Count(r => r.Value == "original").ShouldBe(1);

        // Provenance survives the retry: the seed stays un-attributed, each edit keeps its editor.
        rows[0].EditedByUserId.ShouldBeNull();
        rows[1].EditedByUserId.ShouldBe(rival.Id);
        rows[2].EditedByUserId.ShouldBe(loser.Id);

        var card = await readDb.Cards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        card.DescriptionMarkdown.ShouldBe("loser text");
    }

    [Fact]
    public async Task DescriptionEdit_RacingALaterRevision_LandsOnTopWithoutReseeding()
    {
        // Arrange — a card whose trail already exists, so neither editor stages a seed row and the
        // collision is purely over the next ordinal.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Description Race Later", "first");
        await PatchDescriptionAsync(cardId, "second");

        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Later Rival", UserRole.HumanUser);
        var loser = await TestAuthHelper.CreateUserAsync(_client, _factory, "Later Loser", UserRole.HumanUser);

        await using var loserScope = _factory.Services.CreateAsyncScope();
        var loserDb = loserScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var loserCard = await loserDb.Cards.FindAsync(cardId);
        loserCard!.DescriptionMarkdown = "loser third";

        var loserChange = await CardHistoryHelper.StageDescriptionChangeAsync
        (
            loserDb,
            cardId,
            "second",
            "loser third",
            loser.Id
        );

        await using var rivalScope = _factory.Services.CreateAsyncScope();
        var rivalDb = rivalScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var rivalChange = await CardHistoryHelper.StageDescriptionChangeAsync
        (
            rivalDb,
            cardId,
            "second",
            "rival third",
            rival.Id
        );

        (await CardHistoryHelper.TrySaveWithRevisionRetryAsync(rivalDb, rivalChange)).ShouldBeTrue();

        // Act
        (await CardHistoryHelper.TrySaveWithRevisionRetryAsync(loserDb, loserChange)).ShouldBeTrue();

        // Assert — four revisions, dense and in commit order, nothing lost and nothing duplicated.
        await using var readScope = _factory.Services.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var rows = await readDb.CardFieldHistories
            .Where(h => h.CardId == cardId)
            .OrderBy(h => h.Revision)
                .ToListAsync();

        rows.Select(r => r.Revision).ShouldBe([1, 2, 3, 4]);
        rows.Select(r => r.Value).ShouldBe(["first", "second", "rival third", "loser third"]);
    }

    [Fact]
    public async Task DescriptionEdit_RetriedAfterARace_IsVisibleThroughTheReadSurface()
    {
        // The rows being right is necessary but not the promise; the promise is that a reader sees
        // both edits attributed, with a diff from one to the other.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Race Readback", "alpha");
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Readback Rival", UserRole.HumanUser);
        var loser = await TestAuthHelper.CreateUserAsync(_client, _factory, "Readback Loser", UserRole.HumanUser);

        await using var loserScope = _factory.Services.CreateAsyncScope();
        var loserDb = loserScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var loserCard = await loserDb.Cards.FindAsync(cardId);
        loserCard!.DescriptionMarkdown = "gamma";

        var loserChange = await CardHistoryHelper.StageDescriptionChangeAsync
        (
            loserDb,
            cardId,
            "alpha",
            "gamma",
            loser.Id
        );

        await using var rivalScope = _factory.Services.CreateAsyncScope();
        var rivalDb = rivalScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var rivalChange = await CardHistoryHelper.StageDescriptionChangeAsync
        (
            rivalDb,
            cardId,
            "alpha",
            "beta",
            rival.Id
        );

        (await CardHistoryHelper.TrySaveWithRevisionRetryAsync(rivalDb, rivalChange)).ShouldBeTrue();
        (await CardHistoryHelper.TrySaveWithRevisionRetryAsync(loserDb, loserChange)).ShouldBeTrue();

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/history");
        response.EnsureSuccessStatusCode();
        var trail = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);

        // Assert — three revisions, and the newest diffs against the one that beat it.
        trail.GetProperty("totalCount").GetInt32().ShouldBe(3);
        var entries = trail.GetProperty("entries").EnumerateArray().ToArray();
        entries[0].GetProperty("value").GetString().ShouldBe("gamma");
        entries[0].GetProperty("diff").GetString().ShouldBe("@@ -1,1 +1,1 @@\n-beta\n+gamma\n");
        entries[1].GetProperty("value").GetString().ShouldBe("beta");
    }

    [Fact]
    public async Task TwoEditsRacingWithTheSameText_RecordOneRevision_NotADuplicateWithAnEmptyDiff()
    {
        // The unchanged-description check runs once, against the value read at the start of the
        // request. A retry happens after a rival has committed, so by then that reading is stale —
        // and when both editors are setting the same text, the rival's commit has already made this
        // request a no-op. Recording it anyway appends a revision identical to its predecessor,
        // whose diff is empty; an empty diff on this trail means "oldest revision, nothing before
        // it", which is the one distinction the trail exists to make.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Same Text Race", "original");
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Same Text Rival", UserRole.HumanUser);
        var loser = await TestAuthHelper.CreateUserAsync(_client, _factory, "Same Text Loser", UserRole.HumanUser);

        await using var loserScope = _factory.Services.CreateAsyncScope();
        var loserDb = loserScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var loserCard = await loserDb.Cards.FindAsync(cardId);
        loserCard!.DescriptionMarkdown = "agreed text";

        var loserChange = await CardHistoryHelper.StageDescriptionChangeAsync
        (
            loserDb,
            cardId,
            "original",
            "agreed text",
            loser.Id
        );

        await using var rivalScope = _factory.Services.CreateAsyncScope();
        var rivalDb = rivalScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var rivalCard = await rivalDb.Cards.FindAsync(cardId);
        rivalCard!.DescriptionMarkdown = "agreed text";

        var rivalChange = await CardHistoryHelper.StageDescriptionChangeAsync
        (
            rivalDb,
            cardId,
            "original",
            "agreed text",
            rival.Id
        );

        (await CardHistoryHelper.TrySaveWithRevisionRetryAsync(rivalDb, rivalChange)).ShouldBeTrue();

        // Act
        (await CardHistoryHelper.TrySaveWithRevisionRetryAsync(loserDb, loserChange)).ShouldBeTrue();

        // Assert — exactly the trail these two edits leave when they arrive one after the other
        // instead of at once. The second one changes nothing and records nothing, either way.
        await using var readScope = _factory.Services.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var rows = await readDb.CardFieldHistories
            .Where(h => h.CardId == cardId)
            .OrderBy(h => h.Revision)
                .ToListAsync();

        rows.Select(r => r.Revision).ShouldBe([1, 2]);
        rows.Select(r => r.Value).ShouldBe(["original", "agreed text"]);
        rows[1].EditedByUserId.ShouldBe(rival.Id);

        // The losing save still committed — it just had nothing left to record.
        var card = await readDb.Cards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        card.DescriptionMarkdown.ShouldBe("agreed text");

        // Read back through the surface that publishes the empty-diff meaning, because that is
        // where the damage would show: no revision but the oldest may carry an empty diff.
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/history");
        response.EnsureSuccessStatusCode();
        var trail = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var entries = trail.GetProperty("entries").EnumerateArray().ToArray();

        trail.GetProperty("totalCount").GetInt32().ShouldBe(2);
        entries
            .Where(e => e.GetProperty("revision").GetInt32() > 1)
            .ShouldAllBe(e => e.GetProperty("diff").GetString() != string.Empty);
    }

    [Fact]
    public async Task DescriptionEdit_RetriedRevision_IsStampedWhenItLandsNotWhenTheRequestArrived()
    {
        // A revision that waited out a rival is written after that rival's, so it has to carry a
        // later instant too. Keeping the arrival time would file a higher revision under an earlier
        // timestamp, and a reader who sorts the trail by time would get a different order than the
        // revision numbers give — on a trail whose values only mean anything in revision order.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Stamp Order Race", "start");
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Stamp Rival", UserRole.HumanUser);
        var loser = await TestAuthHelper.CreateUserAsync(_client, _factory, "Stamp Loser", UserRole.HumanUser);

        var loserArrivedAt = DateTimeOffset.UtcNow;

        await using var loserScope = _factory.Services.CreateAsyncScope();
        var loserDb = loserScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var loserCard = await loserDb.Cards.FindAsync(cardId);
        loserCard!.DescriptionMarkdown = "loser wording";

        var loserChange = await CardHistoryHelper.StageDescriptionChangeAsync
        (
            loserDb,
            cardId,
            "start",
            "loser wording",
            loser.Id
        );

        await using var rivalScope = _factory.Services.CreateAsyncScope();
        var rivalDb = rivalScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var rivalCard = await rivalDb.Cards.FindAsync(cardId);
        rivalCard!.DescriptionMarkdown = "rival wording";

        var rivalChange = await CardHistoryHelper.StageDescriptionChangeAsync
        (
            rivalDb,
            cardId,
            "start",
            "rival wording",
            rival.Id
        );

        (await CardHistoryHelper.TrySaveWithRevisionRetryAsync(rivalDb, rivalChange)).ShouldBeTrue();

        // Act
        (await CardHistoryHelper.TrySaveWithRevisionRetryAsync(loserDb, loserChange)).ShouldBeTrue();

        // Assert
        await using var readScope = _factory.Services.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var rows = await readDb.CardFieldHistories
            .Where(h => h.CardId == cardId)
            .OrderBy(h => h.Revision)
                .ToListAsync();

        rows.Select(r => r.Value).ShouldBe(["start", "rival wording", "loser wording"]);

        // The load-bearing pair: timestamps rise with revision, and the retried one moved off the
        // instant its request arrived — which is the reading it would have kept if the stamp came
        // from the caller rather than from where the revision number does.
        rows[2].EditedAtUtc!.Value.ShouldBeGreaterThanOrEqualTo(rows[1].EditedAtUtc!.Value);
        rows[2].EditedAtUtc!.Value.ShouldBeGreaterThan(loserArrivedAt);
    }

    [Fact]
    public async Task DescriptionEdit_UniqueFailureThatIsNotARevisionCollision_ReachesTheCallerUnretried()
    {
        // The retry is for one collision only. Another unique-constraint violation riding in the
        // same save has to arrive at the caller as itself — retrying it cannot help, and rebuilding
        // the history rows in response describes it as a revision problem it never was.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Unrelated Constraint", "before");
        var editor = await TestAuthHelper.CreateUserAsync(_client, _factory, "Constraint Editor", UserRole.HumanUser);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var card = await db.Cards.FindAsync(cardId);
        card!.DescriptionMarkdown = "after";

        var change = await CardHistoryHelper.StageDescriptionChangeAsync
        (
            db,
            cardId,
            "before",
            "after",
            editor.Id
        );

        // Two labels claiming one name on one board — the board's own unique index, nothing to do
        // with revisions.
        db.Labels.Add(new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = "Contested Name" });
        db.Labels.Add(new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = "Contested Name" });

        var stagedIdsBefore = StagedHistoryIds(db);

        // Act
        var act = () => CardHistoryHelper.TrySaveWithRevisionRetryAsync(db, change);
        await Should.ThrowAsync<DbUpdateException>(act);

        // Assert — the discriminator. A retry detaches the staged rows and builds replacements, so
        // surviving with the same identities is what proves no retry ran. The throw alone proves
        // less: it says the failure was not treated as a revision collision on the last attempt, not
        // that no attempt before it was.
        StagedHistoryIds(db).ShouldBe(stagedIdsBefore);
    }

    private static List<Guid> StagedHistoryIds(BoardDbContext db) =>
        [.. db.ChangeTracker
            .Entries<CardFieldHistory>()
            .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity.Id)
                .Order()];

    private async Task<Guid> CreateCardAsync(string name, string descriptionMarkdown)
    {
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name, laneId, descriptionMarkdown }
        );
        response.EnsureSuccessStatusCode();
        var card = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return card.GetProperty("id").GetGuid();
    }

    private async Task PatchDescriptionAsync(Guid cardId, string descriptionMarkdown)
    {
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { descriptionMarkdown });
        response.EnsureSuccessStatusCode();
    }
}
