using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// On Windows a host's default logging includes the Event Log provider, which writes every warning
// into the machine's Application event log. One full test run wrote over 700 entries there, so a few
// dozen runs replaced the whole log, including the records Windows keeps when a process crashes. Test
// hosts have no use for it, so every test host factory removes it. The app's own logging is untouched.
internal static class TestHostLogging
{
    public static void RemoveEventLog(ILoggingBuilder logging)
    {
        var eventLog = logging.Services
            .Where(descriptor => descriptor.ImplementationType == typeof(EventLogLoggerProvider))
                .ToList();

        foreach (var descriptor in eventLog)
        {
            logging.Services.Remove(descriptor);
        }
    }
}
