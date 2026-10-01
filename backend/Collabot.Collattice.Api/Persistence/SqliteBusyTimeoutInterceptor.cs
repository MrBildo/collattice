using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Collabot.Collattice.Api.Persistence;

// Sets SQLite's busy timeout on every connection the app opens. The setting belongs to a
// connection, not to the database file, so running it once at startup reached only the one pooled
// connection that ran it (measured: 1 of 16 pooled connections, serving about 5% of opens under
// contention). Opening a pooled connection again re-runs it, which costs nothing measurable.
//
// What it changes: a save that finds the write lock held now waits inside SQLite, which re-checks
// the lock within milliseconds, instead of in the SQLite provider's own loop, which re-checks it
// only every 150 ms. Correctness does not depend on it: the provider keeps retrying until its
// command timeout (30 s by default) either way, so a held lock never fails a save early. If
// SQLite's own wait runs out, the provider's loop takes over again, so the overall bound is
// unchanged.
//
// Measured through the API with requests released together on one board or card, 8 to 32 at once:
// contended writes got several times faster (32 description edits of one card at once, median:
// 4.4 s down to 1.6 s on a standalone server, 1.3-1.6 s down to 0.27 s in-process), no more
// requests failed on any of the three retrying allocators, and an uncontended request took the
// same time (median 3.9 ms either way).
//
// sealed: a leaf interceptor; no subtype hierarchy is intended.
internal sealed class SqliteBusyTimeoutInterceptor : DbConnectionInterceptor
{
    public const int BusyTimeoutMilliseconds = 5000;

    // One shared instance: it holds no state, and every context gets the same options shape.
    public static readonly SqliteBusyTimeoutInterceptor Instance = new();

    private static readonly string _pragma = string.Create
    (
        CultureInfo.InvariantCulture,
        $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};"
    );

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = _pragma;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync
    (
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = _pragma;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
