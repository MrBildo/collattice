namespace Collabot.Collattice.Api.Hosting;

// The content root is the folder the host reads appsettings.json and the web files (wwwroot) from.
// ASP.NET Core defaults it to the process's current directory, so an installed program started from
// any other folder (the home folder, or found on PATH) reads neither the settings nor the web files
// that sit beside it, and stops at startup asking for a database path its own appsettings.json holds.
// This makes the program's own folder the default instead.
//
// Only the default changes. WebApplicationOptions.ContentRootPath, once set, overrides every other
// source of the content root, so it is set only when none of the standard ones names it:
// ASPNETCORE_CONTENTROOT, DOTNET_CONTENTROOT or --contentRoot still win.
internal static class ContentRootResolver
{
    public static WebApplicationOptions CreateBuilderOptions(string[] args) => new()
    {
        Args = args,
        ContentRootPath = IsNamedExplicitly(args) ? null : AppContext.BaseDirectory,
    };

    // Reads the content-root key through the providers the host itself reads it from, so every
    // spelling the host accepts (either prefix, --contentRoot value or --contentRoot=value, any case)
    // counts as named here too.
    internal static bool IsNamedExplicitly(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables(prefix: "ASPNETCORE_")
            .AddEnvironmentVariables(prefix: "DOTNET_")
            .AddCommandLine(args)
            .Build();

        return configuration[HostDefaults.ContentRootKey] is not null;
    }
}
