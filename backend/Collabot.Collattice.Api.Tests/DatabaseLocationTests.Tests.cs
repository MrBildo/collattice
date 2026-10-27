using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Where the database lives when a path is configured and when none is. Every test hands the resolver a
// temporary folder as the user's base folder, so none of them reads or writes the real one.
//
// Sealed so the plain IDisposable shape below is complete; no derived class can add state to dispose.
public sealed class DatabaseLocationTests : IDisposable
{
    private const UnixFileMode _ownerOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode _ownerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"collattice-database-location-{Guid.NewGuid():N}");
    private readonly string _userFolder;

    public DatabaseLocationTests()
    {
        _userFolder = Path.Combine(_root, "user");

        Directory.CreateDirectory(_userFolder);
    }

    // The names the installers use for their own folder, written out rather than read from the code.
    private string DefaultFolder => Path.Combine(_userFolder, OperatingSystem.IsWindows() ? "Collattice" : ".collattice");

    private string DefaultPath => Path.Combine(DefaultFolder, "collattice.db");

    [Fact]
    public void TryResolve_PathConfigured_UsesItWithoutReadingTheUserFolder()
    {
        var configured = Path.Combine(_root, "configured", "board.db");
        var userFolderRead = false;

        string? ReadUserFolder()
        {
            userFolderRead = true;
            return _userFolder;
        }

        var resolved = DatabaseLocation.TryResolve(Configuration($"Data Source={configured}"), ReadUserFolder, out var database, out var error);

        (resolved, database?.FilePath, database?.IsDefault, userFolderRead, error).ShouldBe((true, configured, false, false, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolve_NothingConfigured_UsesCollatticeDbInTheUserFolder(string? configured)
    {
        var resolved = DatabaseLocation.TryResolve(Configuration(configured), () => _userFolder, out var database, out _);

        var dataSource = database is null ? null : new SqliteConnectionStringBuilder(database.ConnectionString).DataSource;

        (resolved, database?.FilePath, dataSource, database?.IsDefault).ShouldBe((true, DefaultPath, DefaultPath, true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative-home")]
    [InlineData(".")]
    public void TryResolve_NothingConfiguredAndNoAbsoluteUserFolder_RefusesNamingTheSetting(string? userFolder)
    {
        var resolved = DatabaseLocation.TryResolve(Configuration(null), () => userFolder, out var database, out var error);

        (resolved, database).ShouldBe((false, null));
        error.ShouldNotBeNull().ShouldContain("'ConnectionStrings:Board' is not set", Case.Sensitive);
    }

    [Fact]
    public void TryResolve_RelativePathConfigured_Refuses()
    {
        var resolved = DatabaseLocation.TryResolve(Configuration("Data Source=data/collattice.db"), () => _userFolder, out var database, out var error);

        (resolved, database).ShouldBe((false, null));
        error.ShouldNotBeNull().ShouldContain("relative data source 'data/collattice.db'", Case.Sensitive);
    }

    [Fact]
    public void TryPrepare_InMemoryDatabaseConfigured_TouchesNoFile()
    {
        DatabaseLocation.TryResolve(Configuration("Data Source=:memory:"), () => _userFolder, out var database, out _).ShouldBeTrue();

        var prepared = DatabaseLocation.TryPrepare(database, out var createdNew, out var error);

        (prepared, createdNew, database.FilePath, error).ShouldBe((true, false, null, null));
    }

    [Fact]
    public void TryPrepare_NewDefault_CreatesTheFolderAndAnEmptyFileOnce()
    {
        var database = ResolveDefault();

        var first = DatabaseLocation.TryPrepare(database, out var createdFirst, out var firstError);
        var length = new FileInfo(DefaultPath).Length;
        var second = DatabaseLocation.TryPrepare(database, out var createdSecond, out var secondError);

        (first, createdFirst, firstError, length, second, createdSecond, secondError).ShouldBe((true, true, null, 0L, true, false, null));
    }

    [Fact]
    public void TryPrepare_PathConfiguredInFoldersThatDoNotExist_CreatesThemAndTheFile()
    {
        var configured = Path.Combine(_root, "srv", "collattice", "data", "collattice.db");

        DatabaseLocation.TryResolve(Configuration($"Data Source={configured}"), () => _userFolder, out var database, out _).ShouldBeTrue();

        var prepared = DatabaseLocation.TryPrepare(database, out var createdNew, out var error);

        (prepared, createdNew, error, File.Exists(configured), Directory.Exists(DefaultFolder)).ShouldBe((true, true, null, true, false));
    }

    [Fact]
    public async Task TryPrepare_NewDefault_TheFileOpensAsAnEmptySqliteDatabase()
    {
        var database = ResolveDefault();

        DatabaseLocation.TryPrepare(database, out _, out _).ShouldBeTrue();

        await using var connection = new SqliteConnection(database.ConnectionString);

        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master;";

        (await command.ExecuteScalarAsync()).ShouldBe(0L);
    }

    // Creating the file owner-only before SQLite opens it is what keeps SQLite's own files owner-only:
    // SQLite gives its -wal and -shm files the database file's mode, and the app's pre-migration backup
    // is a copy, which keeps the mode too.
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task TryPrepare_NewDefault_FolderFileAndSqliteFilesAreOwnerOnly()
    {
        var database = ResolveDefault();

        DatabaseLocation.TryPrepare(database, out _, out _).ShouldBeTrue();

        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = 'wal'; CREATE TABLE t (x); INSERT INTO t VALUES (1);";

            await command.ExecuteNonQueryAsync();

            var backupPath = DefaultPath + ".bak";
            File.Copy(DefaultPath, backupPath);

            var observed =
            (
                File.GetUnixFileMode(DefaultFolder),
                File.GetUnixFileMode(DefaultPath),
                File.GetUnixFileMode(DefaultPath + "-wal"),
                File.GetUnixFileMode(DefaultPath + "-shm"),
                File.GetUnixFileMode(backupPath)
            );

            observed.ShouldBe((_ownerOnlyFolder, _ownerOnlyFile, _ownerOnlyFile, _ownerOnlyFile, _ownerOnlyFile));
        }

        SqliteConnection.ClearAllPools();
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void TryPrepare_PathConfiguredInFoldersThatDoNotExist_CreatesEachFolderOwnerOnly()
    {
        var dataFolder = Path.Combine(_root, "srv", "data");

        DatabaseLocation.TryResolve(Configuration($"Data Source={Path.Combine(dataFolder, "collattice.db")}"), () => _userFolder, out var database, out _).ShouldBeTrue();
        DatabaseLocation.TryPrepare(database, out _, out _).ShouldBeTrue();

        (File.GetUnixFileMode(Path.Combine(_root, "srv")), File.GetUnixFileMode(dataFolder)).ShouldBe((_ownerOnlyFolder, _ownerOnlyFolder));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void TryPrepare_DefaultFolderOtherUsersCanWrite_RefusesAndCreatesNoDatabase()
    {
        Directory.CreateDirectory(DefaultFolder);
        File.SetUnixFileMode(DefaultFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        var prepared = DatabaseLocation.TryPrepare(ResolveDefault(), out _, out var error);

        (prepared, File.Exists(DefaultPath)).ShouldBe((false, false));
        error.ShouldNotBeNull().ShouldContain($"The default database folder '{DefaultFolder}' can be written by other users", Case.Sensitive);
    }

    // As a world-writable /tmp would be: sticky, so only the owner of a file can remove it, but anyone
    // can create one, or a whole folder, first.
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void TryPrepare_DefaultFolderIsALinkToAFolderOtherUsersCanWrite_RefusesNamingBoth()
    {
        var shared = Path.Combine(_root, "shared");

        Directory.CreateDirectory(shared);
        File.SetUnixFileMode(shared, UnixFileMode.StickyBit | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        Directory.CreateSymbolicLink(DefaultFolder, shared);

        var prepared = DatabaseLocation.TryPrepare(ResolveDefault(), out _, out var error);

        (prepared, File.Exists(Path.Combine(shared, "collattice.db"))).ShouldBe((false, false));
        error.ShouldNotBeNull().ShouldContain($"'{DefaultFolder}' is a link to '{shared}', which other users can write to", Case.Sensitive);
    }

    // The check is for the default alone. A configured path is the operator's choice, and a folder like
    // /tmp is where tests and short-lived runs often put one.
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void TryPrepare_PathConfiguredInAFolderOtherUsersCanWrite_UsesIt()
    {
        var shared = Path.Combine(_root, "shared");

        Directory.CreateDirectory(shared);
        File.SetUnixFileMode(shared, UnixFileMode.StickyBit | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        DatabaseLocation.TryResolve(Configuration($"Data Source={Path.Combine(shared, "collattice.db")}"), () => _userFolder, out var database, out _).ShouldBeTrue();

        DatabaseLocation.TryPrepare(database, out var createdNew, out var error).ShouldBeTrue(error);
        createdNew.ShouldBeTrue();
    }

    [UnprivilegedUnixFact]
    [UnsupportedOSPlatform("windows")]
    public void TryPrepare_DefaultFolderNotWritable_RefusesNamingTheFolder()
    {
        var database = ResolveDefault();

        DatabaseLocation.TryPrepare(database, out _, out _).ShouldBeTrue();
        File.SetUnixFileMode(DefaultFolder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var prepared = DatabaseLocation.TryPrepare(database, out _, out var error);

            prepared.ShouldBeFalse();
            error.ShouldNotBeNull().ShouldContain($"The database folder '{DefaultFolder}' for the default database '{DefaultPath}' (no 'ConnectionStrings:Board' is configured) is not writable", Case.Sensitive);
        }
        finally
        {
            File.SetUnixFileMode(DefaultFolder, _ownerOnlyFolder);
        }
    }

    [UnprivilegedUnixFact]
    [UnsupportedOSPlatform("windows")]
    public void TryPrepare_DatabaseFileNotWritable_RefusesNamingTheFile()
    {
        var database = ResolveDefault();

        DatabaseLocation.TryPrepare(database, out _, out _).ShouldBeTrue();
        File.SetUnixFileMode(DefaultPath, UnixFileMode.UserRead);

        var prepared = DatabaseLocation.TryPrepare(database, out _, out var error);

        prepared.ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain($"Collattice can't write to the default database '{DefaultPath}'", Case.Sensitive);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // A file still held open is left for the OS to clean up; it is never read again.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    private ResolvedDatabase ResolveDefault()
    {
        DatabaseLocation.TryResolve(Configuration(null), () => _userFolder, out var database, out var error).ShouldBeTrue(error);

        return database;
    }

    private static IConfiguration Configuration(string? connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["ConnectionStrings:Board"] = connectionString })
            .Build();
}
