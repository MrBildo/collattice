using Collabot.Collattice.Api.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Which failures REST and MCP answer as a write lost to a concurrent delete. Only two do; a unique
// collision shares SQLite's primary code 19 with a foreign-key failure and must still reach its own
// handling (or the caller) as itself.
public class ConcurrentDeleteConflictTests
{
    [Fact]
    public void Matches_ForeignKeyFailure_ReturnsTrue()
    {
        var ex = new DbUpdateException("save failed", new SqliteException("FOREIGN KEY constraint failed", 19, 787));

        ConcurrentDeleteConflict.Matches(ex).ShouldBeTrue();
    }

    [Fact]
    public void Matches_RowAlreadyGone_ReturnsTrue()
    {
        var ex = new DbUpdateConcurrencyException("expected to affect 1 row, affected 0");

        ConcurrentDeleteConflict.Matches(ex).ShouldBeTrue();
    }

    [Fact]
    public void Matches_UniqueCollision_ReturnsFalse()
    {
        var ex = new DbUpdateException("save failed", new SqliteException("UNIQUE constraint failed", 19, 2067));

        ConcurrentDeleteConflict.Matches(ex).ShouldBeFalse();
    }

    [Fact]
    public void Matches_AnyOtherFailure_ReturnsFalse() =>
        ConcurrentDeleteConflict.Matches(new InvalidOperationException("something else")).ShouldBeFalse();
}
