using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// Forces the card-label race from inside a real add or remove, the card-label counterpart of the
// lane-position and size-create race interceptors. When an armed card's label assignment is about to
// save, a rival commits the same write first: the same label added to the card, or the same
// assignment deleted. The request then meets the duplicate key, or finds no row to delete, exactly as
// the second of two simultaneous calls does.
public class CardLabelRaceInterceptor(IServiceScopeFactory scopeFactory) : SaveChangesInterceptor
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory
        ?? throw new ArgumentNullException(nameof(scopeFactory));

    private Guid _armedCardId;
    private int _firedCount;
    private bool _injectingRival;

    public int FiredCount => Volatile.Read(ref _firedCount);

    // Armed per test, for one collision: the fixture is shared across the class, and leftover arming
    // would let a later test meet a collision it never asked for.
    public void Arm(Guid cardId)
    {
        _armedCardId = cardId;
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
        if (StagedAssignment(eventData.Context) is (CardLabel staged, EntityState state))
        {
            await CommitRivalAsync(staged, state, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    // The rival saves through this same interceptor; the injecting guard keeps its own write from
    // arming a collision against itself.
    private (CardLabel Staged, EntityState State)? StagedAssignment(DbContext? context)
    {
        if (context is null || _armedCardId == Guid.Empty || _injectingRival || FiredCount > 0)
        {
            return null;
        }

        var staged = context.ChangeTracker
            .Entries<CardLabel>()
            .Where(e => e.Entity.CardId == _armedCardId && (e.State == EntityState.Added || e.State == EntityState.Deleted))
                .Select(e => new { e.Entity, e.State })
                    .FirstOrDefault();

        if (staged is null)
        {
            return null;
        }

        Interlocked.Increment(ref _firedCount);

        return (staged.Entity, staged.State);
    }

    private async Task CommitRivalAsync(CardLabel staged, EntityState state, CancellationToken ct)
    {
        _injectingRival = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

            var rival = new CardLabel { CardId = staged.CardId, LabelId = staged.LabelId };
            if (state == EntityState.Added)
            {
                db.CardLabels.Add(rival);
            }
            else
            {
                db.CardLabels.Remove(rival);
            }

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _injectingRival = false;
        }
    }
}

// Adds the card-label race interceptor to the standard harness, and captures webhook events so a test
// can see that the request which lost announced nothing. The rival writes straight to the database, so
// it raises no event of its own.
public class CardLabelRaceFactory : CollatticeApiFactory
{
    public CardLabelRaceInterceptor Interceptor => Services.GetRequiredService<CardLabelRaceInterceptor>();

    public CapturingWebhookSink Sink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<CardLabelRaceInterceptor>();

            var sink = services.SingleOrDefault(d => d.ServiceType == typeof(IWebhookSink));
            if (sink is not null)
            {
                services.Remove(sink);
            }

            services.AddSingleton<IWebhookSink>(Sink);
        });
    }

    protected override void ConfigureDbContext(IServiceProvider serviceProvider, DbContextOptionsBuilder options) =>
        options.AddInterceptors(serviceProvider.GetRequiredService<CardLabelRaceInterceptor>());
}
