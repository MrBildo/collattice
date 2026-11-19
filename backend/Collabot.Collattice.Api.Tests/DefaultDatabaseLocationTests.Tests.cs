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

        Refusal(run, "'ConnectionStrings:Board' is not set").ShouldBe((1, true, false), run.Errors);
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
                Refusal(run, "is not writable"),
                run.Errors.Contains($"The database folder '{folder}'", StringComparison.Ordinal)
            );

            observed.ShouldBe(((1, true, false), true), run.Errors);
        }
        finally
        {
            File.SetUnixFileMode(folder, _ownerOnlyFolder);
        }
    }

    // As the installers leave their own folder under the usual 022 umask, and as macOS always does.
    [UnixFact]
    public async Task Start_DefaultFolderWritableByItsOwnerOnly_Starts()
    {
        var home = _installed.NewFolder("home");
        var folder = Path.Combine(home, ".collattice");

        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, _ownerOnlyFolder | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var startup = await InstalledAppFixture.StartAsync(StartInfo(home));

        (startup.ContentRoot is not null, File.Exists(Path.Combine(folder, "collattice.db"))).ShouldBe((true, true), startup.Errors);
    }

    [UnixFact]
    public async Task Start_DefaultFolderEveryUserCanWrite_StopsAndCreatesNoDatabase()
    {
        var home = _installed.NewFolder("home");
        var folder = Path.Combine(home, ".collattice");

        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, _ownerOnlyFolder | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        var run = await InstalledAppFixture.StartAsync(StartInfo(home));

        (Refusal(run, $"The default database folder '{folder}' can be written by other users (every user can write to it)"), File.Exists(Path.Combine(folder, "collattice.db")))
            .ShouldBe(((1, true, false), false), run.Errors);
    }

    // As the installers leave their own folder on Ubuntu, where a login's umask is 002 and each user
    // has a group of their own: group-writable, by a group only they are in. That is accepted. Where
    // the user's group is shared, the same folder is refused. The runner's account decides which this
    // test sees; the expectation is read from the system (id, getent), not from the app.
    [UnprivilegedLinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Start_DefaultFolderWritableByTheUsersGroup_StartsOnlyWhenTheGroupIsTheirsAlone()
    {
        var home = _installed.NewFolder("home");
        var folder = Path.Combine(home, ".collattice");

        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, _ownerOnlyFolder | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var startup = await InstalledAppFixture.StartAsync(StartInfo(home));

        var observed =
        (
            LinuxFolderSharing.PrimaryGroupIsPrivate,
            startup.ContentRoot is not null,
            startup.ExitCode,
            startup.Errors.Contains("can write to it, and that group is not this user's own", StringComparison.Ordinal)
        );

        var expected = LinuxFolderSharing.PrimaryGroupIsPrivate
            ? (true, true, (int?)null, false)
            : (false, false, 1, true);

        observed.ShouldBe(expected, $"user {LinuxFolderSharing.UserName}: {startup.Errors}");
    }

    [UnprivilegedLinuxSharedGroupFact]
    [SupportedOSPlatform("linux")]
    public async Task Start_DefaultFolderWritableByAGroupOthersCanBeIn_Stops()
    {
        var home = _installed.NewFolder("home");
        var folder = Path.Combine(home, ".collattice");
        var (groupId, groupName) = LinuxFolderSharing.SharedGroup!.Value;

        Directory.CreateDirectory(folder);
        LinuxFolderSharing.GiveToGroup(folder, groupId);
        File.SetUnixFileMode(folder, _ownerOnlyFolder | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);

        var run = await InstalledAppFixture.StartAsync(StartInfo(home));

        (Refusal(run, $"its group '{groupName}' can write to it, and that group is not this user's own"), File.Exists(Path.Combine(folder, "collattice.db")))
            .ShouldBe(((1, true, false), false), run.Errors);
    }

    // The folder's own bits allow its owner alone; the access rule lets another user in as well.
    [UnprivilegedLinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Start_DefaultFolderOpenedToAnotherUserByAnAccessRule_Stops()
    {
        var home = _installed.NewFolder("home");
        var folder = Path.Combine(home, ".collattice");

        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, _ownerOnlyFolder);
        LinuxFolderSharing.LetAnotherUserWrite(folder);

        var run = await InstalledAppFixture.StartAsync(StartInfo(home));

        (Refusal(run, "an access rule (ACL) on it can let other users or groups write to it"), File.Exists(Path.Combine(folder, "collattice.db")))
            .ShouldBe(((1, true, false), false), run.Errors);
    }

    // Root owns '/': a default folder that leads to another user's folder is that user's to fill.
    [UnprivilegedLinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Start_DefaultFolderIsALinkToAnotherUsersFolder_Stops()
    {
        var home = _installed.NewFolder("home");
        var folder = Path.Combine(home, ".collattice");

        Directory.CreateSymbolicLink(folder, "/");

        var run = await InstalledAppFixture.StartAsync(StartInfo(home));

        Refusal(run, $"'{folder}' is a link to '/', which other users can write to (it belongs to another user)").ShouldBe((1, true, false), run.Errors);
    }

    [UnixFact]
    public async Task ResetAdminKey_NothingConfiguredAndNoDatabase_CreatesNothing()
    {
        var home = _installed.NewFolder("home");

        var run = await InstalledAppFixture.RunToExitAsync(StartInfo(home, "--reset-admin-key"));

        (run.ExitCode, Directory.Exists(Path.Combine(home, ".collattice"))).ShouldBe((AdminKeyResetCli.ExitDatabaseUnavailable, false), run.Errors);
    }

    // A refusal ends with its exit code and message, and with no stack trace from an unhandled exception.
    private static (int? ExitCode, bool HasMessage, bool StackTrace) Refusal(FinishedRun run, string message) =>
        Refusal(run.ExitCode, run.Errors, message);

    private static (int? ExitCode, bool HasMessage, bool StackTrace) Refusal(StartedApp run, string message) =>
        Refusal(run.ExitCode, run.Errors, message);

    private static (int? ExitCode, bool HasMessage, bool StackTrace) Refusal(int? exitCode, string errors, string message) =>
    (
        exitCode,
        errors.Contains(message, StringComparison.Ordinal),
        errors.Contains("Unhandled exception", StringComparison.Ordinal) || errors.Contains("   at ", StringComparison.Ordinal)
    );

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
