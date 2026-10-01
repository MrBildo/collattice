using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// Runs a rival request against the same draft from inside a real finalize or cancel, at the moment that
// request is about to save. Both requests found the card still a draft before either saved, which is
// what a double-click or a client retrying a slow request does. The rival is a whole request through
// the API, so it announces whatever it changes, and a test can count the announcements.
public class DraftRaceInterceptor : SaveChangesInterceptor
{
    private Guid _armedCardId;
    private Func<Task>? _rival;
    private int _firedCount;
    private bool _runningRival;

    public int FiredCount => Volatile.Read(ref _firedCount);

    // Armed per test, for one rival: the fixture is shared across the class, and leftover arming would
    // let a later test meet a rival it never asked for.
    public void Arm(Guid cardId, Func<Task> rival)
    {
        _armedCardId = cardId;
        _rival = rival;
        Volatile.Write(ref _firedCount, 0);
        _runningRival = false;
    }

    // Leaves FiredCount as it was, so a test can disarm in a finally and still assert on it.
    public void Disarm()
    {
        _armedCardId = Guid.Empty;
        _rival = null;
        _runningRival = false;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync
    (
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        if (SavesTheArmedDraft(eventData.Context) && _rival is { } rival)
        {
            Interlocked.Increment(ref _firedCount);

            // The rival saves through this same interceptor; the guard keeps it from starting a rival of
            // its own.
            _runningRival = true;
            try
            {
                await rival();
            }
            finally
            {
                _runningRival = false;
            }
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private bool SavesTheArmedDraft(DbContext? context)
    {
        if (context is null || _armedCardId == Guid.Empty || _runningRival || FiredCount > 0)
        {
            return false;
        }

        return context.ChangeTracker
            .Entries<CardItem>()
                .Any(e => e.Entity.Id == _armedCardId && e.State is EntityState.Modified or EntityState.Deleted);
    }
}

// Adds the draft-race interceptor to the standard harness and captures webhook events, so a test can
// count how many times a card was announced.
public class DraftRaceFactory : CollatticeApiFactory
{
    public DraftRaceInterceptor Interceptor => Services.GetRequiredService<DraftRaceInterceptor>();

    public CapturingWebhookSink Sink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<DraftRaceInterceptor>();

            var sink = services.SingleOrDefault(d => d.ServiceType == typeof(IWebhookSink));
            if (sink is not null)
            {
                services.Remove(sink);
            }

            services.AddSingleton<IWebhookSink>(Sink);
        });
    }

    protected override void ConfigureDbContext(IServiceProvider serviceProvider, DbContextOptionsBuilder options) =>
        options.AddInterceptors(serviceProvider.GetRequiredService<DraftRaceInterceptor>());
}
