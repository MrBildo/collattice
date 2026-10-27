using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Collabot.Collattice.Api.Hosting.UpdateCheck;

// The latest stable release is whatever GitHub's /releases/latest reports for
// our own repo — the same repo publish.yml cuts releases against. /releases/latest excludes
// drafts AND pre-releases server-side, which is exactly "the latest stable an operator should
// run", so no client-side filtering is needed. Consumes only a version string and a URL over
// HTTPS; nothing is downloaded, executed, or auto-applied.
//
// sealed: a DI-registered leaf typed-client implementation of ILatestVersionSource; there is
// no subtype and no reason to allow one.
internal sealed class GitHubReleaseVersionSource
(
    HttpClient httpClient,
    IOptions<UpdateCheckSettings> settings,
    ILogger<GitHubReleaseVersionSource> logger
) : ILatestVersionSource
{
    private readonly HttpClient _httpClient = httpClient
        ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly UpdateCheckSettings _settings = settings.Value;
    private readonly ILogger<GitHubReleaseVersionSource> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task<LatestVersionResult?> GetLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var requestUri = $"repos/{_settings.Repository}/releases/latest";

            var release = await _httpClient.GetFromJsonAsync<GitHubRelease>(requestUri, cancellationToken);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                _logger.LogInformation
                (
                    "Update check: GitHub returned no usable latest release for {Repository}.",
                    _settings.Repository
                );
                return null;
            }

            return new LatestVersionResult(release.TagName, release.HtmlUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException or System.Text.Json.JsonException)
        {
            // Fail-quiet: network down, rate-limit (403), offline/air-gap, timeout, or a
            // malformed payload all collapse to "couldn't check" — return null so the caller
            // keeps the last good cached value. Information level, not error: a self-hosted
            // instance that can't reach GitHub is a normal operating state, not a fault.
            _logger.LogInformation
            (
                ex,
                "Update check: could not fetch latest release for {Repository}; keeping last known value.",
                _settings.Repository
            );
            return null;
        }
    }

    private sealed record GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }
    }
}
