using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// A downloaded release, run with nothing configured, keeps its board in ~/.collattice/collattice.db,
// says so at startup, and stops with a message when it can't use that place. Each test gives the
// program a home folder of its own, which is why these run on macOS and Linux only: there the home
// folder comes from HOME, while Windows reads its per-user folder from the system.
[UnsupportedOSPlatform("windows")]
public class DefaultDatabaseLocationTests(UnconfiguredAppFixture installed) : IClassFixture<UnconfiguredAppFixture>
{
    private const UnixFileMode _ownerOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode _ownerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const string _defaultLine = "Using the default database location";
    private const string _newDatabaseLine = "This start created a new, empty database there.";
    private const string _settingsLine = "set 'ConnectionStrings:Board' in appsettings.json or the 'ConnectionStrings__Board' environment variable";

    private readonly UnconfiguredAppFixture _installed = installed;

    [UnixFact]
    public async Task Start_NothingConfigured_CreatesTheDefaultDatabaseOwnerOnlyAndSaysSo_ThenReopensIt()
    {
        var home = _installed.NewFolder("home");
        var folder = Path.Combine(home, ".collattice");
        var databasePath = Path.Combine(folder, "collattice.db");

        var first = await InstalledAppFixture.StartAsync(StartInfo(home));

        var firstObserved =
        (
            first.ContentRoot is not null,
            first.Output.Contains($"{_defaultLine} {databasePath},", StringComparison.Ordinal),
            first.Output.Contains(_newDatabaseLine, StringComparison.Ordinal),
            first.Output.Contains(_settingsLine, StringComparison.Ordinal),
            first.Output.Contains("Admin auth key:", StringComparison.Ordinal),
            File.GetUnixFileMode(folder),
            File.GetUnixFileMode(databasePath),
            Directory.GetFiles(folder, "collattice.db.bak-*").Length
        );

        firstObserved.ShouldBe((true, true, true, true, true, _ownerOnlyFolder, _ownerOnlyFile, 0), first.Output + first.Errors);

        // A second start finds the same board: no new database, no new administrator, no new key.
        var second = await InstalledAppFixture.StartAsync(StartInfo(home));

        var secondObserved =
        (
            second.ContentRoot is not null,
            second.Output.Contains($"{_defaultLine} {databasePath},", StringComparison.Ordinal),
            second.Output.Contains(_newDatabaseLine, StringComparison.Ordinal),
            second.Output.Contains("Admin auth key:", StringComparison.Ordinal),
            await CountUsersAsync(databasePath)
        );

        secondObserved.ShouldBe((true, true, false, false, 1), second.Output + second.Errors);
    }

    [UnixFact]
    public async Task Start_NothingConfiguredAndHomeIsRelative_StopsNamingTheSetting()
    {
        var run = await InstalledAppFixture.RunToExitAsync(StartInfo("relative/home"));

        (run.ExitCode != 0, run.Errors.Contains("'ConnectionStrings:Board' is not set", StringComparison.Ordinal)).ShouldBe((true, true), run.Errors);
    }

    [UnprivilegedUnixFact]
    public async Task Start_NothingConfiguredAndTheDefaultFolderNotWritable_StopsNamingTheFolder()
    {
        var home = _installed.NewFolder("home");
        var folder = Path.Combine(home, ".collattice");

        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var run = await InstalledAppFixture.RunToExitAsync(StartInfo(home));

            var observed =
            (
                run.ExitCode != 0,
                run.Errors.Contains($"The database folder '{folder}'", StringComparison.Ordinal),
                run.Errors.Contains("is not writable", StringComparison.Ordinal)
            );

            observed.ShouldBe((true, true, true), run.Errors);
        }
        finally
        {
            File.SetUnixFileMode(folder, _ownerOnlyFolder);
        }
    }

    [UnixFact]
    public async Task ResetAdminKey_NothingConfiguredAndNoDatabase_CreatesNothing()
    {
        var home = _installed.NewFolder("home");

        var run = await InstalledAppFixture.RunToExitAsync(StartInfo(home, "--reset-admin-key"));

        (run.ExitCode, Directory.Exists(Path.Combine(home, ".collattice"))).ShouldBe((AdminKeyResetCli.ExitDatabaseUnavailable, false), run.Errors);
    }

    private ProcessStartInfo StartInfo(string home, params string[] arguments)
    {
        var startInfo = _installed.StartInfo(_installed.NewFolder("start"), "Production", arguments);

        startInfo.Environment["HOME"] = home;

        return startInfo;
    }

    private static async Task<int> CountUsersAsync(string databasePath)
    {
        var options = new DbContextOptionsBuilder<BoardDbContext>().UseSqlite($"Data Source={databasePath};Mode=ReadOnly").Options;

        await using var db = new BoardDbContext(options);

        var count = await db.Users.CountAsync();

        SqliteConnection.ClearAllPools();

        return count;
    }
}
