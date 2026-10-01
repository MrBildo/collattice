using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// WebhookSubscriptionStore tests — the shared CRUD/validation core both REST and MCP
// delegate to. The store is tested directly against a WAF DI scope (the MCP-tools convention), not
// through an HTTP surface (the store has no HTTP surface of its own). The load-bearing security assertions live
// here: the write-only secret never appears in any read projection, and the SSRF registration check
// is un-bypassable.
public sealed class WebhookSubscriptionStoreTests
{
    private const string _publicUrl = "https://8.8.8.8/hook";

    // A genuinely-private RFC1918 LAN target — the legitimate allowPrivate case. Loopback and the
    // cloud-metadata endpoint are NOT private LAN targets; they stay blocked even with the flag on
    // (see the carve-out tests below), so they are kept as separate literals.
    private const string _privateUrl = "http://10.0.0.1/hook";
    private const string _loopbackUrl = "http://127.0.0.1/hook";
    private const string _metadataUrl = "http://169.254.169.254/latest/meta-data";

    // ── Create + validation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Create_PersistsAndReturnsSecretFreeView()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = NewStore(scope, allowPrivate: false);

        var view = await store.CreateAsync(
            new WebhookSubscriptionInput(_publicUrl, [WebhookEventTypes.CardCreated], "the-secret", Enabled: true, "prod"),
            CancellationToken.None);

        view.Url.ShouldBe(_publicUrl);
        view.Name.ShouldBe("prod");
        view.Enabled.ShouldBeTrue();
        view.Events.ShouldBe([WebhookEventTypes.CardCreated]);
        view.Signed.ShouldBeTrue();
        view.SuccessCount.ShouldBe(0);

        // The row really persisted.
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        (await db.WebhookSubscriptions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Create_EmptyEvents_IsRejected()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = NewStore(scope, allowPrivate: false);

        await Should.ThrowAsync<WebhookValidationException>(() =>
            store.CreateAsync(new WebhookSubscriptionInput(_publicUrl, [], null, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Create_UnknownEvent_IsRejected()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = NewStore(scope, allowPrivate: false);

        await Should.ThrowAsync<WebhookValidationException>(() =>
            store.CreateAsync(new WebhookSubscriptionInput(_publicUrl, ["nonexistent.event"], null, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Create_Wildcard_CollapsesToWildcardAlone()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = NewStore(scope, allowPrivate: false);

        var view = await store.CreateAsync(
            new WebhookSubscriptionInput(_publicUrl, [WebhookEventTypes.Wildcard, WebhookEventTypes.CardCreated], null, null, null),
            CancellationToken.None);

        // The wildcard stands alone; the co-listed explicit type is dropped.
        view.Events.ShouldBe([WebhookEventTypes.Wildcard]);
    }

    [Fact]
    public async Task Create_PrivateUrl_FlagOff_IsRejectedByStore()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = NewStore(scope, allowPrivate: false);

        // The SSRF registration check is in the shared store — un-bypassable by any surface.
        await Should.ThrowAsync<WebhookValidationException>(() =>
            store.CreateAsync(new WebhookSubscriptionInput(_privateUrl, [WebhookEventTypes.CardCreated], null, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Create_PrivateUrl_FlagOn_IsAccepted()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = NewStore(scope, allowPrivate: true);

        var view = await store.CreateAsync(
            new WebhookSubscriptionInput(_privateUrl, [WebhookEventTypes.CardCreated], null, null, null),
            CancellationToken.None);

        view.Url.ShouldBe(_privateUrl);
    }

    [Theory]
    [InlineData(_loopbackUrl)]
    [InlineData(_metadataUrl)]
    public async Task Create_LoopbackOrMetadata_FlagOn_IsStillRejected(string url)
    {
        // The carve-out at the un-bypassable seam: allowPrivate re-permits RFC1918 LAN targets, but
        // never loopback or the cloud-metadata endpoint — so flipping the flag to reach a LAN host
        // cannot also open an SSRF path to the metadata service.
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = NewStore(scope, allowPrivate: true);

        await Should.ThrowAsync<WebhookValidationException>(() =>
            store.CreateAsync(new WebhookSubscriptionInput(url, [WebhookEventTypes.CardCreated], null, null, null), CancellationToken.None));
    }

    // ── The load-bearing security assertion: the secret never leaks ──────────────

    [Fact]
    public async Task Secret_NeverAppearsInAnyReadProjection()
    {
        const string secret = "top-secret-signing-key-value";

        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = NewStore(scope, allowPrivate: false);

        var created = await store.CreateAsync(
            new WebhookSubscriptionInput(_publicUrl, [WebhookEventTypes.CardMoved], secret, null, "signed"),
            CancellationToken.None);

        var fetched = await store.GetAsync(created.Id, CancellationToken.None);
        var listed = await store.ListAsync(CancellationToken.None);

        // Serialize the views exactly as a caller (REST/MCP) would, and assert the secret string is
        // nowhere in them — only the `signed` boolean derives from it.
        var createdJson = JsonSerializer.Serialize(created);
        var fetchedJson = JsonSerializer.Serialize(fetched);
        var listedJson = JsonSerializer.Serialize(listed);

        createdJson.ShouldNotContain(secret);
        fetchedJson.ShouldNotContain(secret);
        listedJson.ShouldNotContain(secret);

        created.Signed.ShouldBeTrue();
        fetched!.Signed.ShouldBeTrue();
    }

    // ── Secret set / keep / clear ────────────────────────────────────────────────

    [Fact]
    public async Task Update_UrlOnly_LeavesSecretUnchanged()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = NewStore(scope, allowPrivate: false);

        var created = await store.CreateAsync(
            new WebhookSubscriptionInput(_publicUrl, [WebhookEventTypes.CardCreated], "keep-me", null, null),
            CancellationToken.None);

        var updated = await store.UpdateAsync(
            created.Id,
            new WebhookSubscriptionPatch("https://1.1.1.1/hook", Events: null, Secret: null, ClearSecret: false, Enabled: null, Name: null),
            CancellationToken.None);

        updated!.Url.ShouldBe("https://1.1.1.1/hook");
        updated.Signed.ShouldBeTrue();   // secret omitted → unchanged
    }

    [Fact]
    public async Task Update_SecretValue_Replaces()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = NewStore(scope, allowPrivate: false);

        var created = await store.CreateAsync(
            new WebhookSubscriptionInput(_publicUrl, [WebhookEventTypes.CardCreated], null, null, null),
            CancellationToken.None);
        created.Signed.ShouldBeFalse();   // created unsigned

        var updated = await store.UpdateAsync(
            created.Id,
            new WebhookSubscriptionPatch(Url: null, Events: null, Secret: "now-signed", ClearSecret: false, Enabled: null, Name: null),
            CancellationToken.None);

        updated!.Signed.ShouldBeTrue();
    }

    [Fact]
    public async Task Update_ClearSecret_GoesUnsigned()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = NewStore(scope, allowPrivate: false);

        var created = await store.CreateAsync(
            new WebhookSubscriptionInput(_publicUrl, [WebhookEventTypes.CardCreated], "drop-me", null, null),
            CancellationToken.None);

        var updated = await store.UpdateAsync(
            created.Id,
            new WebhookSubscriptionPatch(Url: null, Events: null, Secret: null, ClearSecret: true, Enabled: null, Name: null),
            CancellationToken.None);

        updated!.Signed.ShouldBeFalse();
    }

    // ── PATCH re-validates the URL only when it changes ─────────────────────

    [Fact]
    public async Task Update_DisableMigratedPrivateUrl_FlagOff_Succeeds_WithoutReValidatingUrl()
    {
        // The migrated private-URL subscription (seeded directly, not via the store's validator).
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var store = NewStore(scope, allowPrivate: false);

        var seeded = SeedSubscription(db, _privateUrl, [WebhookEventTypes.CardCreated]);
        await db.SaveChangesAsync();

        // The operator's remediation: disable the failing webhook. Must NOT re-validate the
        // unchanged private URL — otherwise the row could only be deleted, never disabled.
        var updated = await store.UpdateAsync(
            seeded.Id,
            new WebhookSubscriptionPatch(Url: null, Events: null, Secret: null, ClearSecret: false, Enabled: false, Name: null),
            CancellationToken.None);

        updated!.Enabled.ShouldBeFalse();
        updated.Url.ShouldBe(_privateUrl);
    }

    [Fact]
    public async Task Update_ChangingUrlToPrivate_FlagOff_IsRejected()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = NewStore(scope, allowPrivate: false);

        var created = await store.CreateAsync(
            new WebhookSubscriptionInput(_publicUrl, [WebhookEventTypes.CardCreated], null, null, null),
            CancellationToken.None);

        // A URL-changing patch DOES re-validate.
        await Should.ThrowAsync<WebhookValidationException>(() =>
            store.UpdateAsync(
                created.Id,
                new WebhookSubscriptionPatch(_privateUrl, Events: null, Secret: null, ClearSecret: false, Enabled: null, Name: null),
                CancellationToken.None));
    }

    // ── Update events (replace-only) + delete ────────────────────────────────────

    [Fact]
    public async Task Update_Events_ReplacesSelection()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = NewStore(scope, allowPrivate: false);

        var created = await store.CreateAsync(
            new WebhookSubscriptionInput(_publicUrl, [WebhookEventTypes.CardCreated], null, null, null),
            CancellationToken.None);

        var updated = await store.UpdateAsync(
            created.Id,
            new WebhookSubscriptionPatch(Url: null, Events: [WebhookEventTypes.CardMoved], Secret: null, ClearSecret: false, Enabled: null, Name: null),
            CancellationToken.None);

        updated!.Events.ShouldBe([WebhookEventTypes.CardMoved]);

        // Round-trips through the value converter on a fresh read.
        var refetched = await store.GetAsync(created.Id, CancellationToken.None);
        refetched!.Events.ShouldBe([WebhookEventTypes.CardMoved]);
    }

    [Fact]
    public async Task Delete_RemovesTheRow()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var store = NewStore(scope, allowPrivate: false);

        var created = await store.CreateAsync(
            new WebhookSubscriptionInput(_publicUrl, [WebhookEventTypes.CardCreated], null, null, null),
            CancellationToken.None);

        (await store.DeleteAsync(created.Id, CancellationToken.None)).ShouldBeTrue();
        (await db.WebhookSubscriptions.FindAsync(created.Id))!.ShouldBeNull();
        (await store.DeleteAsync(created.Id, CancellationToken.None)).ShouldBeFalse();   // already gone
    }

    // ── On-read metrics ──────────────────────────────────────────────────────────

    [Fact]
    public async Task List_EnrichesWithOnReadMetrics()
    {
        await using var factory = await NewFactoryAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var store = NewStore(scope, allowPrivate: false);

        var created = await store.CreateAsync(
            new WebhookSubscriptionInput(_publicUrl, [WebhookEventTypes.CardMoved], null, null, null),
            CancellationToken.None);

        var older = DateTimeOffset.UtcNow.AddMinutes(-5);
        var newer = DateTimeOffset.UtcNow;
        AddAttempt(db, created.Id, WebhookDeliveryStatus.Failed, older);
        AddAttempt(db, created.Id, WebhookDeliveryStatus.Succeeded, newer);
        AddAttempt(db, created.Id, WebhookDeliveryStatus.Succeeded, newer);
        await db.SaveChangesAsync();

        var view = (await store.ListAsync(CancellationToken.None)).Single(v => v.Id == created.Id);

        view.SuccessCount.ShouldBe(2);
        view.FailureCount.ShouldBe(1);
        view.LastDeliveryStatus.ShouldBe("Succeeded");   // newest wins
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<CollatticeApiFactory> NewFactoryAsync()
    {
        var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();
        return factory;
    }

    private static WebhookSubscriptionStore NewStore(AsyncServiceScope scope, bool allowPrivate)
    {
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var settings = Options.Create(new WebhookSettings { AllowPrivateNetworkTargets = allowPrivate });
        return new WebhookSubscriptionStore(db, settings);
    }

    private static WebhookSubscription SeedSubscription(BoardDbContext db, string url, IList<string> events)
    {
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Url = url,
            Enabled = true,
            EventTypes = events,
        };
        db.WebhookSubscriptions.Add(sub);
        return sub;
    }

    private static void AddAttempt(BoardDbContext db, Guid subscriptionId, WebhookDeliveryStatus status, DateTimeOffset at)
    {
        var attempt = new WebhookDeliveryAttempt
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscriptionId,
            EventId = Ulid.NewUlid().ToString(),
            EventType = WebhookEventTypes.CardMoved,
            BoardId = Guid.NewGuid(),
            Attempt = 1,
            Status = status,
            AttemptedAtUtc = at,
        };

        db.WebhookDeliveryAttempts.Add(attempt);
    }
}
