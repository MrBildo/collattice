using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// A start the app refuses on purpose ends with its message and a non-zero exit code, and nothing else:
// no unhandled exception, so no stack trace and no crash dump. The refusals that depend on the home
// folder (the default database location) are in DefaultDatabaseLocationTests, on macOS and Linux.
public class StartupRefusalTests(InstalledAppFixture installed) : IClassFixture<InstalledAppFixture>
{
    private readonly InstalledAppFixture _installed = installed;

    [Fact]
    public async Task Start_DatabasePathRelative_StopsWithTheMessageAndNoStackTrace()
    {
        var startInfo = _installed.StartInfo(_installed.NewFolder("start"), "Production");
        startInfo.Environment["ConnectionStrings__Board"] = "Data Source=data/collattice.db";

        var run = await InstalledAppFixture.StartAsync(startInfo);

        RefusalOf(run, "names a relative data source 'data/collattice.db'").ShouldBe((true, 1, true, false, false), run.Errors);
    }

    // The database is made by this build, so the only thing that differs between the two starts is the
    // one migration a newer version would have recorded.
    [Fact]
    public async Task Start_DatabaseUpgradedByANewerVersion_StopsAndLeavesItUntouched()
    {
        const string newerMigration = "29991231235959_FromANewerVersion";

        var folder = _installed.NewFolder("newer");
        var databasePath = Path.Combine(folder, "collattice.db");

        var current = await InstalledAppFixture.StartAsync(StartInfo(databasePath));

        current.ContentRoot.ShouldNotBeNull(current.Errors);

        await ExecuteAsync(databasePath, $"PRAGMA wal_checkpoint(TRUNCATE); INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('{newerMigration}', '99.0.0');");

        var before = await HashAsync(databasePath);

        var run = await InstalledAppFixture.StartAsync(StartInfo(databasePath));

        // Untouched: the file is the same, and SQLite's write-ahead log beside it, which the app's
        // connections leave behind when the process ends, holds nothing.
        var walPath = databasePath + "-wal";

        var observed =
        (
            RefusalOf(run, "has been upgraded by a newer version of Collattice"),
            run.Errors.Contains(newerMigration, StringComparison.Ordinal),
            run.Errors.Contains(databasePath, StringComparison.Ordinal),
            await HashAsync(databasePath) == before,
            File.Exists(walPath) ? new FileInfo(walPath).Length : 0,
            Directory.GetFiles(folder, "collattice.db.bak-*").Length
        );

        observed.ShouldBe(((true, 1, true, false, false), true, true, true, 0L, 0), run.Output + run.Errors);
    }

    // Stopped by itself, with the refusal's exit code and message, and no sign of an unhandled exception.
    private static (bool Stopped, int? ExitCode, bool HasMessage, bool Unhandled, bool StackTrace) RefusalOf(StartedApp run, string message) =>
    (
        run.ContentRoot is null,
        run.ExitCode,
        run.Errors.Contains(message, StringComparison.Ordinal),
        run.Errors.Contains("Unhandled exception", StringComparison.Ordinal),
        run.Errors.Contains("   at ", StringComparison.Ordinal)
    );

    private System.Diagnostics.ProcessStartInfo StartInfo(string databasePath)
    {
        var startInfo = _installed.StartInfo(_installed.NewFolder("start"), "Production");
        startInfo.Environment["ConnectionStrings__Board"] = $"Data Source={databasePath}";

        return startInfo;
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
    }

    private static async Task<string> HashAsync(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
}
