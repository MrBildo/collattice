using System.Text.Json.Nodes;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class AppSettingsMergerTests
{
    private static JsonObject Parse(string json) =>
        (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void Merge_OperatorUntouchedKey_RefreshesToNewShippedDefault()
    {
        var shipped = Parse("""{"Hosting":{"ListenPort":9090}}""");
        var current = Parse("""{"Hosting":{"ListenPort":8080}}""");
        var baseline = Parse("""{"Hosting":{"ListenPort":8080}}""");

        var result = AppSettingsMerger.Merge(shipped, current, baseline);

        result.Merged["Hosting"]!["ListenPort"]!.GetValue<int>().ShouldBe(9090);
        result.Changes.ShouldContain(c => c.Path == "Hosting:ListenPort" && c.Kind == MergeChangeKind.RefreshedDefault);
    }

    [Fact]
    public void Merge_OperatorEditedKey_PreservesOperatorValueEvenWhenShippedChanged()
    {
        var shipped = Parse("""{"Hosting":{"ListenPort":9090}}""");
        var current = Parse("""{"Hosting":{"ListenPort":7777}}""");
        var baseline = Parse("""{"Hosting":{"ListenPort":8080}}""");

        var result = AppSettingsMerger.Merge(shipped, current, baseline);

        result.Merged["Hosting"]!["ListenPort"]!.GetValue<int>().ShouldBe(7777);
        result.Changes.ShouldContain(c => c.Path == "Hosting:ListenPort" && c.Kind == MergeChangeKind.PreservedOperatorEdit);
    }

    [Fact]
    public void Merge_NewShippedKey_IsAddedWhenAbsentFromOperatorFile()
    {
        var shipped = Parse("""{"Hosting":{"ListenPort":8080},"Cors":{"AllowedOrigins":[]}}""");
        var current = Parse("""{"Hosting":{"ListenPort":8080}}""");
        var baseline = Parse("""{"Hosting":{"ListenPort":8080}}""");

        var result = AppSettingsMerger.Merge(shipped, current, baseline);

        result.Merged["Cors"]!["AllowedOrigins"]!.AsArray().Count.ShouldBe(0);
        result.Changes.ShouldContain(c => c.Path == "Cors" && c.Kind == MergeChangeKind.Added);
    }

    [Fact]
    public void Merge_OperatorAddedKey_PreservesEvenWhenAbsentFromShipped()
    {
        var shipped = Parse("""{"Hosting":{"ListenPort":8080}}""");
        var current = Parse("""{"Hosting":{"ListenPort":8080},"ConnectionStrings":{"Board":"Data Source=/srv/collaboard/data/collaboard.db"}}""");
        var baseline = Parse("""{"Hosting":{"ListenPort":8080}}""");

        var result = AppSettingsMerger.Merge(shipped, current, baseline);

        result.Merged["ConnectionStrings"]!["Board"]!.GetValue<string>().ShouldBe("Data Source=/srv/collaboard/data/collaboard.db");
        result.Changes.ShouldContain(c => c.Path == "ConnectionStrings" && c.Kind == MergeChangeKind.PreservedExtraKey);
    }

    [Fact]
    public void Merge_NoBaseline_RunsInConservativeMode_KeepsExistingOperatorValues()
    {
        var shipped = Parse("""{"Hosting":{"ListenPort":9090}}""");
        var current = Parse("""{"Hosting":{"ListenPort":8080}}""");

        var result = AppSettingsMerger.Merge(shipped, current, baseline: null);

        result.Merged["Hosting"]!["ListenPort"]!.GetValue<int>().ShouldBe(8080);
        result.Conservative.ShouldBeTrue();
        result.Changes.ShouldContain(c => c.Path == "Hosting:ListenPort" && c.Kind == MergeChangeKind.PreservedConservative);
    }

    [Fact]
    public void Merge_NoBaseline_StillAddsBrandNewKeysFromShipped()
    {
        var shipped = Parse("""{"Hosting":{"ListenPort":8080},"NewSection":{"Key":"value"}}""");
        var current = Parse("""{"Hosting":{"ListenPort":8080}}""");

        var result = AppSettingsMerger.Merge(shipped, current, baseline: null);

        result.Merged["NewSection"]!["Key"]!.GetValue<string>().ShouldBe("value");
        result.Changes.ShouldContain(c => c.Path == "NewSection" && c.Kind == MergeChangeKind.Added);
    }

    [Fact]
    public void Merge_OperatorAndShippedIdentical_EmitsNoRefreshNoise()
    {
        var shipped = Parse("""{"Hosting":{"ListenPort":8080}}""");
        var current = Parse("""{"Hosting":{"ListenPort":8080}}""");
        var baseline = Parse("""{"Hosting":{"ListenPort":8080}}""");

        var result = AppSettingsMerger.Merge(shipped, current, baseline);

        result.Merged["Hosting"]!["ListenPort"]!.GetValue<int>().ShouldBe(8080);
        result.HasChanges.ShouldBeFalse();
    }

    [Fact]
    public void Merge_NestedThreeLevels_RoutesPreserveAndRefreshIndependently()
    {
        var shipped = Parse(
            """
            {
              "Logging": {
                "LogLevel": {
                  "Default": "Warning",
                  "Microsoft.AspNetCore": "Warning"
                }
              }
            }
            """);

        // Operator pinned Default to Debug; left Microsoft.AspNetCore alone.
        var current = Parse(
            """
            {
              "Logging": {
                "LogLevel": {
                  "Default": "Debug",
                  "Microsoft.AspNetCore": "Information"
                }
              }
            }
            """);

        var baseline = Parse(
            """
            {
              "Logging": {
                "LogLevel": {
                  "Default": "Information",
                  "Microsoft.AspNetCore": "Information"
                }
              }
            }
            """);

        var result = AppSettingsMerger.Merge(shipped, current, baseline);

        // Operator's Debug edit is preserved.
        result.Merged["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>().ShouldBe("Debug");

        // Untouched Microsoft.AspNetCore moves from Information -> Warning (refreshed).
        result.Merged["Logging"]!["LogLevel"]!["Microsoft.AspNetCore"]!.GetValue<string>().ShouldBe("Warning");

        result.Changes.ShouldContain(c => c.Path == "Logging:LogLevel:Default" && c.Kind == MergeChangeKind.PreservedOperatorEdit);
        result.Changes.ShouldContain(c => c.Path == "Logging:LogLevel:Microsoft.AspNetCore" && c.Kind == MergeChangeKind.RefreshedDefault);
    }

    [Fact]
    public void Merge_OperatorTypeMismatch_TreatsAsLeafAndPreservesOperatorWithBaseline()
    {
        // Operator changed an object into a string. Treat the operator's choice as authoritative.
        var shipped = Parse("""{"Admin":{"AuthKey":null}}""");
        var current = Parse("""{"Admin":"my-string-key"}""");
        var baseline = Parse("""{"Admin":{"AuthKey":null}}""");

        var result = AppSettingsMerger.Merge(shipped, current, baseline);

        result.Merged["Admin"]!.GetValue<string>().ShouldBe("my-string-key");
        result.Changes.ShouldContain(c => c.Path == "Admin" && c.Kind == MergeChangeKind.PreservedOperatorEdit);
    }

    [Fact]
    public void Merge_NullInAllThreeInputs_RoundTripsWithNoChanges()
    {
        var shipped = Parse("""{"Admin":{"AuthKey":null}}""");
        var current = Parse("""{"Admin":{"AuthKey":null}}""");
        var baseline = Parse("""{"Admin":{"AuthKey":null}}""");

        var result = AppSettingsMerger.Merge(shipped, current, baseline);

        result.Merged["Admin"]!["AuthKey"].ShouldBeNull();
        result.HasChanges.ShouldBeFalse();
    }

    [Fact]
    public void Merge_RealCollatticeScenario_PreservesOperatorEditsAndAddsNewSection()
    {
        // Mirrors a realistic upgrade: shipped gains a new section while the operator has pinned
        // an absolute ConnectionStrings:Board and changed Hosting:ListenPort. Both operator edits
        // are preserved; the new section is added.

        var shipped = Parse(
            """
            {
              "Hosting": { "ServeSpa": true, "ListenAddress": "0.0.0.0", "ListenPort": 8080 },
              "Cors": { "AllowedOrigins": [] },
              "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
              "Attachments": { "MaxBytes": 5242880 }
            }
            """);

        var current = Parse(
            """
            {
              "Hosting": { "ServeSpa": true, "ListenAddress": "0.0.0.0", "ListenPort": 9090 },
              "Cors": { "AllowedOrigins": [] },
              "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
              "ConnectionStrings": { "Board": "Data Source=/srv/collaboard/data/collaboard.db" }
            }
            """);

        var baseline = Parse(
            """
            {
              "Hosting": { "ServeSpa": true, "ListenAddress": "0.0.0.0", "ListenPort": 8080 },
              "Cors": { "AllowedOrigins": [] },
              "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } }
            }
            """);

        var result = AppSettingsMerger.Merge(shipped, current, baseline);

        // Operator-pinned absolute DB path preserved (PreservedExtraKey -- key absent from shipped).
        result.Merged["ConnectionStrings"]!["Board"]!.GetValue<string>().ShouldBe("Data Source=/srv/collaboard/data/collaboard.db");

        // Operator's ListenPort edit preserved despite shipped being unchanged at 8080
        // (baseline 8080 != current 9090 -> operator edit).
        result.Merged["Hosting"]!["ListenPort"]!.GetValue<int>().ShouldBe(9090);

        // New shipped Attachments section added.
        result.Merged["Attachments"]!["MaxBytes"]!.GetValue<int>().ShouldBe(5242880);

        result.Changes.ShouldContain(c => c.Path == "ConnectionStrings" && c.Kind == MergeChangeKind.PreservedExtraKey);
        result.Changes.ShouldContain(c => c.Path == "Hosting:ListenPort" && c.Kind == MergeChangeKind.PreservedOperatorEdit);
        result.Changes.ShouldContain(c => c.Path == "Attachments" && c.Kind == MergeChangeKind.Added);
    }

    [Fact]
    public void Merge_NonObjectShippedRoot_Throws()
    {
        var shipped = JsonNode.Parse("[]")!;
        var current = Parse("""{}""");

        Should.Throw<ArgumentException>(() => AppSettingsMerger.Merge(shipped, current, baseline: null));
    }
}
