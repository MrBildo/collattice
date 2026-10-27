using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The command runs against a real database file, built by the app's own migrations, because what it
// must get right is which file it opens and what it refuses to touch.
public class AdminKeyResetCliTests : IAsyncLifetime
{
    private const string _adminKey = "01KAAAAAAAAAAAAAAAAAAAAAAA";
    private const string _agentAdminKey = "01KBBBBBBBBBBBBBBBBBBBBBBB";
    private const string _humanKey = "01KCCCCCCCCCCCCCCCCCCCCCCC";

    private readonly string _scratchDir = Path.Combine(Path.GetTempPath(), $"collattice-reset-key-{Guid.NewGuid():N}");
    private string _databasePath = string.Empty;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_scratchDir);
        Directory.CreateDirectory(UserFolder);
        _databasePath = Path.Combine(_scratchDir, "collattice.db");

        await using var db = OpenDatabase();

        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_scratchDir, true);
        }
        catch (IOException)
        {
            // A temp file still held open is left for the OS to clean up; it is never read again.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_OneActiveAdministrator_ReplacesItsKeyAndPrintsTheNewKeyOnce()
    {
        var admin = await AddUserAsync("Admin", _adminKey, UserRole.Administrator);
        var agentAdmin = await AddUserAsync("Agent", _agentAdminKey, UserRole.AgentAdministrator);
        var human = await AddUserAsync("Human", _humanKey, UserRole.HumanUser);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await RunAsync([], DatabaseConfiguration(), stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitOk);
        stderr.ToString().ShouldBeEmpty();

        var newKey = await KeyOfAsync(admin);
        newKey.ShouldNotBe(_adminKey);
        Ulid.TryParse(newKey, CultureInfo.InvariantCulture, out _).ShouldBeTrue();

        var output = stdout.ToString();
        output.Split(newKey).Length.ShouldBe(2);
        output.ShouldContain(_databasePath);
        output.ShouldNotContain(_adminKey);

        (await KeyOfAsync(agentAdmin)).ShouldBe(_agentAdminKey);
        (await KeyOfAsync(human)).ShouldBe(_humanKey);
    }

    [Fact]
    public async Task RunAsync_TwoActiveAdministratorsAndNoId_ChangesNothingAndListsBoth()
    {
        var first = await AddUserAsync("Admin", _adminKey, UserRole.Administrator);
        var second = await AddUserAsync("Second", _agentAdminKey, UserRole.Administrator);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await RunAsync([], DatabaseConfiguration(), stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitAdministratorAmbiguous);
        stdout.ToString().ShouldBeEmpty();
        stderr.ToString().ShouldContain(first.ToString());
        stderr.ToString().ShouldContain(second.ToString());
        (await KeyOfAsync(first)).ShouldBe(_adminKey);
        (await KeyOfAsync(second)).ShouldBe(_agentAdminKey);
    }

    [Fact]
    public async Task RunAsync_TwoActiveAdministratorsAndAnId_ReplacesOnlyThatOnesKey()
    {
        var first = await AddUserAsync("Admin", _adminKey, UserRole.Administrator);
        var second = await AddUserAsync("Second", _agentAdminKey, UserRole.Administrator);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await RunAsync([second.ToString()], DatabaseConfiguration(), stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitOk);
        (await KeyOfAsync(first)).ShouldBe(_adminKey);
        (await KeyOfAsync(second)).ShouldNotBe(_agentAdminKey);
        stdout.ToString().ShouldContain(await KeyOfAsync(second));
    }

    [Fact]
    public async Task RunAsync_IdOfAUserWhoIsNotAnAdministrator_ChangesNothing()
    {
        await AddUserAsync("Admin", _adminKey, UserRole.Administrator);
        var agentAdmin = await AddUserAsync("Agent", _agentAdminKey, UserRole.AgentAdministrator);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await RunAsync([agentAdmin.ToString()], DatabaseConfiguration(), stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitNoAdministrator);
        stdout.ToString().ShouldBeEmpty();
        (await KeyOfAsync(agentAdmin)).ShouldBe(_agentAdminKey);
    }

    [Fact]
    public async Task RunAsync_OnlyAdministratorIsDeactivated_ChangesNothing()
    {
        var admin = await AddUserAsync("Admin", _adminKey, UserRole.Administrator, isActive: false);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await RunAsync([], DatabaseConfiguration(), stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitNoAdministrator);
        (await KeyOfAsync(admin)).ShouldBe(_adminKey);
    }

    [Fact]
    public async Task RunAsync_NoDatabaseAtTheConfiguredPath_FailsWithoutCreatingOne()
    {
        var missingPath = Path.Combine(_scratchDir, "missing.db");
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await RunAsync([], Configuration($"Data Source={missingPath}"), stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitDatabaseUnavailable);
        stderr.ToString().ShouldContain(missingPath);
        File.Exists(missingPath).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=relative/collattice.db")]
    public async Task RunAsync_ConnectionStringNotAnAbsoluteFile_FailsBeforeOpeningAnything(string connectionString)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await RunAsync([], Configuration(connectionString), stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitDatabaseUnavailable);
        stderr.ToString().ShouldContain("ConnectionStrings");
    }

    [Fact]
    public async Task RunAsync_NothingConfiguredAndNoDefaultDatabase_FailsWithoutCreatingTheFolder()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await RunAsync([], Configuration(null), stdout, stderr);

        (exit, Directory.Exists(DefaultFolder)).ShouldBe((AdminKeyResetCli.ExitDatabaseUnavailable, false));
        stderr.ToString().ShouldContain($"there is no default database '{DefaultPath}'", Case.Sensitive);
    }

    [Fact]
    public async Task RunAsync_NothingConfiguredAndNoUserFolder_FailsNamingTheSetting()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await AdminKeyResetCli.RunAsync([], Configuration(null), () => string.Empty, stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitDatabaseUnavailable);
        stderr.ToString().ShouldContain("'ConnectionStrings:Board' is not set", Case.Sensitive);
    }

    // The command opens the database the app would open, so with nothing configured that is the default.
    [Fact]
    public async Task RunAsync_NothingConfigured_ResetsTheKeyInTheDefaultDatabase()
    {
        _databasePath = DefaultPath;
        Directory.CreateDirectory(DefaultFolder);

        await using (var db = OpenDatabase())
        {
            await db.Database.MigrateAsync();
        }

        var admin = await AddUserAsync("Admin", _adminKey, UserRole.Administrator);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await RunAsync([], Configuration(null), stdout, stderr);

        (exit, await KeyOfAsync(admin) != _adminKey).ShouldBe((AdminKeyResetCli.ExitOk, true), stderr.ToString());
        stdout.ToString().ShouldContain($"reset-admin-key: database {DefaultPath}", Case.Sensitive);
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task RunAsync_NothingConfiguredAndTheDefaultFolderOtherUsersCanWrite_ChangesNothing()
    {
        _databasePath = DefaultPath;
        Directory.CreateDirectory(DefaultFolder);

        await using (var db = OpenDatabase())
        {
            await db.Database.MigrateAsync();
        }

        var admin = await AddUserAsync("Admin", _adminKey, UserRole.Administrator);
        File.SetUnixFileMode(DefaultFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await RunAsync([], Configuration(null), stdout, stderr);

        (exit, await KeyOfAsync(admin)).ShouldBe((AdminKeyResetCli.ExitDatabaseUnavailable, _adminKey));
        stderr.ToString().ShouldContain("can be written by other users", Case.Sensitive);
    }

    [Fact]
    public async Task RunAsync_AFileThatIsNotACollatticeDatabase_FailsAndLeavesItAlone()
    {
        var otherPath = Path.Combine(_scratchDir, "other.db");
        await File.WriteAllBytesAsync(otherPath, []);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await RunAsync([], Configuration($"Data Source={otherPath}"), stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitDatabaseUnavailable);
        stdout.ToString().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("--yes")]
    public async Task RunAsync_ArgumentThatIsNotAUserId_PrintsUsage(string argument)
    {
        var admin = await AddUserAsync("Admin", _adminKey, UserRole.Administrator);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await RunAsync([argument], DatabaseConfiguration(), stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitUsage);
        stderr.ToString().ShouldContain("usage:");
        (await KeyOfAsync(admin)).ShouldBe(_adminKey);
    }

    // The default database would be in this folder, under the test's own temporary folder; the
    // command is never handed the real user folder.
    private string UserFolder => Path.Combine(_scratchDir, "user");

    private string DefaultFolder => Path.Combine(UserFolder, OperatingSystem.IsWindows() ? "Collattice" : ".collattice");

    private string DefaultPath => Path.Combine(DefaultFolder, "collattice.db");

    private Task<int> RunAsync(string[] args, IConfiguration configuration, StringWriter stdout, StringWriter stderr) =>
        AdminKeyResetCli.RunAsync(args, configuration, () => UserFolder, stdout, stderr);

    private BoardDbContext OpenDatabase() =>
        new(new DbContextOptionsBuilder<BoardDbContext>().UseSqlite($"Data Source={_databasePath}").Options);

    private IConfiguration DatabaseConfiguration() => Configuration($"Data Source={_databasePath}");

    private static IConfiguration Configuration(string? connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["ConnectionStrings:Board"] = connectionString })
            .Build();

    private async Task<Guid> AddUserAsync(string name, string authKey, UserRole role, bool isActive = true)
    {
        await using var db = OpenDatabase();

        var user = new BoardUser { Id = Guid.NewGuid(), Name = name, AuthKey = authKey, Role = role, IsActive = isActive };
        db.Users.Add(user);

        await db.SaveChangesAsync();

        return user.Id;
    }

    private async Task<string> KeyOfAsync(Guid userId)
    {
        await using var db = OpenDatabase();

        var user = await db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == userId);

        return user.AuthKey;
    }
}
