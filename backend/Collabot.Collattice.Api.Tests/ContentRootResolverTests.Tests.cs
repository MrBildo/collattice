using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// An installed program is often started from some other folder: the home folder, or found on PATH.
// It must still read the appsettings.json beside it, which is where the installer writes the database
// path, and an explicitly named content root must still win over that default.
public class ContentRootResolverTests(InstalledAppFixture installed) : IClassFixture<InstalledAppFixture>
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private readonly InstalledAppFixture _installed = installed;

    [Fact]
    public async Task Start_FromAnotherFolder_ReadsTheSettingsBesideTheProgram()
    {
        // The folder it is started from holds settings of its own, which must not be the ones read.
        var startFolder = _installed.NewFolder("start");
        var startFolderDatabasePath = Path.Combine(startFolder, "data", "collattice.db");

        InstalledAppFixture.WriteSettings(Path.Combine(startFolder, "appsettings.json"), startFolderDatabasePath);

        var startup = await StartAsync(_installed.StartInfo(startFolder, "Production"));

        var observed = (Normalize(startup.ContentRoot), File.Exists(_installed.DatabasePath), File.Exists(startFolderDatabasePath));

        observed.ShouldBe((Normalize(_installed.ProgramFolder), true, false), startup.Errors);
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

        var startup = await StartAsync(startInfo);

        var observed = (Normalize(startup.ContentRoot), File.Exists(namedDatabasePath));

        observed.ShouldBe((Normalize(namedFolder), true), startup.Errors);
    }

    [Fact]
    public async Task ResetAdminKey_FromAnotherFolder_ChangesTheDatabaseTheSettingsBesideTheProgramName()
    {
        await CreateDatabaseWithAdministratorAsync(_installed.KeyResetDatabasePath);

        var startFolder = _installed.NewFolder("start");
        var startInfo = _installed.StartInfo(startFolder, InstalledAppFixture.KeyResetEnvironment, "--reset-admin-key");

        using var cts = new CancellationTokenSource(_timeout);
        using var process = Process.Start(startInfo)!;

        var output = process.StandardOutput.ReadToEndAsync(cts.Token);
        var errors = process.StandardError.ReadToEndAsync(cts.Token);

        await process.WaitForExitAsync(cts.Token);

        var observed = (process.ExitCode, (await output).Contains($"reset-admin-key: database {_installed.KeyResetDatabasePath}", StringComparison.Ordinal));

        observed.ShouldBe((AdminKeyResetCli.ExitOk, true), await errors);
    }

    // Starts the program, waits for the host's "Content root path:" line (written once the database is
    // migrated and the server is listening), then stops it. A program that stops at startup instead
    // reports no content root, and its error output says why.
    private static async Task<Startup> StartAsync(ProcessStartInfo startInfo)
    {
        using var cts = new CancellationTokenSource(_timeout);
        using var process = Process.Start(startInfo)!;

        var errors = process.StandardError.ReadToEndAsync(cts.Token);

        string? contentRoot = null;

        try
        {
            while (await process.StandardOutput.ReadLineAsync(cts.Token) is { } line)
            {
                const string marker = "Content root path:";
                var index = line.IndexOf(marker, StringComparison.Ordinal);

                if (index >= 0)
                {
                    contentRoot = line[(index + marker.Length)..].Trim();
                    break;
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
        }

        return new Startup(contentRoot, await errors);
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

    private sealed record Startup(string? ContentRoot, string Errors);
}
