using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The busy timeout is a per-connection SQLite setting, so the test holds several pooled connections
// open at once and reads it on each. It runs the app's own DbContext registration over a real
// database file: the standard harness swaps in one shared in-memory connection, which can never
// show a second connection.
public class SqliteBusyTimeoutTests
{
    [Fact]
    public async Task PooledConnections_HeldOpenTogether_EachCarryTheBusyTimeout()
    {
        await using var factory = new FileDatabaseFactory();

        _ = factory.CreateClient();

        var scopes = Enumerable.Range(0, 4)
            .Select(_ => factory.Services.CreateAsyncScope())
                .ToList();

        try
        {
            var readings = new List<(SqliteConnection Connection, long BusyTimeout)>();

            foreach (var scope in scopes)
            {
                var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
                await db.Database.OpenConnectionAsync();

                var connection = (SqliteConnection)db.Database.GetDbConnection();
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA busy_timeout;";

                var busyTimeout = await command.ExecuteScalarAsync();
                readings.Add((connection, Convert.ToInt64(busyTimeout, CultureInfo.InvariantCulture)));
            }

            readings
                .Select(r => r.Connection.Handle)
                .Distinct(ReferenceEqualityComparer.Instance)
                    .Count()
                    .ShouldBe(4);

            readings.ShouldAllBe(r => r.BusyTimeout == SqliteBusyTimeoutInterceptor.BusyTimeoutMilliseconds);
        }
        finally
        {
            foreach (var scope in scopes)
            {
                await scope.DisposeAsync();
            }
        }
    }
}

// sealed: a leaf test host for this file only; no subtype hierarchy is intended.
file sealed class FileDatabaseFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"collattice-busy-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Board", $"Data Source={_databasePath}");
        builder.UseSetting("Admin:AuthKey", CollatticeApiFactory.TestAdminAuthKey);

        builder.ConfigureLogging(TestHostLogging.RemoveEventLog);

        // Keep the host off the real GitHub API, as the standard harness does.
        builder.ConfigureServices(services =>
        {
            var versionSource = services.Single(d => d.ServiceType == typeof(ILatestVersionSource));
            services.Remove(versionSource);
            services.AddSingleton<ILatestVersionSource, NoNetworkVersionSource>();
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
