using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// Which unique index the injected rival collides on.
public enum SizeRivalCollision
{
    Ordinal,
    Name,
}

// Forces size-create collisions from inside a real create, the size counterpart of the lane-position
// race interceptor. When an armed board's size insert is about to save, a rival size commits first,
// taking either the staged ordinal (under another name) or the staged name (under a far-away
// ordinal), so the create meets the unique index exactly as a concurrent create would. Arming for
// more than one collision injects a rival on each retry too, which drives the retry budget to
// exhaustion.
public class SizeCreateRaceInterceptor(IServiceScopeFactory scopeFactory) : SaveChangesInterceptor
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory
        ?? throw new ArgumentNullException(nameof(scopeFactory));

    private Guid _armedBoardId;
    private SizeRivalCollision _collision;
    private int _remainingFires;
    private int _firedCount;
    private bool _injectingRival;

    public int FiredCount => Volatile.Read(ref _firedCount);

    // Armed per test: the fixture is shared across the class, and leftover arming would let a later
    // test meet a collision it never asked for.
    public void Arm(Guid boardId, SizeRivalCollision collision = SizeRivalCollision.Ordinal, int collisions = 1)
    {
        _armedBoardId = boardId;
        _collision = collision;
        _remainingFires = collisions;
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
        if (StagedSize(eventData.Context) is CardSize staged)
        {
            await CommitRivalSizeAsync(staged, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    // The rival saves through this same interceptor; the injecting guard keeps its own insert from
    // arming a collision against itself.
    private CardSize? StagedSize(DbContext? context)
    {
        if (context is null || _armedBoardId == Guid.Empty || _injectingRival || _remainingFires <= 0)
        {
            return null;
        }

        var staged = context.ChangeTracker
            .Entries<CardSize>()
            .Where(e => e.State == EntityState.Added && e.Entity.BoardId == _armedBoardId)
                .Select(e => e.Entity)
                    .FirstOrDefault();

        if (staged is null)
        {
            return null;
        }

        _remainingFires--;
        Interlocked.Increment(ref _firedCount);

        return staged;
    }

    private async Task CommitRivalSizeAsync(CardSize staged, CancellationToken ct)
    {
        _injectingRival = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

            var rivalNumber = FiredCount.ToString(CultureInfo.InvariantCulture);

            // A name rival sits far from any ordinal a test uses, so only the name collides.
            var rival = _collision == SizeRivalCollision.Ordinal
                ? new CardSize { Id = Guid.NewGuid(), BoardId = _armedBoardId, Name = $"Rival {rivalNumber}", Ordinal = staged.Ordinal }
                : new CardSize { Id = Guid.NewGuid(), BoardId = _armedBoardId, Name = staged.Name, Ordinal = 1_000 + FiredCount };

            db.CardSizes.Add(rival);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _injectingRival = false;
        }
    }
}

// Adds the size-create race interceptor to the standard harness and changes nothing else; it commits
// rival sizes mid-save, which no other test has reason to pay for. As with the lane race, the
// interceptor is attached to the DbContext options explicitly.
public class SizeCreateRaceFactory : CollatticeApiFactory
{
    public SizeCreateRaceInterceptor Interceptor => Services.GetRequiredService<SizeCreateRaceInterceptor>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services => services.AddSingleton<SizeCreateRaceInterceptor>());
    }

    protected override void ConfigureDbContext(IServiceProvider serviceProvider, DbContextOptionsBuilder options) =>
        options.AddInterceptors(serviceProvider.GetRequiredService<SizeCreateRaceInterceptor>());
}
