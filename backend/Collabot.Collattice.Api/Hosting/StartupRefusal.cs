namespace Collabot.Collattice.Api.Hosting;

// A start the app refuses on purpose (a database location it can't use, a database a newer version has
// upgraded) ends with its message on standard error and this exit code. It is not thrown: an unhandled
// exception would add a stack trace, and on macOS and Linux a crash dump, to a refusal that already says
// what is wrong and what to change. Unexpected failures still end as unhandled exceptions.
internal static class StartupRefusal
{
    public const int ExitCode = 1;

    public static int Refuse(TextWriter stderr, string message)
    {
        ArgumentNullException.ThrowIfNull(stderr);

        stderr.WriteLine(message);
        stderr.Flush();

        return ExitCode;
    }
}
