namespace Collabot.Collattice.Api.Tests.Infrastructure;

// For tests of what the app does on macOS and Linux only: file modes, and a home folder taken from HOME.
// Windows has neither, and its per-user folder can't be pointed elsewhere for one process, so these
// run on the Linux CI runner and are skipped on Windows.
//
// Sealed because the constructor is the whole rule: a derived attribute could set Skip again after it
// runs, and a test marked with it would run where this attribute's name says it doesn't.
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
//
// Sealed for the same reason as UnixFactAttribute: a derived attribute could undo the skip.
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

// For tests of what the app checks on Linux only: folder owners, groups and access rules (ACLs). On
// macOS the app reads none of them, and the CI runner is Linux. Unprivileged, because the app accepts
// what root owns when it runs as root, and a test of "another user's folder" uses one root owns.
//
// Sealed for the same reason as UnixFactAttribute: a derived attribute could undo the skip.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class UnprivilegedLinuxFactAttribute : FactAttribute
{
    public UnprivilegedLinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux only.";
        }
        else if (Environment.IsPrivilegedProcess)
        {
            Skip = "Needs a user other than root.";
        }
    }
}

// As UnprivilegedLinuxFact, for a test that gives a folder to a group the running user is in but does
// not have to themselves. A user with only their own group has none to give it to.
//
// Sealed for the same reason as UnixFactAttribute: a derived attribute could undo the skip.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class UnprivilegedLinuxSharedGroupFactAttribute : FactAttribute
{
    public UnprivilegedLinuxSharedGroupFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux only.";
        }
        else if (Environment.IsPrivilegedProcess)
        {
            Skip = "Needs a user other than root.";
        }
        else if (LinuxFolderSharing.SharedGroup is null)
        {
            Skip = "The user is in no group other than their own.";
        }
    }
}
