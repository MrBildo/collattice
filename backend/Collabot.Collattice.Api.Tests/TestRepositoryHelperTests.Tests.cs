using Collabot.Collattice.Api.Tests.Infrastructure;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The source-reading tests in ConfigPrecedenceTests and AppSettingsMergeCliTests trust
// TestRepositoryHelper to hand them the checkout under test. CI runs on a normal clone, where
// the old directory-only walk also happened to work, so these build the layouts on disk and
// pin the walk itself -- including a git worktree nested inside the main checkout, the layout
// in which a directory-only walk escapes to the enclosing checkout and silently reads its files.
public class TestRepositoryHelperTests : IDisposable
{
    private readonly string _scratchDir;

    public TestRepositoryHelperTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"collattice-repo-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratchDir))
        {
            try
            {
                Directory.Delete(_scratchDir, true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void FindRoot_WorktreeNestedInsideMainCheckout_StopsAtWorktreeRoot()
    {
        var mainCheckout = Path.Combine(_scratchDir, "main");
        Directory.CreateDirectory(Path.Combine(mainCheckout, ".git"));

        var worktree = Path.Combine(mainCheckout, ".claude", "worktrees", "agent-1");
        Directory.CreateDirectory(worktree);
        File.WriteAllText
        (
            Path.Combine(worktree, ".git"),
            $"gitdir: {Path.Combine(mainCheckout, ".git", "worktrees", "agent-1")}\n"
        );

        var testOutput = Path.Combine(worktree, "backend", "Some.Tests", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(testOutput);

        TestRepositoryHelper.FindRoot(testOutput).ShouldBe(worktree);
    }

    [Fact]
    public void FindRoot_NormalClone_StopsAtCloneRoot()
    {
        var clone = Path.Combine(_scratchDir, "clone");
        Directory.CreateDirectory(Path.Combine(clone, ".git"));

        var testOutput = Path.Combine(clone, "backend", "Some.Tests", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(testOutput);

        TestRepositoryHelper.FindRoot(testOutput).ShouldBe(clone);
    }
}
