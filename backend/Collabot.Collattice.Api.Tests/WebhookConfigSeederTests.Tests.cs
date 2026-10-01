using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Config-migration seeder tests. The seeder is a deterministic static seam driven directly
// (the BoardSeeder / SweepAsync pattern). The WAF boot test proves the load-bearing gate-catch: the
// seed fires on an upgrade where users ALREADY exist — because it gates on an empty subscription
// table, not the !Users.AnyAsync() fresh-install gate.
public class WebhookConfigSeederTests
{
    [Fact]
    public async Task Seed_OnEmptyTable_CreatesOneSubscription_WithV1ParitySelection()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var seeded = await WebhookConfigSeeder.SeedAsync(db, "https://n8n.example/hook", "shared-secret", CancellationToken.None);

        seeded.ShouldNotBeNull();
        seeded!.Url.ShouldBe("https://n8n.example/hook");
        seeded.Secret.ShouldBe("shared-secret");
        seeded.Name.ShouldBe("Migrated from configuration");
        seeded.Enabled.ShouldBeTrue();
        // v1 parity — exactly the two live events, NOT the wildcard (no silent behavior change).
        seeded.EventTypes.ShouldBe([WebhookEventTypes.CardCreated, WebhookEventTypes.CardMoved], ignoreOrder: true);

        (await db.WebhookSubscriptions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Seed_IsIdempotent_DoesNotReSeedWhenTableNonEmpty()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var first = await WebhookConfigSeeder.SeedAsync(db, "https://n8n.example/hook", null, CancellationToken.None);
        first.ShouldNotBeNull();

        var second = await WebhookConfigSeeder.SeedAsync(db, "https://n8n.example/hook", null, CancellationToken.None);
        second.ShouldBeNull();   // already migrated

        (await db.WebhookSubscriptions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Seed_NoEndpoint_IsNoOp()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        (await WebhookConfigSeeder.SeedAsync(db, null, null, CancellationToken.None)).ShouldBeNull();
        (await WebhookConfigSeeder.SeedAsync(db, "  ", null, CancellationToken.None)).ShouldBeNull();

        (await db.WebhookSubscriptions.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Boot_WithEndpointSet_SeedsDespiteUsersAlreadyExisting()
    {
        // The gate-catch: production already has users, so the seed must NOT reuse the
        // !Users.AnyAsync() fresh-install gate. Boot the real host with Webhooks:Endpoint set; the
        // seed fires at startup even though the admin user was already seeded.
        await using var factory = new CollatticeApiFactory
        {
            ConfigOverrides = new Dictionary<string, string?>
            {
                ["Webhooks:Endpoint"] = "https://migrated.example/hook",
                ["Webhooks:Secret"] = "migrated-secret",
            },
        };
        await factory.InitializeAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        // Users exist (the fresh-install gate already ran and seeded the admin) ...
        (await db.Users.AnyAsync()).ShouldBeTrue();

        // ... and the webhook seed STILL fired (the gate is the empty subscription table).
        var subs = await db.WebhookSubscriptions.ToListAsync();
        subs.Count.ShouldBe(1);
        subs[0].Url.ShouldBe("https://migrated.example/hook");
        subs[0].Name.ShouldBe("Migrated from configuration");
        subs[0].EventTypes.ShouldBe([WebhookEventTypes.CardCreated, WebhookEventTypes.CardMoved], ignoreOrder: true);
    }

    private static async Task<CollatticeApiFactory> NewFactoryAsync()
    {
        var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        return factory;
    }
}
