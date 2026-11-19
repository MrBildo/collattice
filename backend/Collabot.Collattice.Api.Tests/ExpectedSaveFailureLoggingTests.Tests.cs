using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
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

    private const string _commandCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    private static readonly string _conflictCategory = typeof(ConcurrentDeleteConflict).FullName!;

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

    [Fact]
    public async Task ACommentThatLosesToADelete_OverRest_IsAnsweredWithAWarningAndNoError()
    {
        // Arrange — the card is deleted after the handler checks it and before the comment saves.
        // The route's draft check reads the card first, so the delete waits for the second read.
        await using var factory = new LoggingFileDatabaseFactory();
        var client = AdminClient(factory);
        var cardId = await CreateCardAsync(factory, client);

        factory.Race.Arm("Cards", DeleteCard(cardId), onRead: 2);
        factory.Log.Clear();

        // Act
        var response = await client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Late" });

        // Assert
        (factory.Race.FiredCount, response.StatusCode).ShouldBe((1, HttpStatusCode.Conflict));

        AssertAnsweredAsAConflict(factory);
    }

    [Fact]
    public async Task ACommentThatLosesToADelete_OverMcp_IsAnsweredWithAWarningAndNoError()
    {
        // Arrange — the same race through the real /mcp transport, so the tool runs inside the
        // server's own filter and request flow
        await using var factory = new LoggingFileDatabaseFactory();
        var cardId = await CreateCardAsync(factory, AdminClient(factory));
        await using var mcp = await ConnectMcpAsync(factory);

        factory.Race.Arm("Cards", DeleteCard(cardId));
        factory.Log.Clear();

        // Act
        var text = await CallToolAsync(mcp, "add_comment", new() { ["cardId"] = cardId, ["contentMarkdown"] = "Late" });

        // Assert
        (factory.Race.FiredCount, text).ShouldBe((1, $"Error: {ConcurrentDeleteConflict.Message}"));

        AssertAnsweredAsAConflict(factory);
    }

    [Fact]
    public async Task ABulkUpdateThatLosesToADelete_AnsweredByTheToolItself_IsStillLoggedAsAnError()
    {
        // Arrange — a batch tool catches its own failed save and reports it in its own words, so the
        // conflict answer is never reached and nothing may quiet the failure on its behalf. The
        // label is deleted after the tool checks it and before the assignment saves.
        await using var factory = new LoggingFileDatabaseFactory();
        var client = AdminClient(factory);
        var cardId = await CreateCardAsync(factory, client);
        var labelId = await CreateLabelAsync(factory, client);
        await using var mcp = await ConnectMcpAsync(factory);

        factory.Race.Arm("Labels", db => db.Labels.Where(l => l.Id == labelId).ExecuteDeleteAsync());
        factory.Log.Clear();

        // Act
        var text = await CallToolAsync(mcp, "bulk_update_cards", new() { ["cardIds"] = cardId.ToString(), ["labelIds"] = labelId.ToString() });

        // Assert — EF's two entries at Error, as when nothing is declared, and no conflict warning
        factory.Race.FiredCount.ShouldBe(1);
        text.ShouldStartWith("Error: Failed to persist bulk operation", Case.Sensitive);

        AssertLoggedAsAnError(factory);
        factory.Log.ShouldNotContain(entry => entry.Category == _conflictCategory);
    }

    [Fact]
    public async Task TheShippedLogLevels_LeaveOutEachSqlStatement_AndKeepAFailedOne()
    {
        // Arrange — the app as an operator runs it, with a log that keeps only what its shipped
        // levels let through. Starting it on a new database has already applied the migrations.
        await using var factory = new LoggingFileDatabaseFactory(asShipped: true);
        var client = AdminClient(factory);

        // Act — ordinary work, then a command that fails
        await CreateCardAsync(factory, client);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

            await Should.ThrowAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("SELECT * FROM NoSuchTable"));
        }

        // Assert — no executed statement was written, the failed one was, at Error; and the same
        // log kept EF's Information entries for the migrations, so it was not simply too strict to
        // keep Information at all
        var commandEntries = factory.Log.Where(entry => entry.Category == _commandCategory).ToList();

        commandEntries.Select(entry => (entry.EventName, entry.Level)).ShouldBe([(_commandError, LogLevel.Error)]);

        factory.Log.ShouldContain(entry => entry.Category == "Microsoft.EntityFrameworkCore.Migrations" && entry.Level == LogLevel.Information);
    }

    // The answer was a conflict, logged once at Warning with the failure it answered, and EF's two
    // entries for that failure went to Debug.
    private static void AssertAnsweredAsAConflict(LoggingFileDatabaseFactory factory)
    {
        factory.Log.ShouldNotContain(entry => entry.Level >= LogLevel.Error);

        EntryLevels(factory, _commandError).ShouldBe([LogLevel.Debug]);
        EntryLevels(factory, _saveChangesFailed).ShouldBe([LogLevel.Debug]);

        var warning = factory.Log.Where(entry => entry.Category == _conflictCategory).ShouldHaveSingleItem();

        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Exception.ShouldBeAssignableTo<DbUpdateException>();
    }

    private static HttpClient AdminClient(LoggingFileDatabaseFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Key", CollatticeApiFactory.TestAdminAuthKey);

        return client;
    }

    private static async Task<Guid> CreateCardAsync(LoggingFileDatabaseFactory factory, HttpClient client)
    {
        var (boardId, laneId) = await FirstBoardAndLaneAsync(factory);

        var response = await client.PostAsJsonAsync($"/api/v1/boards/{boardId}/cards", new { name = "Doomed", laneId });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
    }

    private static async Task<Guid> CreateLabelAsync(LoggingFileDatabaseFactory factory, HttpClient client)
    {
        var (boardId, _) = await FirstBoardAndLaneAsync(factory);

        var response = await client.PostAsJsonAsync($"/api/v1/boards/{boardId}/labels", new { name = "Doomed", color = "#111111" });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
    }

    private static Func<BoardDbContext, Task> DeleteCard(Guid cardId) =>
        db => db.Cards.Where(c => c.Id == cardId).ExecuteDeleteAsync();

    private static Task<McpClient> ConnectMcpAsync(LoggingFileDatabaseFactory factory)
    {
        var httpClient = factory.CreateClient();

        var transport = new HttpClientTransport
        (
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: true
        );

        return McpClient.CreateAsync(transport);
    }

    private static async Task<string> CallToolAsync(McpClient mcp, string tool, Dictionary<string, object?> arguments)
    {
        arguments["authKey"] = CollatticeApiFactory.TestAdminAuthKey;

        var result = await mcp.CallToolAsync(tool, arguments);

        return result.Content.OfType<TextContentBlock>().Single().Text;
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

    // By default the log keeps every entry, Debug included, so a test sees where an entry was sent.
    // As shipped, the host runs in Production on the app's own appsettings.json, and the log keeps
    // only what the configured levels let through, as an operator's log would.
    // sealed: a leaf test host for this class only; no subtype hierarchy is intended.
    private sealed class LoggingFileDatabaseFactory(bool asShipped = false) : WebApplicationFactory<Program>
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"collattice-logging-{Guid.NewGuid():N}.db");
        private readonly CapturingLoggerProvider _provider = new();
        private readonly bool _asShipped = asShipped;

        public ConcurrentQueue<CapturedEntry> Log => _provider.Entries;

        public ConcurrentDeleteRaceInterceptor Race => Services.GetRequiredService<ConcurrentDeleteRaceInterceptor>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Board", $"Data Source={_databasePath}");
            builder.UseSetting("Admin:AuthKey", CollatticeApiFactory.TestAdminAuthKey);

            if (_asShipped)
            {
                builder.UseEnvironment(Environments.Production);
            }

            builder.ConfigureLogging(logging =>
            {
                TestHostLogging.RemoveEventLog(logging);

                logging.AddProvider(_provider);

                if (!_asShipped)
                {
                    logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
                }
            });

            builder.ConfigureServices(services =>
            {
                // Keep the host off the real GitHub API, as the standard harness does.
                var versionSource = services.Single(d => d.ServiceType == typeof(ILatestVersionSource));
                services.Remove(versionSource);
                services.AddSingleton<ILatestVersionSource, NoNetworkVersionSource>();

                // Added to the app's own registration rather than replacing it, which is the point.
                services.AddSingleton<CardNumberRaceInterceptor>();
                services.AddSingleton<ConcurrentDeleteRaceInterceptor>();
                services.ConfigureDbContext<BoardDbContext>((serviceProvider, options) => options.AddInterceptors
                (
                    serviceProvider.GetRequiredService<CardNumberRaceInterceptor>(),
                    serviceProvider.GetRequiredService<ConcurrentDeleteRaceInterceptor>()
                ));
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
