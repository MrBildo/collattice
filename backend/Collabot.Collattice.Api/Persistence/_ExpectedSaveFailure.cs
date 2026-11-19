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
// unchanged otherwise. Any other failure is written at the level EF gave it, and so is everything
// outside a declaration. Deciding needs the exception, which only the second entry carries, so the
// first is held until the second arrives and is written with it. Entries at Warning or above keep
// their order around a held one; a Debug entry EF writes in between (a transaction's rollback) can
// land before it.
//
// Some failures are answered far from the save that failed: a write that loses to a concurrent
// delete is answered once for every write path, by the REST middleware and the MCP tool filter. A
// predicate there cannot tell, when EF writes its entries, whether the failure will reach that
// answer or be caught on the way by code that answers it some other way (a batch tool reports any
// failed save in its own error). So such a site declares with ExpectWhenAnswered and names each
// failure it does answer. Both entries of a failed save its predicate matches are held until the
// declaration ends, and then written at Debug if the failure was answered there and at their own
// level if it was not. Held entries are written after any entry logged in between.
//
// A declaration is an AsyncLocal, so work started inside one carries it after the declaration ends.
// Nothing here starts such work, and an ended declaration passes every entry straight through, so
// work that outlives it is logged as if nothing were declared.
internal static class ExpectedSaveFailure
{
    private static readonly AsyncLocal<Expectation?> _current = new();

    // Lets the logger skip building a deferred write on the common path, where nothing is declared.
    public static bool IsDeclared => _current.Value is not null;

    public static IDisposable Expect(Func<DbUpdateException, bool> isExpected) => Declare(isExpected, awaitsAnswer: false);

    public static IAnsweredSaveFailures ExpectWhenAnswered(Func<DbUpdateException, bool> isAnswerable) => Declare(isAnswerable, awaitsAnswer: true);

    private static Expectation Declare(Func<DbUpdateException, bool> isExpected, bool awaitsAnswer)
    {
        var expectation = new Expectation(isExpected, awaitsAnswer, _current.Value);
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

    private enum Verdict
    {
        Unexpected,
        Expected,
        AwaitingAnswer,
    }

    // sealed: Sonar S3260 asks for it on a private class nothing derives from.
    private sealed class Expectation
    (
        Func<DbUpdateException, bool> isExpected,
        bool awaitsAnswer,
        Expectation? outer
    ) : IAnsweredSaveFailures
    {
        private readonly Func<DbUpdateException, bool> _isExpected = isExpected
            ?? throw new ArgumentNullException(nameof(isExpected));
        private readonly bool _awaitsAnswer = awaitsAnswer;
        private readonly Expectation? _outer = outer;

        // Only a declaration that awaits an answer holds failures and records answers. The lock is
        // for work running in parallel under one declaration; nothing here does that today.
        private readonly Lock _gate = new();
        private readonly List<HeldFailure> _awaitingAnswer = [];
        private readonly HashSet<Exception> _answered = new(ReferenceEqualityComparer.Instance);

        private (LogLevel Level, Action<LogLevel> Write)? _heldCommandError;
        private bool _isEnded;

        public void Route(EventId eventId, LogLevel level, Exception? exception, Action<LogLevel> write)
        {
            if (_isEnded)
            {
                write(level);
                return;
            }

            if (eventId.Id == RelationalEventId.CommandError.Id)
            {
                ReleaseHeldCommandError(asExpected: false);
                _heldCommandError = (level, write);

                return;
            }

            if (eventId.Id == CoreEventId.SaveChangesFailed.Id && exception is DbUpdateException failure)
            {
                var (verdict, holder) = Decide(failure);

                if (verdict == Verdict.AwaitingAnswer)
                {
                    holder!.Hold(failure, TakeHeldCommandError(), (level, write));
                    return;
                }

                var isExpectedFailure = verdict == Verdict.Expected;

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

        public void Answered(Exception failure)
        {
            lock (_gate)
            {
                _answered.Add(failure);
            }
        }

        // A command error with no save failure after it (a failed query, say) is written at its own
        // level when the declaration ends, and so is a held failure nobody answered, so nothing
        // held is ever lost.
        public void Dispose()
        {
            ReleaseHeldCommandError(asExpected: false);

            List<(HeldFailure Failure, bool WasAnswered)> held;
            lock (_gate)
            {
                _isEnded = true;
                held = [.. _awaitingAnswer.Select(failure => (failure, _answered.Contains(failure.Failure)))];

                _awaitingAnswer.Clear();
            }

            foreach (var (failure, wasAnswered) in held)
            {
                foreach (var (level, write) in failure.Entries)
                {
                    write(wasAnswered ? LogLevel.Debug : level);
                }
            }

            _current.Value = _outer;
        }

        // A declaration made inside another adds to it; the outer site's expectation still holds.
        // The innermost declaration that matches decides, so a site's own catch is preferred over
        // an answer further out.
        private (Verdict Verdict, Expectation? Holder) Decide(DbUpdateException failure)
        {
            if (_isExpected(failure) && !(_awaitsAnswer && _isEnded))
            {
                return _awaitsAnswer ? (Verdict.AwaitingAnswer, this) : (Verdict.Expected, null);
            }

            return _outer?.Decide(failure) ?? (Verdict.Unexpected, null);
        }

        private void Hold
        (
            DbUpdateException failure,
            (LogLevel Level, Action<LogLevel> Write)? commandError,
            (LogLevel Level, Action<LogLevel> Write) saveFailure
        )
        {
            List<(LogLevel Level, Action<LogLevel> Write)> entries = commandError is { } error
                ? [error, saveFailure]
                : [saveFailure];

            lock (_gate)
            {
                if (!_isEnded)
                {
                    _awaitingAnswer.Add(new HeldFailure(failure, entries));
                    return;
                }
            }

            // Ended in between: there is no answer to wait for, so the entries go out as EF wrote them.
            foreach (var (level, write) in entries)
            {
                write(level);
            }
        }

        private (LogLevel Level, Action<LogLevel> Write)? TakeHeldCommandError()
        {
            var held = _heldCommandError;
            _heldCommandError = null;

            return held;
        }

        private void ReleaseHeldCommandError(bool asExpected)
        {
            if (TakeHeldCommandError() is not { } held)
            {
                return;
            }

            held.Write(asExpected ? LogLevel.Debug : held.Level);
        }
    }

    private sealed record HeldFailure
    (
        DbUpdateException Failure,
        IReadOnlyList<(LogLevel Level, Action<LogLevel> Write)> Entries
    );
}

// A declaration whose matching failures wait for the site that answers them. Answered names the
// failure that site caught, by reference; a held failure not named by the time the declaration
// ends is written at its own level.
internal interface IAnsweredSaveFailures : IDisposable
{
    void Answered(Exception failure);
}

// The logger factory EF Core is given. While a save failure is declared expected, every entry passes
// through ExpectedSaveFailure; otherwise it goes straight to the app's logger.
internal class ExpectedSaveFailureLoggerFactory(ILoggerFactory inner) : ILoggerFactory
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

// sealed: Sonar S3260 asks for it on a file-local class nothing derives from.
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
