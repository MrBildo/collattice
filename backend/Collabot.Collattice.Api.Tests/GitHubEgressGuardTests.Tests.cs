using Collabot.Collattice.Api.Hosting.UpdateCheck;
using Collabot.Collattice.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The base test factory keeps every host off the real GitHub API in two layers: the update
// check's version source is swapped for one that never touches the network, and every
// IHttpClientFactory client refuses api.github.com, records the attempt, and fails the factory's
// disposal. These tests pin both layers and prove the backstop actually fires.
public class GitHubEgressGuardTests
{
    [Fact]
    public async Task BaseFactory_VersionSource_IsTheNoNetworkSource()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();

        var source = factory.Services.GetRequiredService<ILatestVersionSource>();

        source.ShouldBeOfType<NoNetworkVersionSource>();
        factory.GitHubAttempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task RealGitHubSource_IsRefusedRecordedAndFailsDisposal()
    {
        // Arrange — build the production GitHub source on the production typed-client
        // registration (base address, headers, and the guard the factory adds to every client).
        var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();

        var httpClient = factory.Services
            .GetRequiredService<IHttpClientFactory>()
                .CreateClient(nameof(ILatestVersionSource));

        var realSource = ActivatorUtilities.CreateInstance<GitHubReleaseVersionSource>(factory.Services, httpClient);

        // Act — the guard's refusal reaches the caller; in a hosted-service poll the update check
        // would swallow it, which is why the record and the disposal check exist.
        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => realSource.GetLatestAsync(CancellationToken.None));

        // Assert — refused once (no retries), recorded, and the record fails disposal.
        refusal.Message.ShouldContain(GitHubEgressGuardHandler.BlockedHost);

        var attempt = factory.GitHubAttempts.ShouldHaveSingleItem();
        attempt.Host.ShouldBe(GitHubEgressGuardHandler.BlockedHost);
        attempt.AbsolutePath.ShouldEndWith("/releases/latest");

        var disposal = await Should.ThrowAsync<InvalidOperationException>(() => factory.DisposeAsync());
        disposal.Message.ShouldContain(GitHubEgressGuardHandler.BlockedHost);
    }
}
