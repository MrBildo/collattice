using Collabot.Collattice.Api.Installation;
using Collabot.Collattice.Api.Tests.Infrastructure;

using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class AppSettingsMergeCliTests : IDisposable
{
    private readonly string _scratchDir;

    public AppSettingsMergeCliTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"collattice-merge-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratchDir))
        {
            try
            {
                Directory.Delete(_scratchDir, true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Run_FullMerge_PreservesOperatorEditsRefreshesUntouchedAddsNewKeys()
    {
        var shippedPath = WriteJson("shipped.json",
            """
            {
              "Hosting": { "ListenPort": 9090 },
              "Cors": { "AllowedOrigins": [] }
            }
            """);

        var currentPath = WriteJson("appsettings.json",
            """
            {
              "Hosting": { "ListenPort": 7777 },
              "ConnectionStrings": { "Board": "Data Source=/srv/collaboard/data/collaboard.db" }
            }
            """);

        var baselinePath = WriteJson("appsettings.shipped.json",
            """
            {
              "Hosting": { "ListenPort": 8080 },
              "ConnectionStrings": { "Board": null }
            }
            """);

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = AppSettingsMergeCli.Run
        (
            ["--baseline", baselinePath, shippedPath, currentPath],
            stdout,
            stderr
        );

        exit.ShouldBe(AppSettingsMergeCli.ExitOk);

        var merged = await File.ReadAllTextAsync(currentPath);
        merged.ShouldContain("\"ListenPort\": 7777");                                              // operator edit preserved
        merged.ShouldContain("\"Board\": \"Data Source=/srv/collaboard/data/collaboard.db\"");     // operator add preserved
        merged.ShouldContain("\"AllowedOrigins\":");                                              // new shipped key added

        // Baseline should have refreshed to the new shipped content (byte-for-byte).
        var baselineAfter = await File.ReadAllTextAsync(baselinePath);
        var shippedAfter = await File.ReadAllTextAsync(shippedPath);
        baselineAfter.ShouldBe(shippedAfter);

        var summary = stdout.ToString();
        summary.ShouldContain("updated");
        summary.ShouldContain("Cors");
    }

    [Fact]
    public async Task Run_FirstUpgradeNoBaseline_UsesConservativeModeButStillAddsNewKeys()
    {
        var shippedPath = WriteJson("shipped.json",
            """
            {
              "Hosting": { "ListenPort": 9090 },
              "Cors": { "AllowedOrigins": [] }
            }
            """);

        var currentPath = WriteJson("appsettings.json",
            """{"Hosting":{"ListenPort":8080}}""");

        var baselinePath = Path.Combine(_scratchDir, "appsettings.shipped.json");

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = AppSettingsMergeCli.Run
        (
            ["--baseline", baselinePath, shippedPath, currentPath],
            stdout,
            stderr
        );

        exit.ShouldBe(AppSettingsMergeCli.ExitOk);

        var merged = await File.ReadAllTextAsync(currentPath);
        merged.ShouldContain("\"ListenPort\": 8080");                  // existing value preserved (conservative)
        merged.ShouldContain("\"AllowedOrigins\":");                   // new key added

        // Baseline created on first run with the shipped content -- next merge can refresh.
        File.Exists(baselinePath).ShouldBeTrue();

        stdout.ToString().ShouldContain("conservative mode");
    }

    [Fact]
    public async Task Run_SecondRunWithSameInputs_IsIdempotent()
    {
        // C-6 merge-idempotency proof: after a first successful merge has seeded the baseline,
        // an immediate re-run with the same artifact produces a byte-identical on-disk file
        // and emits no Added/RefreshedDefault entries (the categories the summary's "added"
        // and "refreshed" headers report). PreservedOperatorEdit / PreservedExtraKey may still
        // appear -- they describe operator edits that persist across the no-op merge, not
        // modifications.

        var shippedPath = WriteJson("shipped.json",
            """
            {
              "Hosting": { "ListenPort": 9090 },
              "Cors": { "AllowedOrigins": [] }
            }
            """);

        var currentPath = WriteJson("appsettings.json",
            """{"Hosting":{"ListenPort":7777}}""");

        var baselinePath = Path.Combine(_scratchDir, "appsettings.shipped.json");

        var firstStdout = new StringWriter();
        var firstStderr = new StringWriter();

        var firstExit = AppSettingsMergeCli.Run
        (
            ["--baseline", baselinePath, shippedPath, currentPath],
            firstStdout,
            firstStderr
        );

        firstExit.ShouldBe(AppSettingsMergeCli.ExitOk);

        var afterFirst = await File.ReadAllTextAsync(currentPath);

        // Re-run with identical inputs (baseline is now seeded to shipped).
        var secondStdout = new StringWriter();
        var secondStderr = new StringWriter();

        var secondExit = AppSettingsMergeCli.Run
        (
            ["--baseline", baselinePath, shippedPath, currentPath],
            secondStdout,
            secondStderr
        );

        secondExit.ShouldBe(AppSettingsMergeCli.ExitOk);

        var afterSecond = await File.ReadAllTextAsync(currentPath);
        afterSecond.ShouldBe(afterFirst);

        // No spurious modifications on re-run: the summary's modifying-change headers
        // ("added (n)" / "refreshed (n)") must not appear.
        var secondSummary = secondStdout.ToString();
        secondSummary.ShouldNotContain("added (", Case.Sensitive);
        secondSummary.ShouldNotContain("refreshed (", Case.Sensitive);
    }

    [Fact]
    public void Run_NoArgs_ReturnsUsageExitCode()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = AppSettingsMergeCli.Run([], stdout, stderr);

        exit.ShouldBe(AppSettingsMergeCli.ExitUsage);
        stderr.ToString().ShouldContain("usage:");
    }

    [Fact]
    public async Task Run_MissingShippedFile_ReturnsErrorAndDoesNotTouchOnDisk()
    {
        var currentPath = WriteJson("appsettings.json", """{"Hosting":{"ListenPort":8080}}""");
        var ondiskBefore = await File.ReadAllTextAsync(currentPath);

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var missingShipped = Path.Combine(_scratchDir, "missing-shipped.json");

        var exit = AppSettingsMergeCli.Run
        (
            [missingShipped, currentPath],
            stdout,
            stderr
        );

        exit.ShouldBe(AppSettingsMergeCli.ExitMissingShippedFile);
        (await File.ReadAllTextAsync(currentPath)).ShouldBe(ondiskBefore);
        stderr.ToString().ShouldContain(missingShipped);
        stderr.ToString().ShouldContain("shipped file not found");
    }

    [Fact]
    public async Task Run_MissingCurrentFile_ReturnsError()
    {
        var shippedPath = WriteJson("shipped.json", """{"Hosting":{"ListenPort":8080}}""");
        var missingCurrent = Path.Combine(_scratchDir, "missing-appsettings.json");

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = AppSettingsMergeCli.Run([shippedPath, missingCurrent], stdout, stderr);

        exit.ShouldBe(AppSettingsMergeCli.ExitMissingCurrentFile);
        stderr.ToString().ShouldContain(missingCurrent);
        stderr.ToString().ShouldContain("on-disk file not found");
    }

    [Fact]
    public async Task Run_CorruptOnDiskFile_LeavesItUntouched()
    {
        var shippedPath = WriteJson("shipped.json", """{"Hosting":{"ListenPort":8080}}""");
        var currentPath = Path.Combine(_scratchDir, "appsettings.json");
        await File.WriteAllTextAsync(currentPath, "not json {{");

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = AppSettingsMergeCli.Run([shippedPath, currentPath], stdout, stderr);

        exit.ShouldBe(AppSettingsMergeCli.ExitParseFailed);
        (await File.ReadAllTextAsync(currentPath)).ShouldBe("not json {{");
        stderr.ToString().ShouldContain("leaving on-disk file untouched");
    }

    [Fact]
    public async Task Run_CorruptBaseline_FallsBackToConservativeMergeAndStillSucceeds()
    {
        var shippedPath = WriteJson("shipped.json",
            """
            {
              "Hosting": { "ListenPort": 9090 },
              "NewKey": "value"
            }
            """);

        var currentPath = WriteJson("appsettings.json",
            """{"Hosting":{"ListenPort":7777}}""");

        var baselinePath = Path.Combine(_scratchDir, "appsettings.shipped.json");
        await File.WriteAllTextAsync(baselinePath, "garbage }}");

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = AppSettingsMergeCli.Run
        (
            ["--baseline", baselinePath, shippedPath, currentPath],
            stdout,
            stderr
        );

        exit.ShouldBe(AppSettingsMergeCli.ExitOk);

        var merged = await File.ReadAllTextAsync(currentPath);
        merged.ShouldContain("\"ListenPort\": 7777");      // conservative preservation
        merged.ShouldContain("\"NewKey\": \"value\"");      // still adds new keys

        stderr.ToString().ShouldContain("ignoring corrupt baseline");
    }

    // Regression: guards against a silent failed-closed invocation gate.
    //
    // The Collabhost bug lived in a *silent failed-closed* invocation gate (a bash
    // version-regex that skipped the merge silently on every upgrade). The invariant under
    // test here is "every skip path is loud + non-zero", not any merge-engine property. If a
    // future refactor introduces a code path that returns ExitOk without performing the merge,
    // or that fails silently, this test catches it.
    [Theory]
    [InlineData(SkipCause.MissingShipped)]
    [InlineData(SkipCause.MissingCurrent)]
    [InlineData(SkipCause.UnparseableCurrent)]
    public async Task Run_EverySkipCausingCondition_FailsLoudWithNonZeroExitAndStderr(SkipCause cause)
    {
        var (shippedPath, currentPath, expectedExit, expectedStderrSubstring) = await ArrangeSkipCauseAsync(cause);

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = AppSettingsMergeCli.Run([shippedPath, currentPath], stdout, stderr);

        exit.ShouldNotBe(AppSettingsMergeCli.ExitOk, "every skip-causing condition must return a non-zero exit code");
        exit.ShouldBe(expectedExit);

        var stderrText = stderr.ToString();
        stderrText.ShouldNotBeNullOrWhiteSpace("every skip path must emit a stderr message naming the cause");
        stderrText.ShouldContain(expectedStderrSubstring);
    }

    [Fact]
    public void ProgramCs_DoesNotGateMergeOnVersionOutput()
    {
        // Structural guarantee that Collattice never reintroduces a version-coupled gate around
        // the merge subcommand (the root cause was such a gate in Collabhost's bash). Read
        // the Program.cs source and assert the --merge-appsettings branch does not branch on
        // --version output.
        var programPath = Path.Combine
        (
            TestRepositoryHelper.FindRoot(),
            "backend",
            "Collabot.Collattice.Api",
            "Program.cs"
        );

        File.Exists(programPath).ShouldBeTrue($"Program.cs not found at {programPath}");

        var source = File.ReadAllText(programPath);

        var mergeBranchIndex = source.IndexOf("--merge-appsettings", StringComparison.Ordinal);
        mergeBranchIndex.ShouldBeGreaterThan(-1, "--merge-appsettings subcommand branch missing from Program.cs");

        // Find the closing of the merge branch. The branch is short (Environment.Exit + close
        // brace). We assert there is no occurrence of "--version" *between* the merge branch
        // start and the next 200 characters -- the merge code path is structurally separate
        // from --version handling.
        var mergeBranchScope = source.Substring(mergeBranchIndex, Math.Min(400, source.Length - mergeBranchIndex));
        mergeBranchScope.ShouldNotContain("--version", Case.Sensitive, "merge-appsettings code path must not branch on --version output (no version-coupled gate)");
    }

    public enum SkipCause
    {
        MissingShipped,
        MissingCurrent,
        UnparseableCurrent
    }

    private async Task<(string shippedPath, string currentPath, int expectedExit, string expectedStderr)> ArrangeSkipCauseAsync(SkipCause cause)
    {
        switch (cause)
        {
            case SkipCause.MissingShipped:
                {
                    var current = WriteJson("appsettings.json", """{"Hosting":{"ListenPort":8080}}""");
                    var missing = Path.Combine(_scratchDir, "missing-shipped.json");
                    return (missing, current, AppSettingsMergeCli.ExitMissingShippedFile, "shipped file not found");
                }

            case SkipCause.MissingCurrent:
                {
                    var shipped = WriteJson("shipped.json", """{"Hosting":{"ListenPort":8080}}""");
                    var missing = Path.Combine(_scratchDir, "missing-appsettings.json");
                    return (shipped, missing, AppSettingsMergeCli.ExitMissingCurrentFile, "on-disk file not found");
                }

            case SkipCause.UnparseableCurrent:
                {
                    var shipped = WriteJson("shipped.json", """{"Hosting":{"ListenPort":8080}}""");
                    var current = Path.Combine(_scratchDir, "appsettings.json");
                    await File.WriteAllTextAsync(current, "not valid json {{");
                    return (shipped, current, AppSettingsMergeCli.ExitParseFailed, "failed to parse on-disk file");
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(cause), cause, "unknown skip cause");
        }
    }

    private string WriteJson(string fileName, string content)
    {
        var path = Path.Combine(_scratchDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
