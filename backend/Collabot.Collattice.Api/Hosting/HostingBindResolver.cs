using System.Globalization;

namespace Collabot.Collattice.Api.Hosting;

internal static class HostingBindResolver
{
    // Returns the bind URL the caller should pass to WebHost.UseUrls(...), or null when
    // the env-var path (`urls` or `ASPNETCORE_URLS`) is already set — in which case the
    // caller does not call UseUrls and Kestrel reads the env var directly. The `??`
    // order makes `urls` win over `ASPNETCORE_URLS`; reversing it would silently flip
    // .NET-conventional precedence (see Resolve_BothEnvVarsSet_UrlsWins test).
    public static string? Resolve(IConfiguration configuration)
    {
        var configuredUrls = configuration["urls"]
            ?? configuration["ASPNETCORE_URLS"];

        if (!string.IsNullOrWhiteSpace(configuredUrls))
        {
            return null;
        }

        var hosting = configuration
            .GetSection(HostingSettings.SectionName)
            .Get<HostingSettings>() ?? new HostingSettings();

        return $"http://{hosting.ListenAddress}:{hosting.ListenPort.ToString(CultureInfo.InvariantCulture)}";
    }
}
