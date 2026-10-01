using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// What a write through one entry point came back with, in a shape both surfaces fill. A REST call
// answers with a status. An MCP tool called in-process either returns its result text or lets the
// database's own exception out, which is what an exhausted allocator does: the last attempt runs
// outside the retry's catch on purpose.
public record WriteOutcome(HttpStatusCode? Status, string? ToolResult, DbUpdateException? Collision)
{
    public bool Succeeded => Collision is null && (Status is { } status
        ? (int)status is >= 200 and < 300
        : ToolResult is not null && !ToolResult.StartsWith("Error", StringComparison.Ordinal));

    public static WriteOutcome FromResponse(HttpResponseMessage response) => new(response.StatusCode, null, null);

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

    // An exhausted allocator fails the request with the collision itself. Over REST that is an
    // unhandled 500; in-process the exception is visible, so it is held to naming the index that was
    // contended rather than accepted as any database failure.
    public void ShouldHaveFailedOnCollision(string contendedIndex)
    {
        if (Status is { } status)
        {
            status.ShouldBe(HttpStatusCode.InternalServerError);
            return;
        }

        Collision.ShouldNotBeNull();
        Collision.InnerException.ShouldNotBeNull().Message.ShouldContain(contendedIndex);
    }
}
