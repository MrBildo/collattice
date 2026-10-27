using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;

namespace Collabot.Collattice.Api.Persistence;

// Decides where the board's SQLite database lives, for the app and for --reset-admin-key alike, so the
// two can never name different files.
//
// A configured 'ConnectionStrings:Board' always wins. With nothing configured, the database is
// 'collattice.db' in a per-user folder: ~/.collattice on macOS and Linux, %LOCALAPPDATA%\Collattice on
// Windows. That is the folder the one-line installers install into, but never the database they set up
// there (theirs is data/collattice.db), so a manually downloaded build of another version can't open,
// or upgrade, an installed board's data.
//
// The base folder comes from the operating system, never from a path worked out from the working or
// program folder: a hosted install's program folder can be read-only, and the working folder is
// wherever the program happened to be started. The base folder must be an absolute path, and the
// database location then goes through the same checks as a configured one.
internal static class DatabaseLocation
{
    public const string SettingKey = "ConnectionStrings:Board";
    public const string EnvironmentVariable = "ConnectionStrings__Board";
    public const string DefaultFileName = "collattice.db";

    // Owner-only. The database holds every user's auth key in plain text, and SQLite creates its -wal
    // and -shm files with the database file's own mode, so a file created this way keeps them owner-only
    // too.
    private const UnixFileMode _ownerOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode _ownerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static string DefaultFolderName => OperatingSystem.IsWindows() ? "Collattice" : ".collattice";

    // The operating system's own record of the user's base folder: the Local AppData known folder on
    // Windows, the home folder on macOS and Linux. Not .NET's local-application-data folder on Linux,
    // which is ~/.local/share. Empty when the system has none, or when it does not exist.
    public static string UserDataFolder()
    {
        var folder = OperatingSystem.IsWindows() ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.UserProfile;

        return Environment.GetFolderPath(folder);
    }

    public static bool TryResolve
    (
        IConfiguration configuration,
        [NotNullWhen(true)] out ResolvedDatabase? database,
        [NotNullWhen(false)] out string? error
    ) => TryResolve(configuration, UserDataFolder, out database, out error);

    // The user folder is read only when no path is configured, so a configured install never depends
    // on, or looks at, the user's home folder.
    internal static bool TryResolve
    (
        IConfiguration configuration,
        Func<string?> userDataFolder,
        [NotNullWhen(true)] out ResolvedDatabase? database,
        [NotNullWhen(false)] out string? error
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(userDataFolder);

        database = null;

        var configured = configuration.GetConnectionString("Board");

        if (string.IsNullOrWhiteSpace(configured))
        {
            var baseFolder = userDataFolder();

            if (string.IsNullOrEmpty(baseFolder) || !Path.IsPathFullyQualified(baseFolder))
            {
                error = $"No database path is configured ('{SettingKey}' is not set), and the default location "
                    + "can't be used because the operating system reports no absolute home folder for this user "
                    + $"(it reported '{baseFolder}').";
                return false;
            }

            var defaultPath = Path.Combine(baseFolder, DefaultFolderName, DefaultFileName);
            var connectionString = new SqliteConnectionStringBuilder { DataSource = defaultPath }.ToString();

            database = new ResolvedDatabase(connectionString, defaultPath, IsDefault: true);
            error = null;
            return true;
        }

        string dataSource;

        try
        {
            dataSource = new SqliteConnectionStringBuilder(configured).DataSource;
        }
        catch (ArgumentException ex)
        {
            error = $"'{SettingKey}' is not a valid SQLite connection string: {ex.Message}";
            return false;
        }

        // SQLite's special data sources (':memory:', or empty for a temporary database) have no file.
        if (string.IsNullOrEmpty(dataSource) || dataSource == ":memory:")
        {
            database = new ResolvedDatabase(configured, FilePath: null, IsDefault: false);
            error = null;
            return true;
        }

        if (!Path.IsPathRooted(dataSource))
        {
            error = $"'{SettingKey}' names a relative data source '{dataSource}'. An absolute path is required "
                + "so the database location does not depend on the folder the program is started from.";
            return false;
        }

        database = new ResolvedDatabase(configured, dataSource, IsDefault: false);
        error = null;
        return true;
    }

    // For the app's start: creates the database's folder if it is missing, checks that the folder and
    // the file can be written, and creates an empty database file if there is none, all before SQLite
    // opens it. createdNew says whether this start created the file.
    public static bool TryPrepare
    (
        ResolvedDatabase database,
        out bool createdNew,
        [NotNullWhen(false)] out string? error
    )
    {
        ArgumentNullException.ThrowIfNull(database);

        createdNew = false;

        if (database.FilePath is not { } filePath)
        {
            error = null;
            return true;
        }

        var folder = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(folder))
        {
            error = $"The {Describe(database)} has no folder.";
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // A folder under the user's Local AppData is readable by that user, SYSTEM and
                // Administrators only, by inheritance.
                Directory.CreateDirectory(folder);
            }
            else
            {
                CreateFoldersOwnerOnly(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"The database folder '{folder}' for the {Describe(database)} could not be created: {ex.Message}";
            return false;
        }

        // The default folder is checked after creating it, so a folder someone else created first is
        // checked too.
        return TryCheckDefaultFolder(database, out error)
            && TryCheckFolderWritable(folder, database, out error)
            && TryEnsureFile(filePath, database, out createdNew, out error);
    }

    // The default folder must not be one other users can write to, directly or through a link: with a
    // home folder such as /tmp, another local user could create it first and plant a database with an
    // administrator key of their own. Read-only, so --reset-admin-key can use it too. A configured
    // location passes, since it is the operator's choice, and so does a default folder that does not
    // exist yet. Windows needs no check: the folder is under the user's own profile.
    public static bool TryCheckDefaultFolder(ResolvedDatabase database, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(database);

        error = null;

        var folder = Path.GetDirectoryName(database.FilePath);

        if (!database.IsDefault || OperatingSystem.IsWindows() || string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return true;
        }

        var target = Directory.ResolveLinkTarget(folder, returnFinalTarget: true)?.FullName ?? folder;

        if ((File.GetUnixFileMode(target) & UnixFileMode.OtherWrite) == UnixFileMode.None)
        {
            return true;
        }

        var where = string.Equals(target, folder, StringComparison.Ordinal)
            ? $"The default database folder '{folder}' can be written by other users"
            : $"The default database folder '{folder}' is a link to '{target}', which other users can write to";

        error = $"{where}, so another user could have put a database there. Collattice will not use it. "
            + "Remove other users' write permission from that folder, or configure a database path.";
        return false;
    }

    // What an operator needs to point the app at a database: the setting, in both forms.
    public static string Remedy()
    {
        var example = OperatingSystem.IsWindows()
            ? @"Data Source=C:\collattice\data\collattice.db"
            : "Data Source=/var/lib/collattice/collattice.db";

        var jsonValue = example.Replace(@"\", @"\\", StringComparison.Ordinal);

        return $$"""
            To use a database somewhere else, set '{{SettingKey}}' to an absolute path in either form:

              - Environment variable:
                  {{EnvironmentVariable}}={{example}}

              - appsettings.json (next to the executable; edits survive upgrades via the smart-merge):
                  {
                    "ConnectionStrings": {
                      "Board": "{{jsonValue}}"
                    }
                  }
            """;
    }

    public static string Describe(ResolvedDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        return database.IsDefault
            ? $"default database '{database.FilePath}' (no '{SettingKey}' is configured)"
            : $"database '{database.FilePath}' (from '{SettingKey}')";
    }

    // Every folder this creates is owner-only. Directory.CreateDirectory gives the mode it is handed to
    // the last folder in the path only, and creates any missing folder above it with the default mode,
    // so each missing level is created on its own, from the top down.
    [UnsupportedOSPlatform("windows")]
    private static void CreateFoldersOwnerOnly(string folder)
    {
        var missing = new Stack<string>();
        var current = folder;

        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current);
        }

        while (missing.TryPop(out var next))
        {
            Directory.CreateDirectory(next, _ownerOnlyFolder);
        }
    }

    // A folder that exists can still refuse new files, and SQLite needs to create its -wal and -shm
    // files beside the database, so the check is to create one and delete it.
    private static bool TryCheckFolderWritable(string folder, ResolvedDatabase database, [NotNullWhen(false)] out string? error)
    {
        var probePath = Path.Combine(folder, $".collattice-write-check-{Guid.NewGuid():N}");

        try
        {
            File.Open(probePath, CreateOptions(FileOptions.DeleteOnClose)).Dispose();

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"The database folder '{folder}' for the {Describe(database)} is not writable: {ex.Message}";
            return false;
        }
    }

    // An empty file is a valid empty SQLite database. Creating it here, owner-only, means SQLite never
    // creates it with the process's default mode, which on macOS and Linux lets other users read it.
    private static bool TryEnsureFile(string filePath, ResolvedDatabase database, out bool createdNew, [NotNullWhen(false)] out string? error)
    {
        createdNew = false;

        try
        {
            if (!File.Exists(filePath))
            {
                try
                {
                    File.Open(filePath, CreateOptions(FileOptions.None)).Dispose();

                    createdNew = true;
                }
                catch (IOException) when (File.Exists(filePath))
                {
                    // Created by another process between the check and the create: use it as found.
                }
            }

            // An existing file must be writable as well as its folder.
            File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete).Dispose();

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Collattice can't write to the {Describe(database)}: {ex.Message}";
            return false;
        }
    }

    private static FileStreamOptions CreateOptions(FileOptions options)
    {
        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = options,
        };

        if (!OperatingSystem.IsWindows())
        {
            streamOptions.UnixCreateMode = _ownerOnlyFile;
        }

        return streamOptions;
    }
}

// Where the database is, as the connection string EF opens. FilePath is null for SQLite's special data
// sources (an in-memory database), which have no file.
internal sealed record ResolvedDatabase(string ConnectionString, string? FilePath, bool IsDefault);
