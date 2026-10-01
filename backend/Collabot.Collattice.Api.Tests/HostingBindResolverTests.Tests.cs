using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class HostingBindResolverTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    [Fact]
    public void Resolve_AspnetcoreUrlsSet_ReturnsNull()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["ASPNETCORE_URLS"] = "http://localhost:5000",
        });

        var result = HostingBindResolver.Resolve(configuration);

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_UrlsKeySet_ReturnsNull()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["urls"] = "http://localhost:5000",
        });

        var result = HostingBindResolver.Resolve(configuration);

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_BothEnvUnset_ReturnsStructuredUrl()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["Hosting:ListenAddress"] = "0.0.0.0",
            ["Hosting:ListenPort"] = "8080",
        });

        var result = HostingBindResolver.Resolve(configuration);

        result.ShouldBe("http://0.0.0.0:8080");
    }

    [Fact]
    public void Resolve_OperatorOverridesPort_ReturnsStructuredUrl()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["Hosting:ListenAddress"] = "0.0.0.0",
            ["Hosting:ListenPort"] = "9090",
        });

        var result = HostingBindResolver.Resolve(configuration);

        result.ShouldBe("http://0.0.0.0:9090");
    }

    [Fact]
    public void Resolve_OperatorOverridesAddress_ReturnsStructuredUrl()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["Hosting:ListenAddress"] = "127.0.0.1",
            ["Hosting:ListenPort"] = "8080",
        });

        var result = HostingBindResolver.Resolve(configuration);

        result.ShouldBe("http://127.0.0.1:8080");
    }

    [Fact]
    public void Resolve_DefaultsWhenSectionAbsent_ReturnsStructuredUrl()
    {
        var configuration = BuildConfig([]);

        var result = HostingBindResolver.Resolve(configuration);

        result.ShouldBe("http://0.0.0.0:8080");
    }

    [Fact]
    public void Resolve_EnvVarSet_OverridesStructured()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["ASPNETCORE_URLS"] = "http://localhost:5000",
            ["Hosting:ListenAddress"] = "0.0.0.0",
            ["Hosting:ListenPort"] = "8080",
        });

        var result = HostingBindResolver.Resolve(configuration);

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_BothEnvVarsSet_UrlsWins()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["urls"] = "http://localhost:5000",
            ["ASPNETCORE_URLS"] = "http://localhost:6000",
        });

        var result = HostingBindResolver.Resolve(configuration);

        // `urls` wins per the `??` short-circuit; a future refactor that reverses the
        // `??` order would silently flip .NET-conventional precedence.
        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_EmptyStringEnvVar_ReturnsStructured()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["ASPNETCORE_URLS"] = "",
            ["Hosting:ListenAddress"] = "0.0.0.0",
            ["Hosting:ListenPort"] = "8080",
        });

        var result = HostingBindResolver.Resolve(configuration);

        // IsNullOrWhiteSpace treats empty-string as falsy; the `??` short-circuit alone
        // would NOT have handled this (empty string is non-null, so `??` doesn't fall
        // through). Realistic shape: `ASPNETCORE_URLS=` in a .env or `docker run -e`.
        result.ShouldBe("http://0.0.0.0:8080");
    }
}
