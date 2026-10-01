using System.Globalization;
using Collabot.Collattice.Api.Endpoints;
using Collabot.Collattice.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// Forces description-revision collisions from inside a real request.
//
// The collision lives in the gap between a write path reading the trail's head and the save that
// inserts the row it allocated. Two genuinely parallel requests cannot open that gap on demand, and
// the harness's single shared connection makes racing them fail on connection contention instead.
// This opens it deterministically: when an armed card's save is about to run, a rival edit commits
// first on its own scope, so the request that follows finds its revision taken. Arming for more than
// one collision injects a fresh rival on each of the request's retries, which is how the retry loop
// is exercised past a single attempt — and, armed for the whole attempt budget, driven to exhaustion.
//
// What that buys is coverage of the wiring rather than the mechanism. A test that calls the history
// helper directly proves the retry works; it says nothing about whether the endpoints still reach
// it, so the retry could be deleted from an entry point with every test still green. Reaching the
// collision through the entry point is what makes that deletion visible.
public class RevisionRaceInterceptor(IServiceScopeFactory scopeFactory) : SaveChangesInterceptor
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory
        ?? throw new ArgumentNullException(nameof(scopeFactory));

    private Guid _armedCardId;
    private Guid _rivalUserId;
    private int _remainingFires;
    private int _firedCount;
    private bool _injectingRival;

    public int FiredCount => Volatile.Read(ref _firedCount);

    public bool HasFired => FiredCount > 0;

    // Armed per test rather than per factory: the fixture is shared across the class, and leftover
    // arming would let a later test pass without ever meeting a collision. `collisions` is how many
    // rival edits to inject before letting the request through — one for the wiring tests, the retry
    // budget or one past it for the tests that drive the loop to its last attempt and to exhaustion.
    public void Arm(Guid cardId, Guid rivalUserId, int collisions = 1)
    {
        _armedCardId = cardId;
        _rivalUserId = rivalUserId;
        _remainingFires = collisions;
        Volatile.Write(ref _firedCount, 0);
        _injectingRival = false;
    }

    public void Disarm()
    {
        _armedCardId = Guid.Empty;
        _remainingFires = 0;
        Volatile.Write(ref _firedCount, 0);
        _injectingRival = false;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync
    (
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        if (ShouldFire(eventData.Context))
        {
            await CommitRivalEditAsync(cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private bool ShouldFire(DbContext? context)
    {
        // The rival commits through this same interceptor; without the injecting guard its own save,
        // which also stages a revision on the armed card, would arm a collision against itself.
        if (context is null || _armedCardId == Guid.Empty || _injectingRival || _remainingFires <= 0)
        {
            return false;
        }

        var stagesARevision = context.ChangeTracker
            .Entries<CardFieldHistory>()
                .Any(e => e.State == EntityState.Added && e.Entity.CardId == _armedCardId);

        if (!stagesARevision)
        {
            return false;
        }

        _remainingFires--;
        Interlocked.Increment(ref _firedCount);

        return true;
    }

    private async Task CommitRivalEditAsync(CancellationToken ct)
    {
        _injectingRival = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

            // Read the committed description, not the one the intercepted request is holding: the
            // rival is a separate editor who never saw it. A distinct value per fire keeps each
            // injected edit a real change rather than a no-op the helper would decline to record.
            var card = await db.Cards.FirstAsync(c => c.Id == _armedCardId, ct);
            var oldValue = card.DescriptionMarkdown;
            var rivalValue = $"rival edit {FiredCount.ToString(CultureInfo.InvariantCulture)}";

            card.DescriptionMarkdown = rivalValue;
            card.LastUpdatedByUserId = _rivalUserId;
            card.LastUpdatedAtUtc = DateTimeOffset.UtcNow;

            var change = await CardHistoryHelper.StageDescriptionChangeAsync
            (
                db,
                _armedCardId,
                oldValue,
                rivalValue,
                _rivalUserId,
                ct
            );

            // The rival commits uncontended, so it cannot run out of attempts; failing loudly here
            // keeps a broken rival from passing as a collision the request under test never met.
            if (!await CardHistoryHelper.TrySaveWithRevisionRetryAsync(db, change, ct))
            {
                throw new InvalidOperationException("The injected rival edit failed to commit.");
            }
        }
        finally
        {
            _injectingRival = false;
        }
    }
}
