using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Before the app applies a migration to a database that holds data, it copies the file beside itself.
// A brand-new database is an empty file with nothing to keep, so it gets no copy.
public class DatabaseBackupTests(InstalledAppFixture installed) : IClassFixture<InstalledAppFixture>
{
    private readonly InstalledAppFixture _installed = installed;

    [Fact]
    public async Task Start_DatabaseOneMigrationBehind_BacksItUpBeforeMigrating()
    {
        var folder = _installed.NewFolder("older");
        var databasePath = Path.Combine(folder, "collattice.db");

        await using (var db = new BoardDbContext(new DbContextOptionsBuilder<BoardDbContext>().UseSqlite($"Data Source={databasePath}").Options))
        {
            var migrations = db.Database.GetMigrations().ToList();

            await db.GetService<IMigrator>().MigrateAsync(migrations[^2]);
        }

        SqliteConnection.ClearAllPools();

        var startInfo = _installed.StartInfo(_installed.NewFolder("start"), "Production");
        startInfo.Environment["ConnectionStrings__Board"] = $"Data Source={databasePath}";

        var startup = await InstalledAppFixture.StartAsync(startInfo);

        (startup.ContentRoot is not null, Directory.GetFiles(folder, "collattice.db.bak-*").Length).ShouldBe((true, 1), startup.Errors);
    }
}
