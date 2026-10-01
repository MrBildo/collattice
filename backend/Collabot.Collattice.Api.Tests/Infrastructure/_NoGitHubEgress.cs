using System.Collections.Concurrent;
using Collabot.Collattice.Api.Hosting.UpdateCheck;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// Test hosts must never reach the real GitHub API. The update check polls GitHub's Releases API
// from a hosted service, so before these types every suite run carried a live network dependency
// and, under parallel load, rate-limit (403) noise that read like a cause in flaky-test logs.
// The base factory swaps the version source for NoNetworkVersionSource, and as a backstop routes
// every IHttpClientFactory client through GitHubEgressGuardHandler.

// Answers "no release known" — the same value the real source returns when GitHub is unreachable,
// so the update check takes its ordinary fail-quiet path. Tests that need a release inject their
// own source over this one.
//
// sealed: a leaf test double behind one interface; a subtype would be a second double hiding
// behind the name of this one.
internal sealed class NoNetworkVersionSource : ILatestVersionSource
{
    public Task<LatestVersionResult?> GetLatestAsync(CancellationToken cancellationToken) =>
        Task.FromResult<LatestVersionResult?>(null);
}

// Refuses any request to api.github.com before it reaches a socket, and records it. A refusal
// alone would be invisible: the update check swallows request failures by design. The owning
// factory reads the record when it is disposed and fails the run if anything was attempted.
// The refusal is not an HttpRequestException on purpose: the standard resilience handler retries
// those, which would turn one attempt into four and add seconds of backoff to the test that
// proves this guard fires.
//
// sealed: the guard's whole value is that it cannot be weakened; an override of SendAsync would
// be a way to let the request through.
internal sealed class GitHubEgressGuardHandler(ConcurrentQueue<Uri> attempts) : DelegatingHandler
{
    public const string BlockedHost = "api.github.com";

    private readonly ConcurrentQueue<Uri> _attempts = attempts
        ?? throw new ArgumentNullException(nameof(attempts));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;

        if (uri is not null && string.Equals(uri.Host, BlockedHost, StringComparison.OrdinalIgnoreCase))
        {
            _attempts.Enqueue(uri);

            throw new InvalidOperationException($"Test host attempted an outbound request to {BlockedHost}: {uri}");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
