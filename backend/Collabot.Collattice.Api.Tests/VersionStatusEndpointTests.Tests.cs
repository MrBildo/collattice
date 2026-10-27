using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The real GitHub call is behind the ILatestVersionSource seam, so these tests inject a fake
// source — no live network. The hosted service polls the fake on startup; the endpoint serves
// whatever the shared cache holds.
public class VersionStatusEndpointTests
{
    // sealed: a leaf test double for this class only; no subtype hierarchy is intended.
    private sealed class FakeVersionSource(LatestVersionResult? result) : ILatestVersionSource
    {
        public Task<LatestVersionResult?> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private static UpdateCheckTestFactory NewFactory
    (
        LatestVersionResult? sourceResult,
        IReadOnlyDictionary<string, string?>? config = null
    ) =>
        new()
        {
            ConfigOverrides = config,
            SourceFactory = () => new FakeVersionSource(sourceResult),
        };

    // sealed: a leaf test host for this class only; no subtype hierarchy is intended.
    private sealed class UpdateCheckTestFactory : CollatticeApiFactory
    {
        public Func<ILatestVersionSource>? SourceFactory { get; init; }

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                if (SourceFactory is null)
                {
                    return;
                }

                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ILatestVersionSource));
                if (descriptor is not null)
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<ILatestVersionSource>(_ => SourceFactory());
            });
        }
    }

    [Fact]
    public async Task GetVersionStatus_Returns200_WithCurrentAndNoCacheHeaders()
    {
        await using var factory = NewFactory(sourceResult: null);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/version/status");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        json.TryGetProperty("current", out _).ShouldBeTrue();
        json.TryGetProperty("updateAvailable", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task GetVersionStatus_NoAuthHeader_Returns200()
    {
        await using var factory = NewFactory(sourceResult: null);
        var client = factory.CreateClient();

        // No X-User-Key set — consistent with /version, the status is non-sensitive.
        var response = await client.GetAsync("/api/v1/version/status");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetVersionStatus_SourceReportsHigherVersion_ServesCachedLatest()
    {
        await using var factory = NewFactory
        (
            new LatestVersionResult("v99.0.0", "https://example.test/release")
        );
        var client = factory.CreateClient();

        // The cache is the shared singleton the hosted service writes and the endpoint reads.
        // Drive it deterministically rather than racing the background poll tick.
        var cache = factory.Services.GetRequiredService<VersionStatusCache>();
        cache.SetLatest(new LatestVersionResult("v99.0.0", "https://example.test/release"), DateTimeOffset.UtcNow);

        var response = await client.GetAsync("/api/v1/version/status");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);

        json.GetProperty("latest").GetString().ShouldBe("99.0.0");
        json.GetProperty("updateAvailable").GetBoolean().ShouldBeTrue();
        json.GetProperty("releaseUrl").GetString().ShouldBe("https://example.test/release");
    }

    [Fact]
    public async Task GetVersionStatus_UpdateCheckDisabled_ReportsNoUpdate()
    {
        // Kill switch on: the hosted service must not run, so the cache is never populated and
        // the endpoint reports current-only with no update — even though a (fake) source that
        // would report a higher version is registered.
        await using var factory = NewFactory
        (
            new LatestVersionResult("v99.0.0", "https://example.test/release"),
            config: new Dictionary<string, string?> { ["UpdateCheck:Enabled"] = "false" }
        );
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/version/status");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);

        json.GetProperty("updateAvailable").GetBoolean().ShouldBeFalse();
        json.TryGetProperty("latest", out var latest).ShouldBeTrue();
        (latest.ValueKind == JsonValueKind.Null).ShouldBeTrue();
    }
}
