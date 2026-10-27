using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// Opens the gap between a request's existence (or emptiness) check and its write, deterministically
// and inside the real request. When an armed request finishes reading a table it checks, a rival
// change commits before the request goes on: the card or lane it just found is deleted, or a card or
// lane lands where it just found none. The rival runs between statements, outside any transaction the
// request holds, so it commits exactly as a concurrent request would.
public class ConcurrentDeleteRaceInterceptor(IServiceScopeFactory scopeFactory) : DbCommandInterceptor
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory
        ?? throw new ArgumentNullException(nameof(scopeFactory));

    private string? _afterReadOf;
    private Func<BoardDbContext, Task>? _rival;
    private int _firedCount;
    private int _fireOnRead;
    private int _matchingReads;
    private bool _injectingRival;
    private ConcurrentQueue<string> _commands = new();

    public int FiredCount => Volatile.Read(ref _firedCount);

    // How many of the request's own commands since arming contained the fragment; the rival's are not
    // counted. Lets a test see how many times a save was attempted, not only how it ended.
    public int CommandsContaining(string fragment) =>
        _commands.Count(c => c.Contains(fragment, StringComparison.Ordinal));

    // Armed per test for one rival: the fixture is shared across the class, and leftover arming would
    // let a later test meet a rival it never asked for. The rival fires once, after the first SELECT
    // reading the named table, or after a later one when the request reads that table more than once
    // before the check the test is aimed at.
    public void Arm(string afterReadOf, Func<BoardDbContext, Task> rival, int onRead = 1)
    {
        _afterReadOf = $"FROM \"{afterReadOf}\"";
        _rival = rival;
        _fireOnRead = onRead;
        Volatile.Write(ref _matchingReads, 0);
        Volatile.Write(ref _firedCount, 0);
        _injectingRival = false;
        _commands = new();
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync
    (
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default
    )
    {
        Record(command);

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync
    (
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        Record(command);

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult> DataReaderClosingAsync
    (
        DbCommand command,
        DataReaderClosingEventData eventData,
        InterceptionResult result
    )
    {
        if (ShouldFire(command) && Interlocked.Increment(ref _matchingReads) == _fireOnRead && _rival is Func<BoardDbContext, Task> rival)
        {
            _rival = null;
            Interlocked.Increment(ref _firedCount);

            await CommitRivalAsync(rival);
        }

        return await base.DataReaderClosingAsync(command, eventData, result);
    }

    private void Record(DbCommand command)
    {
        if (!_injectingRival)
        {
            _commands.Enqueue(command.CommandText);
        }
    }

    // The rival's own statements pass through this same interceptor; the injecting guard keeps them
    // from firing it.
    private bool ShouldFire(DbCommand command) =>
        !_injectingRival
            && _afterReadOf is string table
            && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
            && command.CommandText.Contains(table, StringComparison.Ordinal);

    private async Task CommitRivalAsync(Func<BoardDbContext, Task> rival)
    {
        _injectingRival = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();

            await rival(scope.ServiceProvider.GetRequiredService<BoardDbContext>());
        }
        finally
        {
            _injectingRival = false;
        }
    }
}

// Adds the concurrent-delete race interceptor to the standard harness and changes nothing else.
public class ConcurrentDeleteRaceFactory : CollatticeApiFactory
{
    public ConcurrentDeleteRaceInterceptor Interceptor => Services.GetRequiredService<ConcurrentDeleteRaceInterceptor>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services => services.AddSingleton<ConcurrentDeleteRaceInterceptor>());
    }

    protected override void ConfigureDbContext(IServiceProvider serviceProvider, DbContextOptionsBuilder options) =>
        options.AddInterceptors(serviceProvider.GetRequiredService<ConcurrentDeleteRaceInterceptor>());
}
