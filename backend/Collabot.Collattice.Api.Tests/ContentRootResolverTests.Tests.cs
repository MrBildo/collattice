using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// An installed program is often started from some other folder: the home folder, or found on PATH.
// It must still read the appsettings.json beside it, which is where the installer writes the database
// path, and an explicitly named content root must still win over that default.
public class ContentRootResolverTests(InstalledAppFixture installed) : IClassFixture<InstalledAppFixture>
{
    private readonly InstalledAppFixture _installed = installed;

    [Fact]
    public async Task Start_FromAnotherFolder_ReadsTheSettingsBesideTheProgram()
    {
        // The folder it is started from holds settings of its own, which must not be the ones read.
        var startFolder = _installed.NewFolder("start");
        var startFolderDatabasePath = Path.Combine(startFolder, "data", "collattice.db");

        InstalledAppFixture.WriteSettings(Path.Combine(startFolder, "appsettings.json"), startFolderDatabasePath);

        var startup = await InstalledAppFixture.StartAsync(_installed.StartInfo(startFolder, "Production"));

        // The configured path is the one used, and the per-user default is never created beside it. On
        // macOS and Linux the default would be under the home folder the fixture gives the program. A new
        // database has nothing to back up before its first migration, so no backup is left beside it.
        var observed =
        (
            Normalize(startup.ContentRoot),
            File.Exists(_installed.DatabasePath),
            File.Exists(startFolderDatabasePath),
            Directory.Exists(Path.Combine(_installed.HomeFolder, ".collattice")),
            Directory.GetFiles(Path.GetDirectoryName(_installed.DatabasePath)!, "collattice.db.bak-*").Length
        );

        observed.ShouldBe((Normalize(_installed.ProgramFolder), true, false, false, 0), startup.Errors);
    }

    [Theory]
    [InlineData("ASPNETCORE_CONTENTROOT")]
    [InlineData("DOTNET_CONTENTROOT")]
    [InlineData("--contentRoot")]
    [InlineData("--contentroot=")]
    public async Task Start_ContentRootNamedExplicitly_ThatFolderWins(string form)
    {
        var startFolder = _installed.NewFolder("start");
        var namedFolder = _installed.NewFolder("named");
        var namedDatabasePath = Path.Combine(namedFolder, "data", "collattice.db");

        InstalledAppFixture.WriteSettings(Path.Combine(namedFolder, "appsettings.json"), namedDatabasePath);

        var startInfo = form switch
        {
            "--contentRoot" => _installed.StartInfo(startFolder, "Production", form, namedFolder),
            "--contentroot=" => _installed.StartInfo(startFolder, "Production", form + namedFolder),
            _ => _installed.StartInfo(startFolder, "Production"),
        };

        if (form.EndsWith("_CONTENTROOT", StringComparison.Ordinal))
        {
            startInfo.Environment[form] = namedFolder;
        }

        var startup = await InstalledAppFixture.StartAsync(startInfo);

        var observed = (Normalize(startup.ContentRoot), File.Exists(namedDatabasePath));

        observed.ShouldBe((Normalize(namedFolder), true), startup.Errors);
    }

    [Fact]
    public async Task ResetAdminKey_FromAnotherFolder_ChangesTheDatabaseTheSettingsBesideTheProgramName()
    {
        await CreateDatabaseWithAdministratorAsync(_installed.KeyResetDatabasePath);

        var startFolder = _installed.NewFolder("start");
        var startInfo = _installed.StartInfo(startFolder, InstalledAppFixture.KeyResetEnvironment, "--reset-admin-key");

        var run = await InstalledAppFixture.RunToExitAsync(startInfo);

        var observed = (run.ExitCode, run.Output.Contains($"reset-admin-key: database {_installed.KeyResetDatabasePath}", StringComparison.Ordinal));

        observed.ShouldBe((AdminKeyResetCli.ExitOk, true), run.Errors);
    }

    private static async Task CreateDatabaseWithAdministratorAsync(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        var options = new DbContextOptionsBuilder<BoardDbContext>().UseSqlite($"Data Source={databasePath}").Options;

        await using (var db = new BoardDbContext(options))
        {
            await db.Database.MigrateAsync();

            db.Users.Add(new BoardUser { Id = Guid.NewGuid(), Name = "Admin", AuthKey = "01KAAAAAAAAAAAAAAAAAAAAAAA", Role = UserRole.Administrator });

            await db.SaveChangesAsync();
        }

        SqliteConnection.ClearAllPools();
    }

    // Paths compare as the file system does: case-insensitively on Windows, with no trailing separator.
    private static string? Normalize(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }
}
