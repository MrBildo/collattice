using System.Globalization;
using Collabot.Collattice.Api.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// Forces lane-position collisions from inside a real create. Two parallel requests cannot open the
// gap between resolving a position and inserting the lane on demand, and the harness's single shared
// connection serialises them anyway. This opens it deterministically: when an armed board's lane
// insert is about to save, a rival lane commits first at the same position, so the create finds its
// position taken. Arming for more than one collision injects a rival on each retry too, which is how
// the retry budget is driven to exhaustion.
public class LanePositionRaceInterceptor(IServiceScopeFactory scopeFactory) : SaveChangesInterceptor
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory
        ?? throw new ArgumentNullException(nameof(scopeFactory));

    private Guid _armedBoardId;
    private int _remainingFires;
    private int _firedCount;
    private bool _injectingRival;

    public int FiredCount => Volatile.Read(ref _firedCount);

    // Armed per test: the fixture is shared across the class, and leftover arming would let a later
    // test meet a collision it never asked for.
    public void Arm(Guid boardId, int collisions = 1)
    {
        _armedBoardId = boardId;
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
        if (StagedLanePosition(eventData.Context) is int position)
        {
            await CommitRivalLaneAsync(position, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    // The rival saves through this same interceptor; the injecting guard keeps its own insert from
    // arming a collision against itself.
    private int? StagedLanePosition(DbContext? context)
    {
        if (context is null || _armedBoardId == Guid.Empty || _injectingRival || _remainingFires <= 0)
        {
            return null;
        }

        var staged = context.ChangeTracker
            .Entries<Lane>()
            .Where(e => e.State == EntityState.Added && e.Entity.BoardId == _armedBoardId)
                .Select(e => (int?)e.Entity.Position)
                    .FirstOrDefault();

        if (staged is null)
        {
            return null;
        }

        _remainingFires--;
        Interlocked.Increment(ref _firedCount);

        return staged;
    }

    private async Task CommitRivalLaneAsync(int position, CancellationToken ct)
    {
        _injectingRival = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

            db.Lanes.Add(new Lane
            {
                Id = Guid.NewGuid(),
                BoardId = _armedBoardId,
                Name = $"Rival {FiredCount.ToString(CultureInfo.InvariantCulture)}",
                Position = position,
            });

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _injectingRival = false;
        }
    }
}

// Adds the lane-position race interceptor to the standard harness and changes nothing else; it
// commits rival lanes mid-save, which no other test has reason to pay for. As with the revision race,
// the interceptor is attached to the DbContext options explicitly.
public class LanePositionRaceFactory : CollatticeApiFactory
{
    public LanePositionRaceInterceptor Interceptor => Services.GetRequiredService<LanePositionRaceInterceptor>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services => services.AddSingleton<LanePositionRaceInterceptor>());
    }

    protected override void ConfigureDbContext(IServiceProvider serviceProvider, DbContextOptionsBuilder options) =>
        options.AddInterceptors(serviceProvider.GetRequiredService<LanePositionRaceInterceptor>());
}
