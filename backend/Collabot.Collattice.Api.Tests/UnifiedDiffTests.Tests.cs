using System.Globalization;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// UnifiedDiff takes no DbContext and touches no database, so it is tested directly per the
// pure-function carve-out. The rendered string is a published API contract, so these assert the
// exact bytes rather than "contains a plus sign" — a format drift has to fail here.
public class UnifiedDiffTests
{
    [Fact]
    public void Render_IdenticalTexts_ReturnsEmptyString()
    {
        var diff = UnifiedDiff.Render("alpha\nbeta", "alpha\nbeta");

        diff.ShouldBe(string.Empty);
    }

    [Fact]
    public void Render_SingleLineReplacement_EmitsRemovedAndAddedLines()
    {
        var diff = UnifiedDiff.Render("alpha", "beta");

        diff.ShouldBe("@@ -1,1 +1,1 @@\n-alpha\n+beta\n");
    }

    [Fact]
    public void Render_FromEmptyText_UsesGitEmptyRangeConventionForTheOldSide()
    {
        var diff = UnifiedDiff.Render(string.Empty, "first line");

        // "-0,0" and not "-1,0": git points an empty range at the line it follows.
        diff.ShouldBe("@@ -0,0 +1,1 @@\n+first line\n");
    }

    [Fact]
    public void Render_ToEmptyText_UsesGitEmptyRangeConventionForTheNewSide()
    {
        var diff = UnifiedDiff.Render("only line", string.Empty);

        diff.ShouldBe("@@ -1,1 +0,0 @@\n-only line\n");
    }

    [Fact]
    public void Render_ChangeInsideLongText_QuotesThreeLinesOfContextEachSide()
    {
        var original = string.Join('\n', Enumerable.Range(1, 10).Select(n => string.Create(CultureInfo.InvariantCulture, $"line {n}")));
        var edited = original.Replace("line 5", "line five", StringComparison.Ordinal);

        var diff = UnifiedDiff.Render(original, edited);

        diff.ShouldBe
        (
            "@@ -2,7 +2,7 @@\n"
            + " line 2\n line 3\n line 4\n"
            + "-line 5\n+line five\n"
            + " line 6\n line 7\n line 8\n"
        );
    }

    [Fact]
    public void Render_TwoDistantChanges_EmitsSeparateHunks()
    {
        var original = string.Join('\n', Enumerable.Range(1, 30).Select(n => string.Create(CultureInfo.InvariantCulture, $"line {n}")));
        var edited = original
            .Replace("line 3\n", "line three\n", StringComparison.Ordinal)
            .Replace("line 27\n", "line twenty-seven\n", StringComparison.Ordinal);

        var diff = UnifiedDiff.Render(original, edited);

        CountHunks(diff).ShouldBe(2);
    }

    [Fact]
    public void Render_NearbyChanges_MergeIntoOneHunk()
    {
        var original = string.Join('\n', Enumerable.Range(1, 30).Select(n => string.Create(CultureInfo.InvariantCulture, $"line {n}")));
        var edited = original
            .Replace("line 10\n", "line ten\n", StringComparison.Ordinal)
            .Replace("line 12\n", "line twelve\n", StringComparison.Ordinal);

        var diff = UnifiedDiff.Render(original, edited);

        // Two changes three lines apart share their context, so quoting them twice would repeat
        // the same lines.
        CountHunks(diff).ShouldBe(1);
    }

    [Fact]
    public void Render_TrailingNewlineOnBothSides_EmitsNoPhantomContextLine()
    {
        var diff = UnifiedDiff.Render("alpha\nbeta\n", "alpha\ngamma\n");

        diff.ShouldBe("@@ -1,2 +1,2 @@\n alpha\n-beta\n+gamma\n");
    }

    [Fact]
    public void Render_TrailingNewlineAddedOnOneSideOnly_StillReportsTheChange()
    {
        // Trimming the final newline unconditionally would make this edit invisible; it is trimmed
        // only when both sides carry one.
        var diff = UnifiedDiff.Render("alpha", "alpha\n");

        diff.ShouldBe("@@ -1,1 +1,2 @@\n alpha\n+\n");
    }

    [Fact]
    public void Render_CarriageReturnsInInput_ProduceLineFeedOnlyOutput()
    {
        var diff = UnifiedDiff.Render("alpha\r\nbeta", "alpha\r\ngamma");

        // The wire format must not vary with the host's line-ending convention.
        diff.ShouldNotContain("\r");
        diff.ShouldBe(UnifiedDiff.Render("alpha\nbeta", "alpha\ngamma"));
    }

    [Fact]
    public void Render_LineEndingOnlyChange_ReturnsEmptyString()
    {
        // Documented consequence of normalizing line endings before diffing: a change that is only
        // CRLF-versus-LF has nothing to show. format=full still returns both values verbatim.
        var diff = UnifiedDiff.Render("alpha\r\nbeta", "alpha\nbeta");

        diff.ShouldBe(string.Empty);
    }

    [Fact]
    public void Render_PureAppend_ReportsOnlyTheAddedLines()
    {
        var diff = UnifiedDiff.Render("alpha\nbeta", "alpha\nbeta\ngamma");

        diff.ShouldBe("@@ -1,2 +1,3 @@\n alpha\n beta\n+gamma\n");
    }

    private static int CountHunks(string diff) =>
        diff.Split('\n').Count(line => line.StartsWith("@@ ", StringComparison.Ordinal));
}
