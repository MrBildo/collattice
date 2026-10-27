namespace Collabot.Collattice.Api.Tests.Infrastructure;

// For tests of what the app does on macOS and Linux only: file modes, and a home folder taken from HOME.
// Windows has neither, and its per-user folder can't be pointed elsewhere for one process, so these
// run on the Linux CI runner and are skipped on Windows.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "macOS and Linux only.";
        }
    }
}

// For tests that take a permission away and expect a refusal: root ignores file permissions, so under
// root they would pass or fail for the wrong reason.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class UnprivilegedUnixFactAttribute : FactAttribute
{
    public UnprivilegedUnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "macOS and Linux only.";
        }
        else if (Environment.IsPrivilegedProcess)
        {
            Skip = "Needs a user that file permissions apply to, not root.";
        }
    }
}
