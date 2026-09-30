using Collabot.Collattice.Api.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Proves the configuration-provider precedence Program.cs establishes:
//
//   env (Section__Key) > appsettings.json > hardcoded default
//
// The appsettings.Local.json overlay channel was retired; Program.cs no longer
// loads it and the installers no longer write it. The smart-merge on appsettings.json
// is what now preserves operator edits across upgrades.
//
// This is tested by replicating Program.cs's exact provider sequence against a real
// ConfigurationBuilder (the HostingBindResolverTests pattern) rather than the WAF:
// WebApplicationFactory injects in-memory providers via UseSetting /
// ConfigureAppConfiguration, which structurally cannot exercise the AddJsonFile ->
// AddEnvironmentVariables ordering. Temp files + a scoped real env var are the only
// seam that proves file-vs-env ordering.
//
// ProgramConfigChain mirrors Program.cs. Source-regression guards at the
// bottom (a) assert the env re-add still follows WebApplication.CreateBuilder so a
// future-added JSON source cannot silently re-shadow env vars and (b) assert the
// .Local.json load is gone.
public sealed class ConfigPrecedenceTests : IDisposable
{
    private const string _key = "ConfigPrecedence:Probe";
    private const string _envKey = "ConfigPrecedence__Probe";

    private readonly string _dir;
    private readonly string _appsettingsPath;

    public ConfigPrecedenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cfgprec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _appsettingsPath = Path.Combine(_dir, "appsettings.json");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_envKey, null);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // Mirrors Program.cs — the framework default chain (incl. an env-var
    // provider added at builder-construction time), then the env-var re-add. There is
    // no .Local.json overlay anywhere in this chain.
    private IConfiguration BuildProgramConfigChain()
    {
        var builder = new ConfigurationBuilder();

        // appsettings.json — the shipped default tier (now operator-editable; edits
        // survive upgrades via the smart-merge).
        builder.AddJsonFile(_appsettingsPath, optional: true, reloadOnChange: false);

        // The env-var provider WebApplication.CreateBuilder adds at construction time.
        // Present here so the test proves the re-add is what fixes ordering, not merely
        // that an env provider exists at all.
        builder.AddEnvironmentVariables();

        // Program.cs — the env-var re-add (originally added to fix ordering; a later change retired the .Local.json
        // load that sat between these two AddEnvironmentVariables calls but kept the
        // re-add as insurance against a future-added JSON source silently re-shadowing
        // env vars).
        builder.AddEnvironmentVariables();

        return builder.Build();
    }

    // Program.cs's "hardcoded default" tier is a settings-POCO initializer or a
    // GetValue(..., literal) fallback, not a configuration provider — i.e. a
    // null-coalesce on the resolved config value. Model it exactly that way.
    private string Resolve(IConfiguration config, string hardcodedDefault) =>
        config[_key] ?? hardcodedDefault;

    private void WriteAppsettings(string value) =>
        File.WriteAllText(_appsettingsPath, $$"""
            { "ConfigPrecedence": { "Probe": "{{value}}" } }
            """);

    [Fact]
    public void EnvVar_WinsOver_AppsettingsJson()
    {
        WriteAppsettings("from-appsettings");
        Environment.SetEnvironmentVariable(_envKey, "from-env");

        var resolved = Resolve(BuildProgramConfigChain(), "hardcoded-default");

        resolved.ShouldBe("from-env");
    }

    [Fact]
    public void AppsettingsJson_WinsOver_HardcodedDefault()
    {
        WriteAppsettings("from-appsettings");

        var resolved = Resolve(BuildProgramConfigChain(), "hardcoded-default");

        resolved.ShouldBe("from-appsettings");
    }

    [Fact]
    public void HardcodedDefault_WinsWhenNoProviderSuppliesTheKey()
    {
        var resolved = Resolve(BuildProgramConfigChain(), "hardcoded-default");

        resolved.ShouldBe("hardcoded-default");
    }

    // Source-level regression lock 1: the entire invariant is that
    // .Local.json no longer loads. A refactor that re-adds the .Local.json AddJsonFile
    // call silently re-introduces the retired overlay channel with no behavioral test
    // able to catch it (Program.cs would once again load operator-editable JSON the
    // smart-merge contract assumes does not exist).
    [Fact]
    public void ProgramCs_DoesNotLoadAppsettingsLocalJson()
    {
        var programPath = Path.Combine
        (
            TestRepositoryHelper.FindRoot(),
            "backend",
            "Collabot.Collattice.Api",
            "Program.cs"
        );

        File.Exists(programPath)
            .ShouldBeTrue($"Program.cs not found at {programPath}");

        var source = File.ReadAllText(programPath);

        source.ShouldNotContain
        (
            "appsettings.Local.json",
            Case.Sensitive,
            "The appsettings.Local.json overlay channel was retired — Program.cs must not "
            + "AddJsonFile(\"appsettings.Local.json\", ...) (or reference the file name at all). "
            + "If the channel needs to come back, do so behind a new card with a re-revised "
            + "ConfigPrecedenceTests."
        );
    }

    // Source-level regression lock 2: the env-var re-add must still appear after
    // WebApplication.CreateBuilder so a *future*-added JSON source cannot silently
    // re-shadow env vars. The original fix was about ordering against .Local.json;
    // .Local.json is gone now, but the re-add stays as structural insurance.
    [Fact]
    public void ProgramCs_ReaddsEnvVarsAfterWebApplicationCreateBuilder()
    {
        var programPath = Path.Combine
        (
            TestRepositoryHelper.FindRoot(),
            "backend",
            "Collabot.Collattice.Api",
            "Program.cs"
        );

        File.Exists(programPath)
            .ShouldBeTrue($"Program.cs not found at {programPath}");

        var source = File.ReadAllText(programPath);

        var createBuilderIndex = source.IndexOf("WebApplication.CreateBuilder", StringComparison.Ordinal);
        var envReaddIndex = source.IndexOf("AddEnvironmentVariables()", StringComparison.Ordinal);

        createBuilderIndex.ShouldBeGreaterThan
        (
            -1,
            "WebApplication.CreateBuilder call missing from Program.cs"
        );
        envReaddIndex.ShouldBeGreaterThan
        (
            -1,
            "AddEnvironmentVariables() re-add missing from Program.cs — a future-added JSON "
            + "source could silently shadow env vars without this insurance line"
        );
        envReaddIndex.ShouldBeGreaterThan
        (
            createBuilderIndex,
            "AddEnvironmentVariables() must appear AFTER WebApplication.CreateBuilder so it "
            + "sits at the top of the provider chain regardless of any future-added JSON source"
        );
    }
}
