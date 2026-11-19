using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Retention sweep tests. The deletion logic is the deterministic static SweepAsync seam
// (driven directly, never racing the hosted loop against the
// shared in-memory connection). The dormancy gate (DeliveryLogRetentionDays <= 0) is verified
// through the hosted service over a configured WAF, and the first sweep's timing through a running
// host over a database file of its own.
public class WebhookDeliveryLogSweepServiceTests
{
    [Fact]
    public async Task Sweep_MixedAgeAttempts_DeletesOnlyAttemptsOlderThanCutoff()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var old1 = AddAttempt(db, DateTimeOffset.UtcNow.AddDays(-40));
        var old2 = AddAttempt(db, DateTimeOffset.UtcNow.AddDays(-31));
        var fresh = AddAttempt(db, DateTimeOffset.UtcNow.AddDays(-1));
        await db.SaveChangesAsync();

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var deleted = await WebhookDeliveryLogSweepService.SweepAsync(db, cutoff, CancellationToken.None);

        deleted.ShouldBe(2);
        (await db.WebhookDeliveryAttempts.AnyAsync(a => a.Id == old1)).ShouldBeFalse();
        (await db.WebhookDeliveryAttempts.AnyAsync(a => a.Id == old2)).ShouldBeFalse();
        (await db.WebhookDeliveryAttempts.AnyAsync(a => a.Id == fresh)).ShouldBeTrue();
    }

    [Fact]
    public async Task Sweep_NoAgedRows_DeletesNothing()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        AddAttempt(db, DateTimeOffset.UtcNow.AddDays(-1));
        await db.SaveChangesAsync();

        var deleted = await WebhookDeliveryLogSweepService.SweepAsync(db, DateTimeOffset.UtcNow.AddDays(-30), CancellationToken.None);
        deleted.ShouldBe(0);
    }

    [Fact]
    public async Task RetentionZero_HostedSweepIsDormant_LeavesOldRows()
    {
        await using var factory = CollatticeApiFactory.WithConfig(new Dictionary<string, string?>
        {
            ["Webhooks:DeliveryLogRetentionDays"] = "0",
        });
        await factory.InitializeAsync();

        Guid attemptId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
            attemptId = AddAttempt(db, DateTimeOffset.UtcNow.AddDays(-100));   // far past any plausible cutoff
            await db.SaveChangesAsync();
        }

        // Retention 0 → the hosted sweep returns before sweeping (dormant). A non-dormant sweep with
        // a now-minus-0-days cutoff would delete every row; the old row surviving proves dormancy.
        await Task.Delay(300);

        await using var verify = factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<BoardDbContext>();
        (await verifyDb.WebhookDeliveryAttempts.AnyAsync(a => a.Id == attemptId)).ShouldBeTrue();
    }

    [Fact]
    public async Task HostedSweep_FirstSweepIsOneIntervalAfterStartup_NotADay()
    {
        // Arrange — a running API whose sweep interval is one second, over a database file the
        // hosted sweep can work on alongside the test.
        var databasePath = PersistentDatabaseFactory.NewDatabasePath();

        try
        {
            var host = new PersistentDatabaseFactory(databasePath)
            {
                ConfigOverrides = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Webhooks:DeliveryLogSweepInterval"] = "00:00:01",
                },
            };

            try
            {
                await host.InitializeAsync();

                Guid expired;
                Guid retained;

                await using (var scope = host.Services.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
                    expired = AddAttempt(db, DateTimeOffset.UtcNow.AddDays(-40));
                    retained = AddAttempt(db, DateTimeOffset.UtcNow.AddDays(-1));
                    await db.SaveChangesAsync();
                }

                // Act — wait for the first sweep, with a generous bound for a loaded box.
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);

                while (await AttemptExistsAsync(host, expired) && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                }

                // Assert — the row past the 30-day retention is gone, the recent one kept.
                (await AttemptExistsAsync(host, expired)).ShouldBeFalse();
                (await AttemptExistsAsync(host, retained)).ShouldBeTrue();
            }
            finally
            {
                await host.DisposeAsync();
            }
        }
        finally
        {
            PersistentDatabaseFactory.DeleteDatabaseFiles(databasePath);
        }
    }

    private static async Task<bool> AttemptExistsAsync(PersistentDatabaseFactory host, Guid attemptId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        return await db.WebhookDeliveryAttempts.AnyAsync(a => a.Id == attemptId);
    }

    private static Guid AddAttempt(BoardDbContext db, DateTimeOffset at)
    {
        var attempt = new WebhookDeliveryAttempt
        {
            Id = Guid.NewGuid(),
            SubscriptionId = null,   // null is valid (v1/seed/orphaned rows); the sweep deletes by time, not FK
            EventId = Ulid.NewUlid().ToString(),
            EventType = WebhookEventTypes.CardCreated,
            BoardId = Guid.NewGuid(),
            Attempt = 1,
            Status = WebhookDeliveryStatus.Succeeded,
            AttemptedAtUtc = at,
        };
        db.WebhookDeliveryAttempts.Add(attempt);
        return attempt.Id;
    }
}
