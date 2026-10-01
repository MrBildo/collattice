using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collabot.Collattice.Api.Auth;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Mcp;
using Collabot.Collattice.Api.Models;
using Collabot.Collattice.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The collision tests next door drive the history helper directly, which proves the retry resolves
// a collision but not that either entry point still goes through it. Removing the retry from both
// write paths leaves those tests — and the whole suite — green, so the fix for a release-critical
// concurrency defect can be deleted without anything noticing.
//
// These two close that seam by losing the race through the entry points themselves: an interceptor
// commits a rival edit in the gap between staging and save, so a write path that commits through
// the retry answers normally and one that commits through a plain save fails the request.
public class CardHistoryWiringTests(RevisionRaceFactory factory) : IClassFixture<RevisionRaceFactory>
{
    private const string _contendedReason = "were being saved at the same time; try again";

    private readonly RevisionRaceFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task RestDescriptionPatch_LosingTheRevisionRace_StillAnswersAndRecordsBothEdits()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Rest Wiring Race", "start");
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Rest Wiring Rival", UserRole.HumanUser);

        _factory.Interceptor.Arm(cardId, rival.Id);

        try
        {
            // Act
            var response = await _client.PatchAsJsonAsync
            (
                $"/api/v1/cards/{cardId}",
                new { descriptionMarkdown = "my wording" }
            );

            // Assert — the request that lost the race is the one under test, so it has to have met
            // a real collision and still answered.
            _factory.Interceptor.HasFired.ShouldBeTrue();
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            _factory.Interceptor.Disarm();
        }

        await AssertBothEditsRecordedAsync(cardId, "rival edit 1", "my wording");
    }

    [Fact]
    public async Task McpUpdateCard_LosingTheRevisionRace_StillAnswersAndRecordsBothEdits()
    {
        // Arrange — the other write path, which shares the helper but reaches it on its own.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync("Mcp Wiring Race", "start");
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, "Mcp Wiring Rival", UserRole.HumanUser);

        _factory.Interceptor.Arm(cardId, rival.Id);

        try
        {
            // Act
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
            var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
            var tools = new CardTools(db, new McpAuthService(new UserResolver(db)), broadcaster);

            var json = await tools.UpdateCardAsync
            (
                CollatticeApiFactory.TestAdminAuthKey,
                cardId: cardId,
                descriptionMarkdown: "my wording"
            );

            // Assert
            _factory.Interceptor.HasFired.ShouldBeTrue();
            json.ShouldNotStartWith("Error");
        }
        finally
        {
            _factory.Interceptor.Disarm();
        }

        await AssertBothEditsRecordedAsync(cardId, "rival edit 1", "my wording");
    }

    [Theory]
    [InlineData(EntryPoint.RestPatch)]
    [InlineData(EntryPoint.McpUpdateCard)]
    public async Task DescriptionEdit_LosingEveryRetryButTheLast_StillAnswers(EntryPoint entryPoint)
    {
        // Arm one collision short of the retry budget, so the write path loses on every attempt but
        // its last and still commits. The single-collision tests above never reach a second
        // iteration of the retry loop; this is where a loop that stopped rebuilding after one try, a
        // cut budget, or a retry deleted from the entry point would surface. The budget is the
        // test-side one, never the helper's own constant, so that a cut cannot move the test with it.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync($"Last Attempt Race {entryPoint}", "start");
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, $"Last Attempt Rival {entryPoint}", UserRole.HumanUser);

        _factory.Interceptor.Arm(cardId, rival.Id, AllocatorRetryBudget.Attempts - 1);

        try
        {
            // Act
            var outcome = await EditDescriptionAsync(entryPoint, cardId, "my wording");

            // Assert — every collision but the last was met, and the request still answered.
            _factory.Interceptor.FiredCount.ShouldBe(AllocatorRetryBudget.Attempts - 1);
            outcome.Succeeded.ShouldBeTrue();
        }
        finally
        {
            _factory.Interceptor.Disarm();
        }

        // The own edit lands on top of the seed plus one revision per injected rival, and the seed is
        // written exactly once however many times the rows were rebuilt.
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var rows = await db.CardFieldHistories
            .Where(h => h.CardId == cardId)
            .OrderBy(h => h.Revision)
                .ToListAsync();

        rows.Count.ShouldBe(AllocatorRetryBudget.Attempts + 1);
        rows.Count(r => r.Value == "start").ShouldBe(1);
        rows[0].EditedByUserId.ShouldBeNull();
        rows[^1].Value.ShouldBe("my wording");

        var card = await db.Cards.AsNoTracking().SingleAsync(c => c.Id == cardId);
        card.DescriptionMarkdown.ShouldBe("my wording");
    }

    [Theory]
    [InlineData(EntryPoint.RestPatch)]
    [InlineData(EntryPoint.McpUpdateCard)]
    public async Task DescriptionEdit_ExhaustingEveryRetryAttempt_AsksTheCallerToTryAgain(EntryPoint entryPoint)
    {
        // Arm a collision for every attempt, so even the last is lost and the budget runs out. The
        // request answers "try again" rather than hanging, failing with a 500, or silently dropping
        // the edit, and the loop terminates. The answer on its own would not distinguish exhaustion
        // from a retry-less first-collision answer; the fired-count check is what proves the loop ran
        // the full budget before giving up, and it is why this reds too if the retry leaves the entry
        // point.
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var cardId = await CreateCardAsync($"Exhaustion Race {entryPoint}", "start");
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, $"Exhaustion Rival {entryPoint}", UserRole.HumanUser);

        _factory.Interceptor.Arm(cardId, rival.Id, AllocatorRetryBudget.Attempts);

        try
        {
            // Act
            var outcome = await EditDescriptionAsync(entryPoint, cardId, "my wording");

            // Assert — every attempt met a collision, and the exhausted write asked to be retried.
            _factory.Interceptor.FiredCount.ShouldBe(AllocatorRetryBudget.Attempts);
            outcome.Succeeded.ShouldBeFalse();
            outcome.ShouldHaveAskedToTryAgain(_contendedReason);
        }
        finally
        {
            _factory.Interceptor.Disarm();
        }

        // Nothing of the exhausted edit was saved: the card keeps the last rival's text, and the
        // trail holds the seed plus one revision per injected rival.
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var card = await db.Cards.AsNoTracking().SingleAsync(c => c.Id == cardId);
        var revisions = await db.CardFieldHistories.CountAsync(h => h.CardId == cardId);

        card.DescriptionMarkdown.ShouldBe($"rival edit {AllocatorRetryBudget.Attempts}");
        revisions.ShouldBe(AllocatorRetryBudget.Attempts + 1);
    }

    // A description edit that also moves the card to another lane. Every attempt re-saves the whole
    // change, and the renumbering of both lanes is part of it, so the move lands with the edit on
    // the attempt that wins and is never saved without it.
    [Theory]
    [InlineData(EntryPoint.RestPatch)]
    [InlineData(EntryPoint.McpUpdateCard)]
    public async Task DescriptionEditWithLaneMove_LosingEveryRetryButTheLast_MovesTheCardAndRenumbersBothLanes(EntryPoint entryPoint)
    {
        // Arrange
        var board = await SeedTwoLaneBoardAsync(entryPoint);
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, $"Lane Move Rival {entryPoint}", UserRole.HumanUser);

        _factory.Interceptor.Arm(board.MovingCardId, rival.Id, AllocatorRetryBudget.Attempts - 1);

        try
        {
            // Act
            var outcome = await EditDescriptionAndMoveAsync(entryPoint, board, "my wording");

            // Assert
            _factory.Interceptor.FiredCount.ShouldBe(AllocatorRetryBudget.Attempts - 1);
            outcome.Succeeded.ShouldBeTrue();
        }
        finally
        {
            _factory.Interceptor.Disarm();
        }

        (await LanePositionsAsync(board.SourceLaneId)).ShouldBe(["A=0", "C=10"]);
        (await LanePositionsAsync(board.TargetLaneId)).ShouldBe(["X=0", "B=10"]);
    }

    [Theory]
    [InlineData(EntryPoint.RestPatch)]
    [InlineData(EntryPoint.McpUpdateCard)]
    public async Task DescriptionEditWithLaneMove_ExhaustingEveryRetryAttempt_MovesNothing(EntryPoint entryPoint)
    {
        // Arrange
        var board = await SeedTwoLaneBoardAsync(entryPoint);
        var rival = await TestAuthHelper.CreateUserAsync(_client, _factory, $"Lane Move Exhaustion Rival {entryPoint}", UserRole.HumanUser);

        _factory.Interceptor.Arm(board.MovingCardId, rival.Id, AllocatorRetryBudget.Attempts);

        try
        {
            // Act
            var outcome = await EditDescriptionAndMoveAsync(entryPoint, board, "my wording");

            // Assert
            _factory.Interceptor.FiredCount.ShouldBe(AllocatorRetryBudget.Attempts);
            outcome.Succeeded.ShouldBeFalse();
            outcome.ShouldHaveAskedToTryAgain(_contendedReason);
        }
        finally
        {
            _factory.Interceptor.Disarm();
        }

        (await LanePositionsAsync(board.SourceLaneId)).ShouldBe(["A=0", "B=10", "C=20"]);
        (await LanePositionsAsync(board.TargetLaneId)).ShouldBe(["X=0"]);
    }

    public enum EntryPoint
    {
        RestPatch,
        McpUpdateCard
    }

    private async Task<WriteOutcome> EditDescriptionAsync(EntryPoint entryPoint, Guid cardId, string descriptionMarkdown)
    {
        if (entryPoint == EntryPoint.RestPatch)
        {
            return await WriteOutcome.FromResponseAsync(await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { descriptionMarkdown }));
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var tools = new CardTools(db, new McpAuthService(new UserResolver(db)), broadcaster);

        return await WriteOutcome.FromToolAsync(() =>
            tools.UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: cardId, descriptionMarkdown: descriptionMarkdown));
    }

    private async Task AssertBothEditsRecordedAsync(Guid cardId, string rivalValue, string ownValue)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var rows = await db.CardFieldHistories
            .Where(h => h.CardId == cardId)
            .OrderBy(h => h.Revision)
                .ToListAsync();

        // Both edits present, in commit order, on top of a seed written exactly once.
        rows.Select(r => r.Revision).ShouldBe([1, 2, 3]);
        rows.Select(r => r.Value).ShouldBe(["start", rivalValue, ownValue]);
        rows[0].EditedByUserId.ShouldBeNull();

        var card = await db.Cards.AsNoTracking().SingleAsync(c => c.Id == cardId);
        card.DescriptionMarkdown.ShouldBe(ownValue);
    }

    private async Task<WriteOutcome> EditDescriptionAndMoveAsync(EntryPoint entryPoint, LaneMoveBoard board, string descriptionMarkdown)
    {
        if (entryPoint == EntryPoint.RestPatch)
        {
            return await WriteOutcome.FromResponseAsync(await _client.PatchAsJsonAsync($"/api/v1/cards/{board.MovingCardId}", new { descriptionMarkdown, laneId = board.TargetLaneId }));
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var tools = new CardTools(db, new McpAuthService(new UserResolver(db)), broadcaster);

        return await WriteOutcome.FromToolAsync(() =>
            tools.UpdateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: board.MovingCardId, descriptionMarkdown: descriptionMarkdown, laneId: board.TargetLaneId, index: 1));
    }

    // A fresh board: the source lane holds A, B, C at 0, 10, 20, with B the card that moves; the
    // target lane holds X at 0.
    private async Task<LaneMoveBoard> SeedTwoLaneBoardAsync(EntryPoint entryPoint)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var boardId = await PostForIdAsync("/api/v1/boards", new { name = $"Lane Move Race {entryPoint} {Guid.NewGuid():N}" });
        var sourceLaneId = await PostForIdAsync($"/api/v1/boards/{boardId}/lanes", new { name = "Source" });
        var targetLaneId = await PostForIdAsync($"/api/v1/boards/{boardId}/lanes", new { name = "Target" });

        await PostForIdAsync($"/api/v1/boards/{boardId}/cards", new { name = "A", laneId = sourceLaneId });
        var movingCardId = await PostForIdAsync($"/api/v1/boards/{boardId}/cards", new { name = "B", laneId = sourceLaneId, descriptionMarkdown = "start" });
        await PostForIdAsync($"/api/v1/boards/{boardId}/cards", new { name = "C", laneId = sourceLaneId });
        await PostForIdAsync($"/api/v1/boards/{boardId}/cards", new { name = "X", laneId = targetLaneId });

        return new LaneMoveBoard(sourceLaneId, targetLaneId, movingCardId);
    }

    private async Task<Guid> PostForIdAsync(string path, object body)
    {
        var response = await _client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);

        return json.GetProperty("id").GetGuid();
    }

    // The lane's saved cards in position order, as name=position.
    private async Task<List<string>> LanePositionsAsync(Guid laneId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var cards = await db.Cards
            .Where(c => c.LaneId == laneId)
            .OrderBy(c => c.Position)
                .Select(c => new { c.Name, c.Position })
                    .ToListAsync();

        return [.. cards.Select(c => $"{c.Name}={c.Position.ToString(CultureInfo.InvariantCulture)}")];
    }

    private sealed record LaneMoveBoard(Guid SourceLaneId, Guid TargetLaneId, Guid MovingCardId);

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
}
