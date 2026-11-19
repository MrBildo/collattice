using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// The webhook delivery harness (running dispatcher, real queue, real sender against a capture stub,
// real delivery log) with two database faults a test can arm: the dispatcher's read of the
// subscription registry failing, and a delivery-attempt row failing to save. Both are injected as
// commands at the database, which is where they fail in production; nothing in the dispatcher is
// replaced. It also keeps every log entry, so a test can see how a failure was reported.
public class WebhookDispatchFaultFactory : WebhookDeliveryFactory
{
    private readonly LogCapture _log = new();

    public DispatchFaultInterceptor Faults { get; } = new();

    public IReadOnlyList<CapturedLogEntry> Log => [.. _log.Entries];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(_log);
            logging.AddFilter<LogCapture>(null, LogLevel.Trace);
        });
    }

    protected override void ConfigureDbContext(IServiceProvider serviceProvider, DbContextOptionsBuilder options) =>
        options.AddInterceptors(Faults);

    // sealed: a leaf test provider; no subtype hierarchy is intended.
    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose()
        {
        }
    }

    // sealed: a leaf test logger; no subtype hierarchy is intended.
    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        private readonly string _category = category;
        private readonly ConcurrentQueue<CapturedLogEntry> _entries = entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>
        (
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) =>
            _entries.Enqueue(new CapturedLogEntry(_category, logLevel, formatter(state, exception), exception));
    }
}

public sealed record CapturedLogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

// Fails the next N commands of each armed kind with SQLite's "database is locked", the error a read
// or write gets when SQLite's busy wait runs out. Disarmed by default, so a host boots normally and a
// test arms it once its own setup is written.
public sealed class DispatchFaultInterceptor : DbCommandInterceptor
{
    private int _subscriptionReadFailures;
    private int _attemptInsertFailures;
    private int _subscriptionReads;

    // Every read of the subscription registry seen since the host started, failed or not.
    public int SubscriptionReads => Volatile.Read(ref _subscriptionReads);

    public void FailSubscriptionReads(int count) => Volatile.Write(ref _subscriptionReadFailures, count);

    public void FailAttemptInserts(int count) => Volatile.Write(ref _attemptInsertFailures, count);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync
    (
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default
    )
    {
        Inspect(command);
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
        Inspect(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Inspect(DbCommand command)
    {
        var text = command.CommandText;

        if (text.Contains("FROM \"WebhookSubscriptions\"", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _subscriptionReads);
            ThrowIfArmed(ref _subscriptionReadFailures);
        }

        if (text.Contains("INSERT INTO \"WebhookDeliveryAttempts\"", StringComparison.Ordinal))
        {
            ThrowIfArmed(ref _attemptInsertFailures);
        }
    }

    private static void ThrowIfArmed(ref int remaining)
    {
        if (Volatile.Read(ref remaining) > 0 && Interlocked.Decrement(ref remaining) >= 0)
        {
            throw new SqliteException("SQLite Error 5: 'database is locked'.", 5);
        }
    }
}
