using System.Net;
using System.Text.Json;
using Collabot.Collattice.Api.Mcp;
using Collabot.Collattice.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Production sits behind a reverse proxy that terminates TLS and dials this process over plain
// HTTP on localhost, sending X-Forwarded-Proto: https. The pipeline honors that header from a
// loopback peer only, and get_api_info builds its base URL from the resulting request scheme.
public class ForwardedSchemeTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;

    private Task<HttpContext> SendVersionRequestAsync(IPAddress remoteAddress, string? forwardedProto) =>
        _factory.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/api/v1/version";
            context.Connection.RemoteIpAddress = remoteAddress;

            if (forwardedProto is not null)
            {
                context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
            }
        });

    [Fact]
    public async Task ForwardedProto_FromLoopbackProxy_SetsRequestScheme()
    {
        var context = await SendVersionRequestAsync(IPAddress.Loopback, "https");

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Request.Scheme.ShouldBe("https");
    }

    [Fact]
    public async Task ForwardedProto_FromIPv6LoopbackProxy_SetsRequestScheme()
    {
        var context = await SendVersionRequestAsync(IPAddress.IPv6Loopback, "https");

        context.Request.Scheme.ShouldBe("https");
    }

    [Fact]
    public async Task ForwardedProto_FromNonLoopbackPeer_IsIgnored()
    {
        // 192.0.2.0/24 is the documentation range: a peer that is not a same-box proxy.
        var context = await SendVersionRequestAsync(IPAddress.Parse("192.0.2.10"), "https");

        context.Request.Scheme.ShouldBe("http");
    }

    [Fact]
    public async Task ForwardedProto_Absent_KeepsConnectionScheme()
    {
        var context = await SendVersionRequestAsync(IPAddress.Loopback, forwardedProto: null);

        context.Request.Scheme.ShouldBe("http");
    }

    // Only the scheme is taken from a trusted proxy. A loopback peer that also sends
    // X-Forwarded-For and X-Forwarded-Host must not change the client address or the host.
    [Fact]
    public async Task ForwardedForAndHost_FromLoopbackProxy_AreIgnored()
    {
        var context = await _factory.Server.SendAsync(request =>
        {
            request.Request.Method = HttpMethods.Get;
            request.Request.Path = "/api/v1/version";
            request.Request.Host = new HostString("collattice-api.example.test");
            request.Connection.RemoteIpAddress = IPAddress.Loopback;
            request.Request.Headers["X-Forwarded-Proto"] = "https";
            request.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
            request.Request.Headers["X-Forwarded-Host"] = "spoofed.example.test";
        });

        context.Request.Scheme.ShouldBe("https");
        context.Request.Host.Value.ShouldBe("collattice-api.example.test");
        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Loopback);
    }

    [Fact]
    public async Task GetApiInfo_BaseUrl_UsesRequestSchemeAndHost()
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("collattice-api.example.test");

        var tools = new SystemTools
        (
            scope.ServiceProvider.GetRequiredService<McpAuthService>(),
            new HttpContextAccessor { HttpContext = httpContext }
        );

        var result = await tools.GetApiInfoAsync(_factory.AdminAuthKey, CancellationToken.None);

        var payload = JsonSerializer.Deserialize<JsonElement>(result);
        payload.GetProperty("baseUrl").GetString().ShouldBe("https://collattice-api.example.test");
        payload.GetProperty("apiPrefix").GetString().ShouldBe("/api/v1");
    }
}
