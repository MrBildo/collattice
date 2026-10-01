using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// A save that loses a race the code expects (a card number, a revision, a position or a name taken
// first) must not be logged as an error, and every other failed save still must. EF Core writes each
// failed save as two Error entries, CommandError then SaveChangesFailed, before any catch runs.
//
// Where those entries go is decided in the app's own DbContext registration, which the standard
// harness replaces with its own, so these run the app's registration over a real database file.
public class ExpectedSaveFailureLoggingTests
{
    private const string _commandError = "Microsoft.EntityFrameworkCore.Database.Command.CommandError";
    private const string _saveChangesFailed = "Microsoft.EntityFrameworkCore.Update.SaveChangesFailed";

    [Fact]
    public async Task ACardCreateThatRecoversFromACollision_ThroughTheApp_LogsNoError()
    {
        // Arrange
        await using var factory = new LoggingFileDatabaseFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Key", CollatticeApiFactory.TestAdminAuthKey);

        var (boardId, laneId) = await FirstBoardAndLaneAsync(factory);
        var interceptor = factory.Services.GetRequiredService<CardNumberRaceInterceptor>();

        interceptor.Arm(boardId, collisions: 1);
        factory.Log.Clear();

        // Act
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync($"/api/v1/boards/{boardId}/cards", new { name = "Recovered", laneId });
        }
        finally
        {
            interceptor.Disarm();
        }

        // Assert — the request met a real collision and recovered, and nothing was logged as an error.
        // EF's two entries for the collision are still written, at Debug.
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        factory.Log.ShouldNotContain(entry => entry.Level >= LogLevel.Error);

        EntryLevels(factory, _commandError).ShouldBe([LogLevel.Debug]);
        EntryLevels(factory, _saveChangesFailed).ShouldBe([LogLevel.Debug]);
    }

    [Fact]
    public async Task SaveChanges_NothingDeclared_IsLoggedAsAnError()
    {
        // Arrange
        await using var factory = new LoggingFileDatabaseFactory();
        _ = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var (boardId, _) = await FirstBoardAndLaneAsync(factory);

        StageDuplicateLabels(db, boardId);
        factory.Log.Clear();

        // Act
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());

        // Assert
        AssertLoggedAsAnError(factory);
    }

    [Fact]
    public async Task SaveChanges_DeclarationDoesNotMatch_IsLoggedAsAnError()
    {
        // Arrange — a declaration is in force, but for some other failure.
        await using var factory = new LoggingFileDatabaseFactory();
        _ = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var (boardId, _) = await FirstBoardAndLaneAsync(factory);

        StageDuplicateLabels(db, boardId);
        factory.Log.Clear();

        // Act
        using (ExpectedSaveFailure.Expect(_ => false))
        {
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Assert
        AssertLoggedAsAnError(factory);
    }

    [Fact]
    public async Task SaveChanges_DeclarationMatches_IsLoggedAtDebug()
    {
        // Arrange — the same failure as the two tests above, now declared expected. This is what
        // lets their Error assertions be read as the declaration's doing rather than the setup's.
        await using var factory = new LoggingFileDatabaseFactory();
        _ = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var (boardId, _) = await FirstBoardAndLaneAsync(factory);

        StageDuplicateLabels(db, boardId);
        factory.Log.Clear();

        // Act
        using (ExpectedSaveFailure.Expect(_ => true))
        {
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Assert
        factory.Log.ShouldNotContain(entry => entry.Level >= LogLevel.Error);

        EntryLevels(factory, _commandError).ShouldBe([LogLevel.Debug]);
        EntryLevels(factory, _saveChangesFailed).ShouldBe([LogLevel.Debug]);
    }

    [Fact]
    public async Task FailedQuery_UnderADeclaration_IsLoggedAsAnErrorWhenTheDeclarationEnds()
    {
        // Arrange — a command that fails with no save failure after it, inside a declaration that
        // would match anything. Its CommandError is held, and only the declaration's end writes it.
        await using var factory = new LoggingFileDatabaseFactory();
        _ = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        factory.Log.Clear();

        // Act
        using (ExpectedSaveFailure.Expect(_ => true))
        {
            await Should.ThrowAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("SELECT * FROM NoSuchTable"));
        }

        // Assert
        EntryLevels(factory, _commandError).ShouldBe([LogLevel.Error]);
    }

    [Fact]
    public async Task FailedQueryThenExpectedSave_UnderOneDeclaration_KeepsTheQueryAtError()
    {
        // Arrange — the query's CommandError is held when the save's own CommandError arrives.
        await using var factory = new LoggingFileDatabaseFactory();
        _ = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var (boardId, _) = await FirstBoardAndLaneAsync(factory);

        StageDuplicateLabels(db, boardId);
        factory.Log.Clear();

        // Act
        using (ExpectedSaveFailure.Expect(_ => true))
        {
            await Should.ThrowAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("SELECT * FROM NoSuchTable"));
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Assert — the query's failure keeps its level; only the declared save failure drops.
        EntryLevels(factory, _commandError).ShouldBe([LogLevel.Error, LogLevel.Debug]);
        EntryLevels(factory, _saveChangesFailed).ShouldBe([LogLevel.Debug]);
    }

    [Fact]
    public async Task SaveChanges_InWorkThatOutlivesItsDeclaration_IsLoggedAsAnError()
    {
        // Arrange — work started inside a declaration carries it, because it is an AsyncLocal. This
        // work waits until the declaration has ended, then fails a save the declaration would match.
        await using var factory = new LoggingFileDatabaseFactory();
        _ = factory.CreateClient();

        var (boardId, _) = await FirstBoardAndLaneAsync(factory);
        using var declarationEnded = new SemaphoreSlim(0);

        factory.Log.Clear();

        // Act
        Task outliving;
        using (ExpectedSaveFailure.Expect(_ => true))
        {
            outliving = Task.Run(async () =>
            {
                await declarationEnded.WaitAsync();

                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

                StageDuplicateLabels(db, boardId);

                await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
            });
        }

        declarationEnded.Release();
        await outliving;

        // Assert
        AssertLoggedAsAnError(factory);
    }

    // Two labels claiming one name on one board, which the board's unique index refuses.
    private static void StageDuplicateLabels(BoardDbContext db, Guid boardId)
    {
        db.Labels.Add(new Label { Id = Guid.NewGuid(), BoardId = boardId, Name = "Contested" });
        db.Labels.Add(new Label { Id = Guid.NewGuid(), BoardId = boardId, Name = "Contested" });
    }

    // Both of EF's entries at Error, the failed command first, and the save failure carrying the
    // exception: what EF writes when nothing intervenes.
    private static void AssertLoggedAsAnError(LoggingFileDatabaseFactory factory)
    {
        var failures = factory.Log
            .Where(entry => entry.EventName is _commandError or _saveChangesFailed)
                .ToList();

        failures.Select(entry => (entry.EventName, entry.Level)).ShouldBe
        (
            [
                (_commandError, LogLevel.Error),
                (_saveChangesFailed, LogLevel.Error),
            ]
        );

        failures[1].Exception.ShouldBeOfType<DbUpdateException>();
    }

    private static List<LogLevel> EntryLevels(LoggingFileDatabaseFactory factory, string eventName) =>
        [.. factory.Log
            .Where(entry => entry.EventName == eventName)
                .Select(entry => entry.Level)];

    private static async Task<(Guid BoardId, Guid LaneId)> FirstBoardAndLaneAsync(LoggingFileDatabaseFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var lane = await db.Lanes
            .Where(l => !l.IsArchiveLane)
            .OrderBy(l => l.Position)
                .FirstAsync();

        return (lane.BoardId, lane.Id);
    }

    // sealed: a leaf test host for this class only; no subtype hierarchy is intended.
    private sealed class LoggingFileDatabaseFactory : WebApplicationFactory<Program>
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"collattice-logging-{Guid.NewGuid():N}.db");
        private readonly CapturingLoggerProvider _provider = new();

        public ConcurrentQueue<CapturedEntry> Log => _provider.Entries;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Board", $"Data Source={_databasePath}");
            builder.UseSetting("Admin:AuthKey", CollatticeApiFactory.TestAdminAuthKey);

            builder.ConfigureLogging(logging =>
            {
                logging.AddProvider(_provider);
                logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
            });

            builder.ConfigureServices(services =>
            {
                // Keep the host off the real GitHub API, as the standard harness does.
                var versionSource = services.Single(d => d.ServiceType == typeof(ILatestVersionSource));
                services.Remove(versionSource);
                services.AddSingleton<ILatestVersionSource, NoNetworkVersionSource>();

                // Added to the app's own registration rather than replacing it, which is the point.
                services.AddSingleton<CardNumberRaceInterceptor>();
                services.ConfigureDbContext<BoardDbContext>((serviceProvider, options) =>
                    options.AddInterceptors(serviceProvider.GetRequiredService<CardNumberRaceInterceptor>()));
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();

            // Close this database's pooled connections first; an open one keeps the file locked on
            // Windows.
            await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
            {
                SqliteConnection.ClearPool(connection);
            }

            foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // A temp file still held open is left for the OS to clean up; it is never read again.
                }
            }
        }
    }

    private sealed record CapturedEntry(string Category, LogLevel Level, string? EventName, Exception? Exception);

    // sealed: a leaf test provider; no subtype hierarchy is intended.
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<CapturedEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose()
        {
        }
    }

    // sealed: a leaf test logger; no subtype hierarchy is intended.
    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedEntry> entries) : ILogger
    {
        private readonly string _category = category;
        private readonly ConcurrentQueue<CapturedEntry> _entries = entries;

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
            _entries.Enqueue(new CapturedEntry(_category, logLevel, eventId.Name, exception));
    }
}
