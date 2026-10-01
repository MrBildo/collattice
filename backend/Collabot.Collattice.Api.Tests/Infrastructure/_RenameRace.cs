using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// Which unique value the injected rival takes from the size, label or lane being updated.
public enum RenameRivalCollision
{
    SizeName,
    SizeOrdinal,
    LabelName,
    LanePosition,
}

// Forces a size, label or lane update to collide from inside the real request, the update counterpart
// of the create race interceptors. When an armed board's size, label or lane is about to save a changed
// name, ordinal or position, a rival commits that same value first, so the update meets the unique
// index exactly as it would behind a concurrent create, rename or move. A transient rival is removed
// again as soon as the update's save fails, which is the rare case of a value that moved on before the
// update could re-read it.
public class RenameRaceInterceptor(IServiceScopeFactory scopeFactory) : SaveChangesInterceptor
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory
        ?? throw new ArgumentNullException(nameof(scopeFactory));

    private Guid _armedBoardId;
    private RenameRivalCollision _collision;
    private bool _transient;
    private int _firedCount;
    private bool _injectingRival;
    private object? _rival;

    public int FiredCount => Volatile.Read(ref _firedCount);

    // Armed per test, for one collision: the fixture is shared across the class, and leftover arming
    // would let a later test meet a collision it never asked for.
    public void Arm(Guid boardId, RenameRivalCollision collision, bool transient = false)
    {
        _armedBoardId = boardId;
        _collision = collision;
        _transient = transient;
        Volatile.Write(ref _firedCount, 0);
        _injectingRival = false;
        _rival = null;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync
    (
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        if (StagedRival(eventData.Context) is object rival)
        {
            await WriteRivalAsync(rival, remove: false, cancellationToken);
            _rival = rival;
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async Task SaveChangesFailedAsync
    (
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        if (_transient && !_injectingRival && _rival is object rival)
        {
            _rival = null;
            await WriteRivalAsync(rival, remove: true, cancellationToken);
        }

        await base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    // Builds the rival from the value the update is about to save; null when nothing armed is staged.
    // The rival saves through this same interceptor, and the injecting guard keeps its own write from
    // arming a collision against itself.
    private object? StagedRival(DbContext? context)
    {
        if (context is null || _armedBoardId == Guid.Empty || _injectingRival || FiredCount > 0)
        {
            return null;
        }

        object? rival = _collision switch
        {
            RenameRivalCollision.SizeName => StagedSize(context) is CardSize size
                ? new CardSize { Id = Guid.NewGuid(), BoardId = _armedBoardId, Name = size.Name, Ordinal = 1_000 }
                : null,
            RenameRivalCollision.SizeOrdinal => StagedSize(context) is CardSize size
                ? new CardSize { Id = Guid.NewGuid(), BoardId = _armedBoardId, Name = "Rival", Ordinal = size.Ordinal }
                : null,
            RenameRivalCollision.LabelName => StagedLabel(context) is Label label
                ? new Label { Id = Guid.NewGuid(), BoardId = _armedBoardId, Name = label.Name }
                : null,
            _ => StagedLane(context) is Lane lane
                ? new Lane { Id = Guid.NewGuid(), BoardId = _armedBoardId, Name = "Rival", Position = lane.Position }
                : null,
        };

        if (rival is not null)
        {
            Interlocked.Increment(ref _firedCount);
        }

        return rival;
    }

    private CardSize? StagedSize(DbContext context) =>
        context.ChangeTracker
            .Entries<CardSize>()
            .Where(e => e.State == EntityState.Modified && e.Entity.BoardId == _armedBoardId)
                .Select(e => e.Entity)
                    .FirstOrDefault();

    private Label? StagedLabel(DbContext context) =>
        context.ChangeTracker
            .Entries<Label>()
            .Where(e => e.State == EntityState.Modified && e.Entity.BoardId == _armedBoardId)
                .Select(e => e.Entity)
                    .FirstOrDefault();

    private Lane? StagedLane(DbContext context) =>
        context.ChangeTracker
            .Entries<Lane>()
            .Where(e => e.State == EntityState.Modified && e.Entity.BoardId == _armedBoardId)
                .Select(e => e.Entity)
                    .FirstOrDefault();

    private async Task WriteRivalAsync(object rival, bool remove, CancellationToken ct)
    {
        _injectingRival = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

            if (remove)
            {
                db.Remove(rival);
            }
            else
            {
                db.Add(rival);
            }

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _injectingRival = false;
        }
    }
}

// Adds the rename race interceptor to the standard harness and changes nothing else; it commits rival
// sizes, labels and lanes mid-save, which no other test has reason to pay for.
public class RenameRaceFactory : CollatticeApiFactory
{
    public RenameRaceInterceptor Interceptor => Services.GetRequiredService<RenameRaceInterceptor>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services => services.AddSingleton<RenameRaceInterceptor>());
    }

    protected override void ConfigureDbContext(IServiceProvider serviceProvider, DbContextOptionsBuilder options) =>
        options.AddInterceptors(serviceProvider.GetRequiredService<RenameRaceInterceptor>());
}
