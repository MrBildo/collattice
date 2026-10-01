using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// What a write through one entry point came back with, in a shape both surfaces fill. A REST call
// answers with a status and a body. An MCP tool called in-process returns its result text; a
// database exception that escapes it is captured rather than thrown, so a write path that stops
// answering an exhausted allocator with "try again" reds on the assertion instead of erroring out.
public record WriteOutcome(HttpStatusCode? Status, string? Text, DbUpdateException? Collision)
{
    public bool Succeeded => Collision is null && (Status is { } status
        ? (int)status is >= 200 and < 300
        : Text is not null && !Text.StartsWith("Error", StringComparison.Ordinal));

    public static async Task<WriteOutcome> FromResponseAsync(HttpResponseMessage response) =>
        new(response.StatusCode, await response.Content.ReadAsStringAsync(), null);

    public static async Task<WriteOutcome> FromToolAsync(Func<Task<string>> callTool)
    {
        try
        {
            return new WriteOutcome(null, await callTool(), null);
        }
        catch (DbUpdateException ex)
        {
            return new WriteOutcome(null, null, ex);
        }
    }


    // An exhausted allocator that tells the caller to try again: a 409 over REST and an error result
    // over MCP, each carrying the reason, and never the database's own collision.
    public void ShouldHaveAskedToTryAgain(string reason)
    {
        Collision.ShouldBeNull();

        if (Status is { } status)
        {
            status.ShouldBe(HttpStatusCode.Conflict);
        }

        Text.ShouldNotBeNull().ShouldContain(reason);
    }
}
