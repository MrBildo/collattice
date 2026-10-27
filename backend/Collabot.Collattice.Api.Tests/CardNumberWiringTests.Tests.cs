using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Drives the card-number allocator through every entry point that claims a number, losing the race
// for that number on every attempt but the last, and then on every attempt.
//
// A single forced collision proves an entry point still reaches the retry, but it cannot tell an
// eight-attempt budget from a three-attempt one: both survive one collision. Losing seven in a row and
// still answering, and losing all eight and failing, is what pins the budget, and it pins it per entry
// point, so a retry removed from any one of them reds that entry point's test.
public class CardNumberWiringTests(CardNumberRaceFactory factory) : IClassFixture<CardNumberRaceFactory>
{
    private const string _contendedReason = "being created on this board at the same time; try again";

    private readonly CardNumberRaceFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    public enum EntryPoint
    {
        RestCreate,
        RestFinalize,
        McpCreateCard,
        McpDuplicateCard
    }

    [Theory]
    [InlineData(EntryPoint.RestCreate)]
    [InlineData(EntryPoint.RestFinalize)]
    [InlineData(EntryPoint.McpCreateCard)]
    [InlineData(EntryPoint.McpDuplicateCard)]
    public async Task NumberingACard_LosingEveryAttemptButTheLast_StillNumbersIt(EntryPoint entryPoint)
    {
        // Arrange
        var name = $"Last attempt {entryPoint}";
        var numbering = await PrepareAsync(entryPoint, name);
        var highestBefore = await HighestNumberAsync();

        _factory.Interceptor.Arm(_factory.DefaultBoardId, AllocatorRetryBudget.Attempts - 1);

        try
        {
            // Act
            var outcome = await NumberAsync(numbering);

            // Assert — every collision but the last was met, and the request still answered.
            _factory.Interceptor.FiredCount.ShouldBe(AllocatorRetryBudget.Attempts - 1);
            outcome.Succeeded.ShouldBeTrue();
        }
        finally
        {
            _factory.Interceptor.Disarm();
        }

        // Each rival took the number the request had just claimed, so the request lands one past all
        // of them, numbered and no longer a draft.
        var card = await FindCardAsync(name);

        card.ShouldNotBeNull();
        card.Number.ShouldBe(highestBefore + AllocatorRetryBudget.Attempts);
        card.IsTemp.ShouldBeFalse();
    }

    [Theory]
    [InlineData(EntryPoint.RestCreate)]
    [InlineData(EntryPoint.RestFinalize)]
    [InlineData(EntryPoint.McpCreateCard)]
    [InlineData(EntryPoint.McpDuplicateCard)]
    public async Task NumberingACard_LosingEveryAttempt_AsksTheCallerToTryAgain(EntryPoint entryPoint)
    {
        // Arrange
        var name = $"Exhaustion {entryPoint}";
        var numbering = await PrepareAsync(entryPoint, name);

        _factory.Interceptor.Arm(_factory.DefaultBoardId, AllocatorRetryBudget.Attempts);

        try
        {
            // Act
            var outcome = await NumberAsync(numbering);

            // Assert — the fired count is what separates exhaustion from a first-collision failure on
            // an entry point that never retried at all; both fail the request.
            _factory.Interceptor.FiredCount.ShouldBe(AllocatorRetryBudget.Attempts);
            outcome.Succeeded.ShouldBeFalse();
            outcome.ShouldHaveAskedToTryAgain(_contendedReason);
        }
        finally
        {
            _factory.Interceptor.Disarm();
        }

        // Nothing was numbered: a create or duplicate left no card behind, and a finalize left the
        // draft a draft.
        var card = await FindCardAsync(name);

        if (entryPoint == EntryPoint.RestFinalize)
        {
            card.ShouldNotBeNull();
            card.IsTemp.ShouldBeTrue();
            card.Number.ShouldBe(0);
        }
        else
        {
            card.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(AllocatorRetryBudget.Attempts - 1)]
    public async Task FinalizingADraft_UnderCardNumberContention_StampsTheFinalizeTimeAndShowsInASincePoll(int collisions)
    {
        // Arrange — a client watching the board polls for activity since a moment after the draft
        // began. The draft's own stamps are its start time, so the guard below is what makes the
        // poll able to tell a finalize stamp from a draft-start stamp at all.
        var name = string.Create(CultureInfo.InvariantCulture, $"Finalize stamp {collisions}");
        var numbering = await PrepareAsync(EntryPoint.RestFinalize, name);
        var draft = await FindCardAsync(name);
        var pollFromUtc = DateTimeOffset.UtcNow;

        draft.ShouldNotBeNull();
        draft.LastUpdatedAtUtc.ShouldBeLessThan(pollFromUtc);

        _factory.Interceptor.Arm(_factory.DefaultBoardId, collisions);

        try
        {
            // Act
            var outcome = await NumberAsync(numbering);

            _factory.Interceptor.FiredCount.ShouldBe(collisions);
            outcome.Succeeded.ShouldBeTrue();
        }
        finally
        {
            _factory.Interceptor.Disarm();
        }

        // Assert — however many races the finalize lost on the way, it carries the time it was
        // finalized, and so it is in the poll.
        var card = await FindCardAsync(name);

        card.ShouldNotBeNull();
        card.LastUpdatedAtUtc.ShouldBeGreaterThanOrEqualTo(pollFromUtc);

        var polled = await PollCardIdsSinceAsync(pollFromUtc);

        polled.ShouldContain(card.Id);
    }

    // Everything an entry point needs before it is armed: the draft to finalize or the card to
    // duplicate are written first, so creating them spends none of the forced collisions.
    private async Task<Numbering> PrepareAsync(EntryPoint entryPoint, string name)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var laneId = await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

        var existingCardId = entryPoint switch
        {
            EntryPoint.RestFinalize => await CreateCardAsync("cards/temp", name, laneId),
            EntryPoint.McpDuplicateCard => await CreateCardAsync("cards", $"Source of {name}", laneId),
            _ => (Guid?)null
        };

        return new Numbering(entryPoint, name, laneId, existingCardId);
    }

    private Task<WriteOutcome> NumberAsync(Numbering numbering) => numbering.EntryPoint switch
    {
        EntryPoint.RestCreate => CreateOverRestAsync(numbering),
        EntryPoint.RestFinalize => FinalizeOverRestAsync(numbering),
        EntryPoint.McpCreateCard => CreateOverMcpAsync(numbering),
        EntryPoint.McpDuplicateCard => DuplicateOverMcpAsync(numbering),
        _ => throw new ArgumentOutOfRangeException(nameof(numbering), numbering.EntryPoint, "Unknown entry point.")
    };

    private async Task<WriteOutcome> CreateOverRestAsync(Numbering numbering)
    {
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name = numbering.Name, laneId = numbering.LaneId }
        );

        return await WriteOutcome.FromResponseAsync(response);
    }

    private async Task<WriteOutcome> FinalizeOverRestAsync(Numbering numbering) =>
        await WriteOutcome.FromResponseAsync(await _client.PostAsync($"/api/v1/cards/{numbering.ExistingCardId}/finalize", null));

    private async Task<WriteOutcome> CreateOverMcpAsync(Numbering numbering)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var tools = new CardTools(db, new McpAuthService(new UserResolver(db)), broadcaster);

        return await WriteOutcome.FromToolAsync(() =>
            tools.CreateCardAsync(CollatticeApiFactory.TestAdminAuthKey, numbering.Name, numbering.LaneId));
    }

    private async Task<WriteOutcome> DuplicateOverMcpAsync(Numbering numbering)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        var tools = new DuplicateCardTools(db, new McpAuthService(new UserResolver(db)), broadcaster);

        return await WriteOutcome.FromToolAsync(() =>
            tools.DuplicateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: numbering.ExistingCardId, name: numbering.Name));
    }

    private async Task<Guid> CreateCardAsync(string route, string name, Guid laneId)
    {
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/{route}",
            new { name, laneId }
        );
        response.EnsureSuccessStatusCode();

        var card = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);

        return card.GetProperty("id").GetGuid();
    }

    private async Task<long> HighestNumberAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        return await db.Cards
            .Where(c => c.BoardId == _factory.DefaultBoardId && c.Number > 0)
                .MaxAsync(c => (long?)c.Number) ?? 0;
    }

    private async Task<CardItem?> FindCardAsync(string name)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        return await db.Cards
            .AsNoTracking()
                .SingleOrDefaultAsync(c => c.BoardId == _factory.DefaultBoardId && c.Name == name);
    }

    private async Task<List<Guid>> PollCardIdsSinceAsync(DateTimeOffset since)
    {
        var sinceParameter = Uri.EscapeDataString(since.ToString("O", CultureInfo.InvariantCulture));
        var page = await _client.GetFromJsonAsync<PagedResult<JsonElement>>
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards?since={sinceParameter}",
            TestAuthHelper.JsonOptions
        );

        page.ShouldNotBeNull();

        return [.. page.Items.Select(c => c.GetProperty("id").GetGuid())];
    }

    private sealed record Numbering(EntryPoint EntryPoint, string Name, Guid LaneId, Guid? ExistingCardId);
}
