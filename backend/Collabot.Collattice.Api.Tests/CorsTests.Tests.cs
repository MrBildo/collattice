using System.Net;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class CorsTests
{
    private const string _allowedOrigin = "https://portal.example.com";
    private const string _disallowedOrigin = "https://evil.example.com";

    private static async Task<CollatticeApiFactory> CreateProdFactoryAsync(
        IReadOnlyDictionary<string, string?> overrides)
    {
        var factory = CollatticeApiFactory.WithConfig("Production", overrides);
        await factory.InitializeAsync();
        return factory;
    }

    private static async Task<CollatticeApiFactory> CreateDevFactoryAsync(
        IReadOnlyDictionary<string, string?> overrides)
    {
        var factory = CollatticeApiFactory.WithConfig("Development", overrides);
        await factory.InitializeAsync();
        return factory;
    }

    private static HttpRequestMessage Preflight(string path, string origin, string requestMethod)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", requestMethod);
        request.Headers.Add("Access-Control-Request-Headers", "x-user-key");
        return request;
    }

    [Fact]
    public async Task Cors_AllowedOrigin_PreflightSucceeds()
    {
        await using var factory = await CreateProdFactoryAsync(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = _allowedOrigin,
        });
        var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("/api/v1/boards", _allowedOrigin, "GET"));

        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldContain(_allowedOrigin);
        response.Headers.GetValues("Access-Control-Allow-Credentials").ShouldContain("true");
    }

    [Fact]
    public async Task Cors_DisallowedOrigin_NoAllowOriginHeader()
    {
        await using var factory = await CreateProdFactoryAsync(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = _allowedOrigin,
        });
        var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("/api/v1/boards", _disallowedOrigin, "GET"));

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task Cors_EmptyAllowList_NoAllowOriginHeader()
    {
        await using var factory = await CreateProdFactoryAsync(new Dictionary<string, string?>());
        var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("/api/v1/boards", _allowedOrigin, "GET"));

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task Cors_AllowedOrigin_ActualRequestSucceeds()
    {
        await using var factory = await CreateProdFactoryAsync(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = _allowedOrigin,
        });
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/boards");
        request.Headers.Add("Origin", _allowedOrigin);

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldContain(_allowedOrigin);
        response.Headers.GetValues("Access-Control-Allow-Credentials").ShouldContain("true");
    }

    [Fact]
    public async Task Cors_DevEnvironment_AllowsAnyOrigin()
    {
        await using var factory = await CreateDevFactoryAsync(new Dictionary<string, string?>());
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/boards");
        request.Headers.Add("Origin", _disallowedOrigin);

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldContain("*");
    }

    [Fact]
    public async Task Cors_Sse_AllowedOrigin_EmitsAllowOriginHeader()
    {
        await using var factory = await CreateProdFactoryAsync(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = _allowedOrigin,
        });
        var client = factory.CreateClient();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = new HttpRequestMessage
        (
            HttpMethod.Get,
            $"/api/v1/boards/{factory.DefaultBoardId}/events"
        );
        request.Headers.Add("Origin", _allowedOrigin);

        var response = await client.SendAsync
        (
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token
        );

        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldContain(_allowedOrigin);
    }
}
