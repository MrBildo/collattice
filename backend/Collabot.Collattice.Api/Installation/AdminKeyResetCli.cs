using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Collabot.Collattice.Api.Installation;

// Gives an Administrator a new auth key and prints it once, to the terminal that ran the command.
// The app logs an administrator's key only when it generates one on first setup, so this is how an
// operator recovers a lost key or rotates one that has leaked. It runs before any host is built and
// its database context logs nothing, so the key never reaches the app log or any log sink. Running it
// takes write access to the database file, which already holds every key, so it grants nothing new.
public static class AdminKeyResetCli
{
    public const int ExitOk = 0;
    public const int ExitUsage = 2;
    public const int ExitDatabaseUnavailable = 3;
    public const int ExitNoAdministrator = 4;
    public const int ExitAdministratorAmbiguous = 5;

    private const string _usage = "usage: Collabot.Collattice.Api --reset-admin-key [<administrator-user-id>]";

    public static Task<int> RunAsync
    (
        string[] args,
        IConfiguration configuration,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct = default
    ) => RunAsync(args, configuration, DatabaseLocation.UserDataFolder, stdout, stderr, ct);

    // userDataFolder is the base of the default database location, read only when no path is configured.
    internal static async Task<int> RunAsync
    (
        string[] args,
        IConfiguration configuration,
        Func<string?> userDataFolder,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(userDataFolder);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (!TryParseArgs(args, out var requestedUserId))
        {
            await stderr.WriteLineAsync(_usage);
            return ExitUsage;
        }

        if (!TryResolveDatabase(configuration, userDataFolder, stderr, out var databasePath, out var connectionString))
        {
            return ExitDatabaseUnavailable;
        }

        // The app's own context, so table and column names come from the one model, with logging
        // switched off rather than left to a default.
        var options = new DbContextOptionsBuilder<BoardDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(SqliteBusyTimeoutInterceptor.Instance)
            .UseLoggerFactory(NullLoggerFactory.Instance)
            .Options;

        await using var db = new BoardDbContext(options);

        try
        {
            // Only active administrators: a deactivated user's key is refused whatever it is.
            var administrators = await db.Users
                .Where(user => user.Role == UserRole.Administrator && user.IsActive)
                .OrderBy(user => user.Name)
                    .Select(user => new AdministratorRow(user.Id, user.Name))
                        .ToListAsync(ct);

            if (!TryChooseAdministrator(administrators, requestedUserId, stderr, out var administrator, out var exitCode))
            {
                return exitCode;
            }

            var newKey = Ulid.NewUlid().ToString(null, CultureInfo.InvariantCulture);

            // The role and active checks are repeated in the UPDATE itself, so an account deactivated
            // or demoted since the read above is left alone instead of being handed a key.
            var targetRow = db.Users.Where(user => user.Id == administrator.Id && user.Role == UserRole.Administrator && user.IsActive);
            var updated = await targetRow.ExecuteUpdateAsync(setters => setters.SetProperty(user => user.AuthKey, newKey), ct);

            if (updated != 1)
            {
                await stderr.WriteLineAsync($"reset-admin-key: the Administrator '{administrator.Name}' ({administrator.Id}) changed while the key was being reset; nothing was written. Run the command again.");
                return ExitNoAdministrator;
            }

            await stdout.WriteLineAsync($"reset-admin-key: database {databasePath}");
            await stdout.WriteLineAsync($"reset-admin-key: new auth key for Administrator '{administrator.Name}' ({administrator.Id}):");
            await stdout.WriteLineAsync();
            await stdout.WriteLineAsync(newKey);
            await stdout.WriteLineAsync();
            await stdout.WriteLineAsync("The previous key no longer works, and the running app accepts the new one now; no restart is needed.");
            await stdout.WriteLineAsync("This is the only place the new key is shown. It is not written to the app log, so store it now.");

            return ExitOk;
        }
        catch (SqliteException ex)
        {
            await stderr.WriteLineAsync($"reset-admin-key: could not reset the key in {databasePath}: {ex.Message}");
            return ExitDatabaseUnavailable;
        }
    }

    private static bool TryParseArgs(string[] args, out Guid? requestedUserId)
    {
        requestedUserId = null;

        if (args.Length == 0)
        {
            return true;
        }

        if (args.Length == 1 && Guid.TryParse(args[0], out var userId))
        {
            requestedUserId = userId;
            return true;
        }

        return false;
    }

    private static bool TryResolveDatabase
    (
        IConfiguration configuration,
        Func<string?> userDataFolder,
        TextWriter stderr,
        out string databasePath,
        out string connectionString
    )
    {
        databasePath = string.Empty;
        connectionString = string.Empty;

        // The app's own resolver, so the command names the database the app would open: the configured
        // one, or the default when none is configured. Run with different configuration than the app's,
        // or as a different user, and this names the wrong database, so it prints the one it used.
        const string sameConfiguration = "Run the command as the user the app runs as, with the configuration the app runs with: "
            + "the environment variables its service sets (ConnectionStrings__Board in particular).";

        if (!DatabaseLocation.TryResolve(configuration, userDataFolder, out var database, out var error))
        {
            stderr.WriteLine($"reset-admin-key: {error} {sameConfiguration}");
            return false;
        }

        if (database.FilePath is not { } filePath)
        {
            stderr.WriteLine($"reset-admin-key: '{DatabaseLocation.SettingKey}' names an in-memory or temporary database, not a database file. {sameConfiguration}");
            return false;
        }

        // The checks are read-only: nothing here creates the default folder.
        if (!DatabaseLocation.TryCheckDefaultFolder(database, out error))
        {
            stderr.WriteLine($"reset-admin-key: {error}");
            return false;
        }

        if (!File.Exists(filePath))
        {
            stderr.WriteLine($"reset-admin-key: there is no {DatabaseLocation.Describe(database)}. {sameConfiguration}");
            return false;
        }

        // Never create a database: a mistyped path must fail here, not leave an empty file behind.
        databasePath = filePath;
        connectionString = new SqliteConnectionStringBuilder(database.ConnectionString) { Mode = SqliteOpenMode.ReadWrite }.ToString();

        return true;
    }

    // With no id the choice must be unambiguous: one active Administrator, or nothing is changed and
    // the candidates are listed so the operator can name one.
    private static bool TryChooseAdministrator
    (
        List<AdministratorRow> administrators,
        Guid? requestedUserId,
        TextWriter stderr,
        [NotNullWhen(true)] out AdministratorRow? administrator,
        out int exitCode
    )
    {
        administrator = null;
        exitCode = ExitOk;

        if (requestedUserId is { } userId)
        {
            administrator = administrators.Find(candidate => candidate.Id == userId);
            if (administrator is not null)
            {
                return true;
            }

            stderr.WriteLine($"reset-admin-key: {userId} is not an active Administrator; no key was changed.");
            WriteAdministrators(administrators, stderr);
            exitCode = ExitNoAdministrator;
            return false;
        }

        if (administrators.Count == 0)
        {
            stderr.WriteLine("reset-admin-key: the database has no active Administrator; no key was changed.");
            exitCode = ExitNoAdministrator;
            return false;
        }

        if (administrators.Count > 1)
        {
            stderr.WriteLine
            (
                string.Create
                (
                    CultureInfo.InvariantCulture,
                    $"reset-admin-key: the database has {administrators.Count} active Administrators; name the one to reset by its id. No key was changed."
                )
            );
            WriteAdministrators(administrators, stderr);
            stderr.WriteLine(_usage);
            exitCode = ExitAdministratorAmbiguous;
            return false;
        }

        administrator = administrators[0];
        return true;
    }

    private static void WriteAdministrators(List<AdministratorRow> administrators, TextWriter stderr)
    {
        if (administrators.Count == 0)
        {
            stderr.WriteLine("  (no active Administrators)");
            return;
        }

        stderr.WriteLine("Active Administrators:");

        foreach (var administrator in administrators)
        {
            stderr.WriteLine($"  {administrator.Id}  {administrator.Name}");
        }
    }

    private sealed record AdministratorRow(Guid Id, string Name);
}
