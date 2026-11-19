using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The integrator guide (docs/collattice/SKILL.md) is a hand-written reference to the MCP tools, and
// agents call the tools exactly as it says. When a tool parameter was renamed and the guide kept the
// old name, agents sent the old name and got a binder error, and nothing caught it: no test reads the
// guide. These tests hold the guide against the tools the composed app registers, the collection
// tools/list answers from, so a rename, an added or removed tool, or an added parameter has to change
// the guide in the same pull request.
//
// How the guide is read, so an author knows what counts:
// - A tool is a "#### `name`" heading under "## Tool Reference", a name in the tool-group table under
//   "## Connecting", and the "**N tools**" count beside that table.
// - A tool's parameters are the backticked names on its "- **Params:**" bullet and the lines that
//   continue it. On a nested bullet under it, only the first backticked name counts. Anything inside
//   parentheses is read as a value, a default or a note, never as a parameter. "a card ref" and
//   "card refs" stand for the names the guide defines them as, and "same as `tool`" copies that
//   tool's parameters.
// - Any backticked snake_case name anywhere in the guide must be a live tool, except the names the
//   guide mentions only to say they don't exist.
// Parameter names in the prose around a Params bullet are not read.
public partial class IntegratorGuideToolDriftTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private const string _guidePath = "docs/collattice/SKILL.md";

    // The guide names these to say they are deliberately not on the MCP surface.
    private static readonly string[] _namedAbsentTools = ["bulk_delete_cards"];

    // The guide's own definition: a cardId, or a cardNumber paired with boardId or boardSlug.
    private static readonly string[] _cardReference = ["cardId", "cardNumber", "boardId", "boardSlug"];

    // The bulk tools' form of the same: cardIds, or cardNumbers paired with boardId or boardSlug.
    private static readonly string[] _cardReferences = ["cardIds", "cardNumbers", "boardId", "boardSlug"];

    private readonly CollatticeApiFactory _factory = factory;

    [Fact]
    public void ToolReference_EverySection_MatchesTheLiveTools()
    {
        var live = LoadLiveTools();
        var guide = LoadGuide();

        var findings = new List<string>();

        foreach (var tool in guide.Tools.Where(tool => !live.ContainsKey(tool.Name)))
        {
            findings.Add(At(tool.Line, $"the Tool Reference has a section for `{tool.Name}`, which is not a live tool."));
        }

        var documented = guide.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var name in live.Keys.Where(name => !documented.Contains(name)).Order(StringComparer.Ordinal))
        {
            findings.Add($"`{name}` is a live tool with no \"#### `{name}`\" section in the Tool Reference.");
        }

        findings.Count.ShouldBe(0, Explain(findings));
    }

    [Fact]
    public void ToolGroupTable_ListedToolsAndCount_MatchTheLiveTools()
    {
        var live = LoadLiveTools();
        var guide = LoadGuide();

        var findings = new List<string>();

        foreach (var (name, line) in guide.TableTools.Where(entry => !live.ContainsKey(entry.Name)))
        {
            findings.Add(At(line, $"the tool-group table lists `{name}`, which is not a live tool."));
        }

        var listed = guide.TableTools.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var name in live.Keys.Where(name => !listed.Contains(name)).Order(StringComparer.Ordinal))
        {
            findings.Add($"`{name}` is a live tool missing from the tool-group table under \"## Connecting\".");
        }

        if (guide.StatedToolCount != live.Count)
        {
            var stated = guide.StatedToolCount?.ToString(CultureInfo.InvariantCulture) ?? "no";

            findings.Add
            (
                string.Create(CultureInfo.InvariantCulture, $"the guide says it hosts **{stated} tools**; the live server has {live.Count}.")
            );
        }

        findings.Count.ShouldBe(0, Explain(findings));
    }

    [Fact]
    public void ParamsBullets_EveryDocumentedParameter_ExistsOnTheLiveTool()
    {
        var live = LoadLiveTools();
        var guide = LoadGuide();

        var findings = new List<string>();

        foreach (var tool in guide.Tools)
        {
            if (!live.TryGetValue(tool.Name, out var liveParameters))
            {
                continue;
            }

            if (tool.Parameters is null)
            {
                findings.Add(At(tool.Line, $"the section for `{tool.Name}` has no \"- **Params:**\" bullet."));

                continue;
            }

            foreach (var parameter in tool.Parameters.Where(parameter => !liveParameters.Contains(parameter)).Order(StringComparer.Ordinal))
            {
                findings.Add
                (
                    At(tool.Line, $"`{tool.Name}`: the guide documents parameter `{parameter}`, which the live tool doesn't have. Live parameters: {List(liveParameters)}.")
                );
            }
        }

        findings.Count.ShouldBe(0, Explain(findings));
    }

    [Fact]
    public void ParamsBullets_EveryLiveParameter_IsDocumented()
    {
        var live = LoadLiveTools();
        var guide = LoadGuide();

        var findings = new List<string>();

        foreach (var tool in guide.Tools.Where(tool => tool.Parameters is not null))
        {
            if (!live.TryGetValue(tool.Name, out var liveParameters))
            {
                continue;
            }

            foreach (var parameter in liveParameters.Where(parameter => !tool.Parameters!.Contains(parameter)).Order(StringComparer.Ordinal))
            {
                findings.Add
                (
                    At(tool.Line, $"`{tool.Name}`: the live tool has parameter `{parameter}`, which the guide's Params bullet doesn't name.")
                );
            }
        }

        findings.Count.ShouldBe(0, Explain(findings));
    }

    [Fact]
    public void GuideText_EverySnakeCaseName_IsALiveTool()
    {
        var live = LoadLiveTools();
        var guide = LoadGuide();

        var findings = guide.SnakeCaseNames
            .Where(entry => !live.ContainsKey(entry.Name) && !_namedAbsentTools.Contains(entry.Name, StringComparer.Ordinal))
                .Select(entry => At(entry.Line, $"the guide names `{entry.Name}`, which is not a live tool."))
                    .ToList();

        findings.Count.ShouldBe(0, Explain(findings));
    }

    private Dictionary<string, HashSet<string>> LoadLiveTools()
    {
        var options = _factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;

        options.ToolCollection.ShouldNotBeNull();

        var tools = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var tool in options.ToolCollection)
        {
            var parameters = new HashSet<string>(StringComparer.Ordinal);

            if (tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    parameters.Add(property.Name);
                }
            }

            tools.Add(tool.ProtocolTool.Name, parameters);
        }

        tools.ShouldNotBeEmpty();

        return tools;
    }

    private static Guide LoadGuide()
    {
        var path = Path.Combine(TestRepositoryHelper.FindRoot(), "docs", "collattice", "SKILL.md");
        var lines = File.ReadAllText(path).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        var tools = new List<GuideTool>();
        var tableTools = new List<(string Name, int Line)>();
        var snakeCaseNames = new List<(string Name, int Line)>();
        int? statedToolCount = null;
        var section = "";

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var lineNumber = index + 1;

            foreach (Match match in SnakeCaseName.Matches(line))
            {
                snakeCaseNames.Add((match.Groups["name"].Value, lineNumber));
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                section = line[3..].Trim();

                continue;
            }

            if (section == "Connecting")
            {
                var count = StatedToolCountPattern.Match(line);

                if (count.Success)
                {
                    statedToolCount = int.Parse(count.Groups["count"].Value, CultureInfo.InvariantCulture);
                }

                var row = TableRow.Match(line);

                if (row.Success)
                {
                    foreach (Match match in BacktickedName.Matches(row.Groups["tools"].Value))
                    {
                        tableTools.Add((match.Groups["name"].Value, lineNumber));
                    }
                }

                continue;
            }

            if (section != "Tool Reference")
            {
                continue;
            }

            var heading = ToolHeading.Match(line);

            if (heading.Success)
            {
                tools.Add(ReadTool(heading.Groups["name"].Value, lines, index));
            }
        }

        tools.ShouldNotBeEmpty($"{_guidePath} has no \"#### `tool`\" headings under \"## Tool Reference\".");

        var byName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        var resolved = tools.Select(tool => ResolveSameAs(tool, byName)).ToList();

        return new Guide(resolved, tableTools, statedToolCount, snakeCaseNames);
    }

    private static GuideTool ResolveSameAs(GuideTool tool, Dictionary<string, GuideTool> byName) =>
        tool.SameAs is not null && byName.TryGetValue(tool.SameAs, out var source)
            ? tool with { Parameters = source.Parameters }
            : tool;

    private static GuideTool ReadTool(string name, string[] lines, int headingIndex)
    {
        var start = -1;

        for (var index = headingIndex + 1; index < lines.Length && !lines[index].StartsWith('#'); index++)
        {
            if (lines[index].StartsWith("- **Params:**", StringComparison.Ordinal))
            {
                start = index;

                break;
            }
        }

        if (start < 0)
        {
            return new GuideTool(name, headingIndex + 1, null, null);
        }

        var main = lines[start]["- **Params:**".Length..];
        var nested = new List<string>();

        for (var index = start + 1; index < lines.Length; index++)
        {
            var line = lines[index];

            if (line.Length == 0 || line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith('#'))
            {
                break;
            }

            if (line.StartsWith("  - ", StringComparison.Ordinal))
            {
                nested.Add(line);
            }
            else if (nested.Count > 0)
            {
                nested[^1] += " " + line.Trim();
            }
            else
            {
                main += " " + line.Trim();
            }
        }

        var outsideParentheses = StripParenthesized(main);

        var parameters = BacktickedIdentifier.Matches(outsideParentheses)
            .Select(match => match.Groups["name"].Value)
                .ToHashSet(StringComparer.Ordinal);

        foreach (var bullet in nested)
        {
            var first = BacktickedIdentifier.Match(bullet);

            if (first.Success)
            {
                parameters.Add(first.Groups["name"].Value);
            }
        }

        if (outsideParentheses.Contains("card refs", StringComparison.Ordinal))
        {
            parameters.UnionWith(_cardReferences);
        }
        else if (outsideParentheses.Contains("card ref", StringComparison.Ordinal))
        {
            parameters.UnionWith(_cardReference);
        }

        var sameAs = SameAsPattern.Match(outsideParentheses);

        return new GuideTool(name, headingIndex + 1, parameters, sameAs.Success ? sameAs.Groups["name"].Value : null);
    }

    private static string StripParenthesized(string text)
    {
        var depth = 0;
        var kept = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && depth > 0)
            {
                depth--;
            }
            else if (depth == 0)
            {
                kept.Append(character);
            }
        }

        return kept.ToString();
    }

    private static string At(int line, string finding) =>
        string.Create(CultureInfo.InvariantCulture, $"{_guidePath} line {line}: {finding}");

    private static string List(IEnumerable<string> names) =>
        string.Join(", ", names.Order(StringComparer.Ordinal).Select(name => $"`{name}`"));

    private static string Explain(List<string> findings) =>
        string.Create
        (
            CultureInfo.InvariantCulture,
            $"{_guidePath} disagrees with the live MCP tools in {findings.Count} place(s). Change the guide to match the tool (or the tool, if the guide is right):{Environment.NewLine}{string.Join(Environment.NewLine, findings)}"
        );

    [GeneratedRegex(@"^#### `(?<name>[a-z_]{1,64})`", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex ToolHeading { get; }

    [GeneratedRegex(@"^\| [^|]{1,64} \| (?<tools>[^|]{1,2000}) \|$", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex TableRow { get; }

    [GeneratedRegex(@"\*\*(?<count>\d{1,4}) tools\*\*", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex StatedToolCountPattern { get; }

    [GeneratedRegex("`(?<name>[a-z_]{1,64})`", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex BacktickedName { get; }

    [GeneratedRegex("`(?<name>[a-z][A-Za-z0-9]{0,63})`", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex BacktickedIdentifier { get; }

    [GeneratedRegex("`(?<name>[a-z]{1,32}(?:_[a-z]{1,32}){1,8})`", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex SnakeCaseName { get; }

    [GeneratedRegex("same as `(?<name>[a-z_]{1,64})`", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex SameAsPattern { get; }

    private sealed record GuideTool(string Name, int Line, HashSet<string>? Parameters, string? SameAs);

    private sealed record Guide
    (
        List<GuideTool> Tools,
        List<(string Name, int Line)> TableTools,
        int? StatedToolCount,
        List<(string Name, int Line)> SnakeCaseNames
    );
}
