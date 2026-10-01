using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// Forces card-number collisions from inside a real request, the card-number counterpart of the
// revision-race interceptor.
//
// A card number is the board's highest plus one, read before the save that claims it. When a save on
// the armed board is about to claim a number, a rival card commits that same number first on its own
// scope, so the request's save meets the unique index and its retry has to read again. Armed for more
// than one collision, a fresh rival takes each number the retry goes on to claim, which is how the
// loop is driven to its last attempt and, armed for the whole budget, to exhaustion.
//
// A save claims a number in two ways: inserting a numbered card (create and duplicate) and changing a
// card's number (finalizing a draft). A draft is inserted at number zero and claims nothing, so
// creating one while armed does not spend a collision.
public class CardNumberRaceInterceptor(IServiceScopeFactory scopeFactory) : SaveChangesInterceptor
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory
        ?? throw new ArgumentNullException(nameof(scopeFactory));

    private Guid _armedBoardId;
    private int _remainingFires;
    private int _firedCount;
    private bool _injectingRival;

    public int FiredCount => Volatile.Read(ref _firedCount);

    // Armed per test rather than per factory: the fixture is shared across the class, and leftover
    // arming would let a later test meet collisions it never asked for.
    public void Arm(Guid boardId, int collisions)
    {
        _armedBoardId = boardId;
        _remainingFires = collisions;
        Volatile.Write(ref _firedCount, 0);
        _injectingRival = false;
    }

    public void Disarm()
    {
        _armedBoardId = Guid.Empty;
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
        var claim = FindNumberClaim(eventData.Context);

        if (claim is not null)
        {
            await CommitRivalAsync(claim, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private CardItem? FindNumberClaim(DbContext? context)
    {
        // The rival commits through this same interceptor, and its own insert claims a number on the
        // armed board; without the injecting guard it would arm a collision against itself.
        if (context is null || _armedBoardId == Guid.Empty || _injectingRival || _remainingFires <= 0)
        {
            return null;
        }

        var claim = context.ChangeTracker
            .Entries<CardItem>()
                .FirstOrDefault(e => e.Entity.BoardId == _armedBoardId && e.Entity.Number > 0 && ClaimsNumber(e));

        if (claim is null)
        {
            return null;
        }

        _remainingFires--;
        Interlocked.Increment(ref _firedCount);

        return claim.Entity;
    }

    private static bool ClaimsNumber(EntityEntry<CardItem> entry) =>
        entry.State == EntityState.Added
        || (entry.State == EntityState.Modified && entry.Property(c => c.Number).IsModified);

    private async Task CommitRivalAsync(CardItem claimed, CancellationToken ct)
    {
        _injectingRival = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
            var now = DateTimeOffset.UtcNow;

            db.Cards.Add(new CardItem
            {
                Id = Guid.NewGuid(),
                Number = claimed.Number,
                BoardId = claimed.BoardId,
                Name = $"rival card {claimed.Number.ToString(CultureInfo.InvariantCulture)}",
                SizeId = claimed.SizeId,
                LaneId = claimed.LaneId,
                Position = claimed.Position,
                CreatedByUserId = claimed.CreatedByUserId,
                CreatedAtUtc = now,
                LastUpdatedByUserId = claimed.CreatedByUserId,
                LastUpdatedAtUtc = now
            });

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _injectingRival = false;
        }
    }
}
