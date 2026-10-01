using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Models;
using Collabot.Collattice.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// A draft is finalized or cancelled after a check that it is still a draft, and the check and the save
// are separate statements. Two requests for one draft at once (a double-click, or a client retrying a
// slow request) can both pass the check. Whichever saves second must get the answer it would have got
// arriving second, and must announce nothing: a finalized card is announced once, and a finalized card
// is never deleted by a cancel that read it as a draft.
public class DraftRaceTests(DraftRaceFactory factory) : IClassFixture<DraftRaceFactory>
{
    private const string _notADraft = "Card is not a temp card.";

    private readonly DraftRaceFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task FinalizingADraft_WhileAnotherFinalizeOfItSavesFirst_AnswersNotADraftAndAnnouncesOnce()
    {
        // Arrange
        var draftId = await CreateDraftAsync("Finalized twice");
        HttpResponseMessage? rival = null;

        _factory.Interceptor.Arm(draftId, async () => rival = await FinalizeAsync(draftId));
        _factory.Sink.Clear();

        // Act
        HttpResponseMessage response;
        try
        {
            response = await FinalizeAsync(draftId);
        }
        finally
        {
            _factory.Interceptor.Disarm();
        }

        // Assert — the rival finalized the draft first; this request answers as a second finalize does
        _factory.Interceptor.FiredCount.ShouldBe(1);
        rival.ShouldNotBeNull().StatusCode.ShouldBe(HttpStatusCode.OK);
        var announcedNumber = (await rival.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("number").GetInt64();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(_notADraft);

        CreatedEventsFor(draftId).ShouldBe(1);

        var card = await FindCardAsync(draftId);

        card.ShouldNotBeNull();
        card.IsTemp.ShouldBeFalse();
        card.Number.ShouldBe(announcedNumber);
    }

    [Fact]
    public async Task CancellingADraft_WhileAFinalizeOfItSavesFirst_AnswersNotADraftAndKeepsTheCard()
    {
        // Arrange
        var draftId = await CreateDraftAsync("Cancelled while finalized");
        HttpResponseMessage? rival = null;

        _factory.Interceptor.Arm(draftId, async () => rival = await FinalizeAsync(draftId));
        _factory.Sink.Clear();

        // Act
        HttpResponseMessage response;
        try
        {
            response = await _client.PostAsync($"/api/v1/cards/{draftId}/cancel", null);
        }
        finally
        {
            _factory.Interceptor.Disarm();
        }

        // Assert — the card the finalize announced is still there
        _factory.Interceptor.FiredCount.ShouldBe(1);
        rival.ShouldNotBeNull().StatusCode.ShouldBe(HttpStatusCode.OK);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(_notADraft);

        CreatedEventsFor(draftId).ShouldBe(1);

        var card = await FindCardAsync(draftId);

        card.ShouldNotBeNull();
        card.IsTemp.ShouldBeFalse();
    }

    [Fact]
    public async Task FinalizingADraft_WhileACancelOfItSavesFirst_AnswersNotFoundAndAnnouncesNothing()
    {
        // Arrange
        var draftId = await CreateDraftAsync("Finalized while cancelled");
        HttpResponseMessage? rival = null;

        _factory.Interceptor.Arm(draftId, async () => rival = await _client.PostAsync($"/api/v1/cards/{draftId}/cancel", null));
        _factory.Sink.Clear();

        // Act
        HttpResponseMessage response;
        try
        {
            response = await FinalizeAsync(draftId);
        }
        finally
        {
            _factory.Interceptor.Disarm();
        }

        // Assert — the draft is gone, so this finalize answers as one arriving after the cancel does
        _factory.Interceptor.FiredCount.ShouldBe(1);
        rival.ShouldNotBeNull().StatusCode.ShouldBe(HttpStatusCode.NoContent);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        CreatedEventsFor(draftId).ShouldBe(0);
        (await FindCardAsync(draftId)).ShouldBeNull();
    }

    private async Task<Guid> CreateDraftAsync(string name)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var laneId = await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards/temp", new { name, laneId });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> FinalizeAsync(Guid draftId) =>
        _client.PostAsync($"/api/v1/cards/{draftId}/finalize", null);

    private int CreatedEventsFor(Guid cardId) =>
        _factory.Sink.Captured
            .Count(e => e.EventType == WebhookEventTypes.CardCreated && e.Data is WebhookCardCreatedData data && data.Card.Id == cardId);

    private async Task<CardItem?> FindCardAsync(Guid cardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        return await db.Cards
            .AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == cardId);
    }
}
