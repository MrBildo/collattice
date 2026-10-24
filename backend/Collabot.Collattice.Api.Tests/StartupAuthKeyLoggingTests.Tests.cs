using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// An auth key is written to the log only when the app has just generated it on first setup. These
// boot the app's own registration (its EF Core logging included, captured at every level) over a real
// database file, so a key that reached any log entry, a SQL command's included, is caught.
public class StartupAuthKeyLoggingTests : IDisposable
{
    private const string _configuredKey = "01KDDDDDDDDDDDDDDDDDDDDDDD";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"collattice-startup-key-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

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

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task FirstStart_GeneratedKey_IsLoggedInExactlyOneEntry()
    {
        await using var host = new FileDatabaseHost(_databasePath, adminAuthKey: null);

        _ = host.CreateClient();

        var key = await AdminKeyAsync(host);
        var entries = host.Log.Where(entry => entry.Contains(key, StringComparison.Ordinal)).ToList();

        entries.ShouldHaveSingleItem().ShouldBe($"Admin auth key: {key}");
    }

    [Fact]
    public async Task FirstStart_KeySetInConfiguration_IsNotLogged()
    {
        await using var host = new FileDatabaseHost(_databasePath, _configuredKey);

        _ = host.CreateClient();

        (await AdminKeyAsync(host)).ShouldBe(_configuredKey);
        host.Log.ShouldNotBeEmpty();
        host.Log.ShouldNotContain(entry => entry.Contains(_configuredKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaterStart_ExistingAdministrator_KeyIsNotLogged()
    {
        string key;

        await using (var firstStart = new FileDatabaseHost(_databasePath, adminAuthKey: null))
        {
            _ = firstStart.CreateClient();
            key = await AdminKeyAsync(firstStart);
        }

        await using var laterStart = new FileDatabaseHost(_databasePath, adminAuthKey: null);

        _ = laterStart.CreateClient();

        laterStart.Log.ShouldContain(entry => entry.Contains("--reset-admin-key", StringComparison.Ordinal));
        laterStart.Log.ShouldNotContain(entry => entry.Contains(key, StringComparison.Ordinal));
    }

    private static async Task<string> AdminKeyAsync(WebApplicationFactory<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var admin = await db.Users.AsNoTracking().SingleAsync(user => user.Role == UserRole.Administrator);

        return admin.AuthKey;
    }

    // sealed: a leaf test host for this class only; no subtype hierarchy is intended.
    private sealed class FileDatabaseHost(string databasePath, string? adminAuthKey) : WebApplicationFactory<Program>
    {
        private readonly string _databasePath = databasePath;
        private readonly string? _adminAuthKey = adminAuthKey;
        private readonly MessageCapturingLoggerProvider _provider = new();

        public IReadOnlyCollection<string> Log => _provider.Messages;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Production, so a developer's own appsettings.Development.json cannot supply a key.
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Board", $"Data Source={_databasePath}");

            if (_adminAuthKey is not null)
            {
                builder.UseSetting("Admin:AuthKey", _adminAuthKey);
            }

            builder.ConfigureLogging(logging =>
            {
                TestHostLogging.RemoveEventLog(logging);

                logging.AddProvider(_provider);
                logging.AddFilter<MessageCapturingLoggerProvider>(null, LogLevel.Trace);
            });

            builder.ConfigureServices(services =>
            {
                // Keep the host off the real GitHub API, as the standard harness does.
                var versionSource = services.Single(d => d.ServiceType == typeof(ILatestVersionSource));
                services.Remove(versionSource);
                services.AddSingleton<ILatestVersionSource, NoNetworkVersionSource>();
            });
        }
    }

    // sealed: a leaf test provider; no subtype hierarchy is intended.
    private sealed class MessageCapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new MessageCapturingLogger(_messages);

        public void Dispose()
        {
        }
    }

    // sealed: a leaf test logger; no subtype hierarchy is intended.
    private sealed class MessageCapturingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        private readonly ConcurrentQueue<string> _messages = messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        // The rendered message, with any exception's text, is what a log sink would hold.
        public void Log<TState>
        (
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) =>
            _messages.Enqueue(exception is null ? formatter(state, exception) : $"{formatter(state, exception)} {exception}");
    }
}
