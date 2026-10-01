using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class VersionEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetVersion_AsAdmin_Returns200WithVersion()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, factory);

        // Act
        var response = await _client.GetAsync("/api/v1/version");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        json.TryGetProperty("version", out var versionProp).ShouldBeTrue();
        versionProp.GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetVersion_ResponseHasNoCacheHeaders()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, factory);

        // Act
        var response = await _client.GetAsync("/api/v1/version");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl!.NoCache.ShouldBeTrue();
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }
}
