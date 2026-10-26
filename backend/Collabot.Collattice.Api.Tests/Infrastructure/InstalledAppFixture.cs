using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// A copy of the app laid out the way the installer lays it out: the program's files in one folder,
// with an appsettings.json beside them that names an absolute database path. The tests start it as a
// real process from other folders, because the content root is decided before the host is built and
// an in-process test host always names one explicitly. Copied once per test class, since the program's
// own folder (this test run's output folder) must not be written to.
public class InstalledAppFixture : IDisposable
{
    public const string KeyResetEnvironment = "KeyReset";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"collattice-installed-{Guid.NewGuid():N}");

    public InstalledAppFixture()
    {
        ProgramFolder = Path.Combine(_root, "install");
        DatabasePath = Path.Combine(ProgramFolder, "data", "collattice.db");
        KeyResetDatabasePath = Path.Combine(ProgramFolder, "data", "key-reset.db");

        CopyDirectory(AppContext.BaseDirectory, ProgramFolder);

        // What the installer does on a first install: the shipped settings, plus the database path.
        var appsettingsPath = Path.Combine(ProgramFolder, "appsettings.json");
        var appsettings = JsonNode.Parse(File.ReadAllText(appsettingsPath))!.AsObject();
        appsettings["ConnectionStrings"] = new JsonObject { ["Board"] = $"Data Source={DatabasePath}" };
        File.WriteAllText(appsettingsPath, appsettings.ToJsonString());

        // The key-reset test runs under its own environment name, so it reads its own database from
        // a settings file beside the program and never shares one with the tests that start the app.
        WriteSettings(Path.Combine(ProgramFolder, $"appsettings.{KeyResetEnvironment}.json"), KeyResetDatabasePath);
    }

    public string ProgramFolder { get; }

    public string DatabasePath { get; }

    public string KeyResetDatabasePath { get; }

    public string ProgramPath => Path.Combine(ProgramFolder, "Collabot.Collattice.Api.dll");

    public string NewFolder(string name)
    {
        var folder = Path.Combine(_root, $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        return folder;
    }

    public static void WriteSettings(string path, string databasePath)
    {
        var settings = new JsonObject { ["ConnectionStrings"] = new JsonObject { ["Board"] = $"Data Source={databasePath}" } };

        File.WriteAllText(path, settings.ToJsonString());
    }

    // Runs the program with `dotnet`, from startFolder. Everything in this test run's environment
    // that could name the content root, the environment or a setting under test is removed first, so
    // only what the test sets reaches the program.
    public ProcessStartInfo StartInfo(string startFolder, string environmentName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = startFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add(ProgramPath);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        string[] inheritedPrefixes = ["ConnectionStrings__", "Hosting__", "Admin__"];
        string[] inheritedNames =
        [
            "ASPNETCORE_CONTENTROOT",
            "DOTNET_CONTENTROOT",
            "ASPNETCORE_ENVIRONMENT",
            "DOTNET_ENVIRONMENT",
            "ASPNETCORE_URLS",
            "DOTNET_URLS",
            "OTEL_EXPORTER_OTLP_ENDPOINT",
        ];

        var removed = startInfo.Environment.Keys
            .Where(name => inheritedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                || inheritedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                .ToList();

        foreach (var name in removed)
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = environmentName;
        startInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        startInfo.Environment["UpdateCheck__Enabled"] = "false";

        // A real process, not a test host: keep it out of the Windows Application event log.
        startInfo.Environment["Logging__EventLog__LogLevel__Default"] = "None";

        return startInfo;
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
            // A file a stopped process still holds open is left for the OS to clean up.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }

        GC.SuppressFinalize(this);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
