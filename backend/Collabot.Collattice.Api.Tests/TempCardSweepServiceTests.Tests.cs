using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Collabot.Collattice.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class TempCardSweepServiceTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> GetFirstLaneIdAsync()
        => await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

    private async Task<Guid> CreateTempCardAsync()
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var payload = new
        {
            name = "Sweep Temp Card",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999),
        };

        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards/temp",
            payload
        );
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    private async Task<Guid> AddAttachmentAsync(Guid cardId)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([1, 2, 3, 4]);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "sweep-test.bin");

        var response = await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", content);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    // Back-dates a card's CreatedAtUtc directly, simulating a temp card that has been
    // sitting orphaned (the create-temp endpoint always stamps CreatedAtUtc = now).
    private async Task BackdateCreatedAtAsync(Guid cardId, DateTimeOffset createdAt)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var card = await db.Cards.FindAsync(cardId);
        card.ShouldNotBeNull();
        card.CreatedAtUtc = createdAt;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sweep_RemovesAgedTempCard_KeepsFreshTempAndRealCard()
    {
        // Arrange — an aged temp card (with an attachment), a fresh temp card, and a real card.
        var agedTempId = await CreateTempCardAsync();
        await AddAttachmentAsync(agedTempId);
        await BackdateCreatedAtAsync(agedTempId, DateTimeOffset.UtcNow.AddHours(-3));

        var freshTempId = await CreateTempCardAsync();

        var realCardId = await CreateTempCardAsync();
        var finalizeResponse = await _client.PostAsync($"/api/v1/cards/{realCardId}/finalize", null);
        finalizeResponse.EnsureSuccessStatusCode();

        // Act — one sweep tick with a 1-hour TTL cutoff.
        int deleted;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
            deleted = await TempCardSweepService.SweepAsync(db, cutoff, CancellationToken.None);
        }

        // Assert — exactly the aged temp card removed; fresh temp and real card survive.
        deleted.ShouldBe(1);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<BoardDbContext>();

        (await verifyDb.Cards.AnyAsync(c => c.Id == agedTempId)).ShouldBeFalse();
        (await verifyDb.Cards.AnyAsync(c => c.Id == freshTempId)).ShouldBeTrue();
        (await verifyDb.Cards.AnyAsync(c => c.Id == realCardId)).ShouldBeTrue();

        // Cascade — the aged temp card's attachment is gone with it.
        (await verifyDb.Attachments.AnyAsync(a => a.CardId == agedTempId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Sweep_DoesNotDeleteAgedCardOnceFinalized()
    {
        // Arrange — a card created in the past but finalized (IsTemp = false). The sweep's
        // WHERE predicate filters on IsTemp, so an old-but-real card must survive. This is the
        // structural guard against the finalize-vs-sweep race: a card that flipped IsTemp=false
        // no longer matches the delete, even though its CreatedAtUtc is well past the cutoff.
        var cardId = await CreateTempCardAsync();
        var finalizeResponse = await _client.PostAsync($"/api/v1/cards/{cardId}/finalize", null);
        finalizeResponse.EnsureSuccessStatusCode();
        await BackdateCreatedAtAsync(cardId, DateTimeOffset.UtcNow.AddHours(-3));

        // Act
        int deleted;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
            deleted = await TempCardSweepService.SweepAsync(db, cutoff, CancellationToken.None);
        }

        // Assert — the finalized card is untouched.
        deleted.ShouldBe(0);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<BoardDbContext>();
        (await verifyDb.Cards.AnyAsync(c => c.Id == cardId)).ShouldBeTrue();
    }

    [Fact]
    public async Task Sweep_IsIdempotent_SecondRunDeletesNothing()
    {
        // Arrange — one aged temp card.
        var agedTempId = await CreateTempCardAsync();
        await BackdateCreatedAtAsync(agedTempId, DateTimeOffset.UtcNow.AddHours(-3));

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);

        // Act — two consecutive sweeps over the same state.
        int firstDeleted;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
            firstDeleted = await TempCardSweepService.SweepAsync(db, cutoff, CancellationToken.None);
        }

        int secondDeleted;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
            secondDeleted = await TempCardSweepService.SweepAsync(db, cutoff, CancellationToken.None);
        }

        // Assert — first run removes the orphan, second run is a no-op.
        firstDeleted.ShouldBe(1);
        secondDeleted.ShouldBe(0);
    }

    [Fact]
    public async Task HostedSweep_AfterRestart_LeavesOrphanUntilFirstTick()
    {
        // Arrange — a draft orphaned before a restart, and a restarted host whose first tick is an
        // hour away.
        var databasePath = PersistentDatabaseFactory.NewDatabasePath();

        try
        {
            var draftId = await LeaveAgedDraftAsync(databasePath);

            var restarted = new PersistentDatabaseFactory(databasePath)
            {
                ConfigOverrides = SweepEvery("01:00:00"),
            };

            try
            {
                await restarted.InitializeAsync();

                // Act — a sweep at startup completes within milliseconds of the host starting, so two
                // seconds is ample time for one to have run, and far short of the first tick.
                await Task.Delay(TimeSpan.FromSeconds(2));

                // Assert
                (await DraftExistsAsync(restarted, draftId)).ShouldBeTrue();
            }
            finally
            {
                await restarted.DisposeAsync();
            }
        }
        finally
        {
            PersistentDatabaseFactory.DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task HostedSweep_AfterRestart_RemovesOrphanOnFirstTick()
    {
        // Arrange — the same orphan, and a restarted host that ticks every second. This is also what
        // keeps the test above honest: it proves the restarted host runs the sweep at all.
        var databasePath = PersistentDatabaseFactory.NewDatabasePath();

        try
        {
            var draftId = await LeaveAgedDraftAsync(databasePath);

            var restarted = new PersistentDatabaseFactory(databasePath)
            {
                ConfigOverrides = SweepEvery("00:00:01"),
            };

            try
            {
                await restarted.InitializeAsync();

                // Act — wait for the first tick to remove it, with a generous bound for a loaded box.
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);

                while (await DraftExistsAsync(restarted, draftId) && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                }

                // Assert
                (await DraftExistsAsync(restarted, draftId)).ShouldBeFalse();
            }
            finally
            {
                await restarted.DisposeAsync();
            }
        }
        finally
        {
            PersistentDatabaseFactory.DeleteDatabaseFiles(databasePath);
        }
    }

    private static Dictionary<string, string?> SweepEvery(string interval) =>
        new(StringComparer.Ordinal) { ["TempCardSweep:SweepInterval"] = interval };

    // Creates a draft on a first host, ages it past the one-hour TTL and stops that host: a browser
    // that closed mid-create before the API restarted.
    private static async Task<Guid> LeaveAgedDraftAsync(string databasePath)
    {
        var host = new PersistentDatabaseFactory(databasePath);

        try
        {
            await host.InitializeAsync();

            var client = host.CreateClient();
            TestAuthHelper.SetAdminAuth(client, host);

            var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, host.DefaultBoardId);

            var response = await client.PostAsJsonAsync
            (
                $"/api/v1/boards/{host.DefaultBoardId}/cards/temp",
                new { name = "Orphaned draft", descriptionMarkdown = "", laneId, position = 10 }
            );
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
            var draftId = json.GetProperty("id").GetGuid();

            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

            var draft = await db.Cards.SingleAsync(c => c.Id == draftId);
            draft.CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-3);

            await db.SaveChangesAsync();

            return draftId;
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task<bool> DraftExistsAsync(PersistentDatabaseFactory host, Guid draftId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        return await db.Cards.AnyAsync(c => c.Id == draftId);
    }
}
