var builder = DistributedApplication.CreateBuilder(args);

// ConnectionStrings:Board is required configuration with no fallback;
// the API hard-fails at startup if it is unset. The orchestrator is the "told
// input" for the dev run: an absolute path anchored on the AppHost's binary
// directory (stable per build, independent of the cwd `aspire start` is invoked
// from) so the dev database location does not depend on where the CLI was run.
var devDataPath = Path.Combine(AppContext.BaseDirectory, "data", "collattice.db");

// The API's endpoint and environment are declared here rather than read from its launch
// profile. The API's Properties/launchSettings.json is a per-developer file that git ignores,
// so on a fresh clone there is no profile to read and the resource would have no endpoint at
// all. Passing a null profile name also stops a developer's own launch profile (still used by
// a standalone `dotnet run`) from taking over the endpoints when one is present, so the dev
// run is the same on every machine rather than shaped by whichever local file exists. Without
// a profile nothing else names the environment, and the API would start as Production and
// drop its development-only surface (OpenAPI, open CORS).
var api = builder.AddProject<Projects.Collabot_Collattice_Api>("api", launchProfileName: null)
    .WithHttpEndpoint()
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("ConnectionStrings__Board", $"Data Source={devDataPath}")
    .WithHttpHealthCheck("/health");

builder.AddViteApp("frontend", "../../frontend")
    .WithExternalHttpEndpoints()
    .WithReference(api)
    .WaitFor(api);

await builder.Build().RunAsync();
