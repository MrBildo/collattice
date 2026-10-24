using System.Globalization;
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

        var exit = await AdminKeyResetCli.RunAsync([], DatabaseConfiguration(), stdout, stderr);

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

        var exit = await AdminKeyResetCli.RunAsync([], DatabaseConfiguration(), stdout, stderr);

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

        var exit = await AdminKeyResetCli.RunAsync([second.ToString()], DatabaseConfiguration(), stdout, stderr);

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

        var exit = await AdminKeyResetCli.RunAsync([agentAdmin.ToString()], DatabaseConfiguration(), stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitNoAdministrator);
        stdout.ToString().ShouldBeEmpty();
        (await KeyOfAsync(agentAdmin)).ShouldBe(_agentAdminKey);
    }

    [Fact]
    public async Task RunAsync_OnlyAdministratorIsDeactivated_ChangesNothing()
    {
        var admin = await AddUserAsync("Admin", _adminKey, UserRole.Administrator, isActive: false);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await AdminKeyResetCli.RunAsync([], DatabaseConfiguration(), stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitNoAdministrator);
        (await KeyOfAsync(admin)).ShouldBe(_adminKey);
    }

    [Fact]
    public async Task RunAsync_NoDatabaseAtTheConfiguredPath_FailsWithoutCreatingOne()
    {
        var missingPath = Path.Combine(_scratchDir, "missing.db");
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await AdminKeyResetCli.RunAsync([], Configuration($"Data Source={missingPath}"), stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitDatabaseUnavailable);
        stderr.ToString().ShouldContain(missingPath);
        File.Exists(missingPath).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=relative/collattice.db")]
    public async Task RunAsync_ConnectionStringUnsetOrNotAnAbsoluteFile_FailsBeforeOpeningAnything(string? connectionString)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await AdminKeyResetCli.RunAsync([], Configuration(connectionString), stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitDatabaseUnavailable);
        stderr.ToString().ShouldContain("ConnectionStrings");
    }

    [Fact]
    public async Task RunAsync_AFileThatIsNotACollatticeDatabase_FailsAndLeavesItAlone()
    {
        var otherPath = Path.Combine(_scratchDir, "other.db");
        await File.WriteAllBytesAsync(otherPath, []);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await AdminKeyResetCli.RunAsync([], Configuration($"Data Source={otherPath}"), stdout, stderr);

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

        var exit = await AdminKeyResetCli.RunAsync([argument], DatabaseConfiguration(), stdout, stderr);

        exit.ShouldBe(AdminKeyResetCli.ExitUsage);
        stderr.ToString().ShouldContain("usage:");
        (await KeyOfAsync(admin)).ShouldBe(_adminKey);
    }

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
