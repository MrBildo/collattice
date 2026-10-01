using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Collabot.Collattice.Api.Persistence;

// A save that loses a race on a unique index is an expected outcome wherever the code catches it:
// the allocators retry it, and the update helpers answer it as a taken value. But EF Core logs every
// failed save at Error before any catch runs, as two entries: CommandError under
// Microsoft.EntityFrameworkCore.Database.Command (the failed SQL, with no exception attached) and then
// SaveChangesFailed under Microsoft.EntityFrameworkCore.Update (the exception and its stack trace).
// Measured under the forced-collision harnesses, each collision a request recovered from wrote both,
// so one card create that used all eight attempts left sixteen errors in the log and still answered
// correctly. In production logs that reads as a fault.
//
// A site that catches a failure declares it around the save by passing Expect the same predicate its
// catch filter uses, and holds the returned declaration in a using. While the declaration is in
// force, a failed save the predicate matches has both of EF's entries written at Debug instead,
// unchanged otherwise. Any other failure is written exactly as EF wrote it,
// at its own level and in its own order, and so is everything outside a declaration. Deciding needs
// the exception, which only the second entry carries, so the first is held until the second arrives
// and is written with it.
internal static class ExpectedSaveFailure
{
    private static readonly AsyncLocal<Expectation?> _current = new();

    // Lets the logger skip building a deferred write on the common path, where nothing is declared.
    public static bool IsDeclared => _current.Value is not null;

    public static IDisposable Expect(Func<DbUpdateException, bool> isExpected)
    {
        var expectation = new Expectation(isExpected, _current.Value);
        _current.Value = expectation;

        return expectation;
    }

    public static void Write(EventId eventId, LogLevel level, Exception? exception, Action<LogLevel> write)
    {
        if (_current.Value is { } expectation)
        {
            expectation.Route(eventId, level, exception, write);
            return;
        }

        write(level);
    }

    // sealed: a private leaf; nothing derives from it.
    private sealed class Expectation(Func<DbUpdateException, bool> isExpected, Expectation? outer) : IDisposable
    {
        private readonly Func<DbUpdateException, bool> _isExpected = isExpected
            ?? throw new ArgumentNullException(nameof(isExpected));
        private readonly Expectation? _outer = outer;

        private (LogLevel Level, Action<LogLevel> Write)? _heldCommandError;

        public void Route(EventId eventId, LogLevel level, Exception? exception, Action<LogLevel> write)
        {
            if (eventId.Id == RelationalEventId.CommandError.Id)
            {
                ReleaseHeldCommandError(asExpected: false);
                _heldCommandError = (level, write);

                return;
            }

            if (eventId.Id == CoreEventId.SaveChangesFailed.Id && exception is DbUpdateException failure)
            {
                var isExpectedFailure = IsExpected(failure);

                ReleaseHeldCommandError(isExpectedFailure);

                write(isExpectedFailure ? LogLevel.Debug : level);

                return;
            }

            // EF writes its transaction rollback at Debug between the two entries of a failed save
            // that ran in a transaction, so a lower-level entry must not end the hold. One at Warning
            // or above does, and goes after the held error, so entries that matter keep their order.
            if (level >= LogLevel.Warning)
            {
                ReleaseHeldCommandError(asExpected: false);
            }

            write(level);
        }

        // A command error with no save failure after it (a failed query, say) is written at its own
        // level when the declaration ends, so nothing held is ever lost.
        public void Dispose()
        {
            ReleaseHeldCommandError(asExpected: false);

            _current.Value = _outer;
        }

        // A declaration made inside another adds to it; the outer site's expectation still holds.
        private bool IsExpected(DbUpdateException failure) =>
            _isExpected(failure) || (_outer?.IsExpected(failure) ?? false);

        private void ReleaseHeldCommandError(bool asExpected)
        {
            if (_heldCommandError is not { } held)
            {
                return;
            }

            _heldCommandError = null;

            held.Write(asExpected ? LogLevel.Debug : held.Level);
        }
    }
}

// The logger factory EF Core is given. While a save failure is declared expected, every entry passes
// through ExpectedSaveFailure; otherwise it goes straight to the app's logger.
//
// sealed: a leaf decorator over the app's logger factory; no subtype hierarchy is intended.
internal sealed class ExpectedSaveFailureLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    private readonly ILoggerFactory _inner = inner
        ?? throw new ArgumentNullException(nameof(inner));

    public ILogger CreateLogger(string categoryName) => new ExpectedSaveFailureLogger(_inner.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);

    // The app owns the inner factory and disposes it.
    public void Dispose()
    {
    }
}

// sealed: a leaf decorator; no subtype hierarchy is intended.
file sealed class ExpectedSaveFailureLogger(ILogger inner) : ILogger
{
    private readonly ILogger _inner = inner
        ?? throw new ArgumentNullException(nameof(inner));

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>
    (
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        if (!ExpectedSaveFailure.IsDeclared)
        {
            _inner.Log(logLevel, eventId, state, exception, formatter);
            return;
        }

        ExpectedSaveFailure.Write
        (
            eventId,
            logLevel,
            exception,
            level => _inner.Log(level, eventId, state, exception, formatter)
        );
    }
}
