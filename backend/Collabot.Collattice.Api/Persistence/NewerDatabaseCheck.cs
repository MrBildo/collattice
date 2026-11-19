using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Persistence;

// A database records every migration applied to it. One this build doesn't know means a newer version
// of Collattice has upgraded the database, and this version doesn't know the shape it is in now: EF
// would migrate nothing and carry on, and the app would read and write tables it doesn't understand.
// So the app refuses to start on it, before taking a backup or applying anything.
internal static class NewerDatabaseCheck
{
    // Null when every migration the database records is one this build knows (an older or a current
    // database), otherwise the message to refuse the start with.
    public static async Task<string?> FindRefusalAsync(BoardDbContext db, ResolvedDatabase database, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(database);

        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = await db.Database.GetAppliedMigrationsAsync(ct);

        var unknown = applied
            .Where(migration => !known.Contains(migration))
                .ToList();

        if (unknown.Count == 0)
        {
            return null;
        }

        var backupName = database.FilePath is { } filePath ? $"'{Path.GetFileName(filePath)}.bak-<date and time>'" : "'<file>.bak-<date and time>'";

        return $"The {DatabaseLocation.Describe(database)} has been upgraded by a newer version of Collattice. "
            + $"It records database changes this version does not know ({string.Join(", ", unknown)}), "
            + "so this version can't safely read or write it, and has left it untouched. "
            + "Run the newer version against it, or point this version at another database. "
            + "To go back to this version, restore the copy Collattice saved beside the database before that upgrade, "
            + $"named {backupName}; anything written since the upgrade is not in it.";
    }
}
