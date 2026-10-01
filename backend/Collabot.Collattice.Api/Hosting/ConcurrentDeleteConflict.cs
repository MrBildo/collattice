using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Hosting;

// A write can check that the card, lane, label or size it refers to exists, and then lose it to a
// delete that commits before the write does. The database refuses the write, either because a
// foreign key now points at nothing or because the row being updated or deleted is already gone,
// and before this the caller got a 500 (an unhandled tool error on MCP). Measured against a running
// API, with a delete released together with the writes under it, it happened to comments,
// attachments, card labels, card edits and moves, card and lane creates, and comment edits and
// deletes alike.
//
// Answered here, once for every write path, rather than at each save: the cause is the same
// wherever it happens, and a path added later is covered without remembering to be. The answer
// is a conflict, not a not-found, because only the caller's retry can tell what is gone now; that
// retry gets the endpoint's own precise answer.
//
// Deliberately narrow. Only these two failures are translated, and nothing in the model makes
// either one mean anything else: there are no concurrency tokens, so an update or delete that
// touches no row means the row was deleted, and a foreign-key failure means a referenced row is
// missing. Every other exception still reaches the caller as a 500, and the translated one is
// still logged.
internal static class ConcurrentDeleteConflict
{
    public const string Message = "Something this change refers to was deleted at the same moment. Reload and try again.";

    // SQLITE_CONSTRAINT_FOREIGNKEY. A unique-index failure shares the primary code 19 and is not
    // matched.
    private const int _foreignKeyFailure = 787;

    public static bool Matches(Exception ex) => ex switch
    {
        DbUpdateConcurrencyException => true,
        DbUpdateException { InnerException: SqliteException { SqliteExtendedErrorCode: _foreignKeyFailure } } => true,
        _ => false,
    };

    public static IApplicationBuilder UseConcurrentDeleteConflict(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex) when (Matches(ex) && !context.Response.HasStarted)
            {
                var logger = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(ConcurrentDeleteConflict));

                logger.LogWarning(ex, "A write to {Path} lost to a concurrent delete; answered 409", context.Request.Path);

                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status409Conflict;

                await context.Response.WriteAsJsonAsync(Message, context.RequestAborted);
            }
        });
}
