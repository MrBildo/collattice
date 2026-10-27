using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// A test host over a database file that outlives the host. Disposing one host and building another
// on the same path is an API restart against the data the first one left, which is how a test sees
// what a hosted service does at boot. Every context opens its own connection to the file, so a
// running hosted service can do database work alongside the test thread. The test owns the file and
// removes it with DeleteDatabaseFiles once its last host is disposed.
public class PersistentDatabaseFactory(string databasePath) : CollatticeApiFactory
{
    private readonly string _databasePath = databasePath;

    public static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"collattice-restart-{Guid.NewGuid():N}.db");

    public static void DeleteDatabaseFiles(string databasePath)
    {
        // Close the pooled connections first; an open one keeps the file locked on Windows.
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            SqliteConnection.ClearPool(connection);
        }

        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A temp file still held open is left for the OS to clean up; it is never read again.
            }
        }
    }

    protected override void UseTestDatabase(DbContextOptionsBuilder options) =>
        options.UseSqlite($"Data Source={_databasePath}");
}
