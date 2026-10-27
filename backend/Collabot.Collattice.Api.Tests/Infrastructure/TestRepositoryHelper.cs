using Shouldly;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

public static class TestRepositoryHelper
{
    // Source-reading tests locate the checkout they were built from by walking up to the
    // nearest `.git` entry. In a normal clone `.git` is a directory, but in a git worktree it
    // is a file pointing back at the main repository — so the walk must stop at either form.
    // Accepting only the directory form walks past the worktree root into whatever checkout
    // encloses it, and the tests then read (and vouch for) a different tree's source.
    public static string FindRoot() => FindRoot(AppContext.BaseDirectory);

    public static string FindRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null && !IsRepositoryRoot(dir.FullName))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Could not locate repo root (.git) from test base dir");
        return dir.FullName;
    }

    private static bool IsRepositoryRoot(string directory)
    {
        var gitEntry = Path.Combine(directory, ".git");

        return Directory.Exists(gitEntry) || File.Exists(gitEntry);
    }
}
