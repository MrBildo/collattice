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
//   parentheses is read as a value, a default or a note, never as a parameter. "same as `tool`"
//   copies that tool's parameters.
// - "a card ref" and "card refs" stand for the parameters the guide defines them as, read from the
//   guide's definitions: for "a card ref", the bullets under "### Pattern B" and the sentence in the
//   Tool Reference that begins "Card ref" below means; for "card refs", the sentence under
//   "### Bulk operations" that begins "Identify the cards with". A parenthesis written right after
//   "card ref" or "card refs" on a Params line is read whole, the parentheses inside it included:
//   one that names parameters is a definition too, and one that names none, such as "(the source)",
//   is a note. Agents read every one of these, so every definition of a term must name the same
//   parameters, and each name in them is checked on every tool that uses it.
// - A parenthesis on a Params line that doesn't close is a failure, because what follows it can't
//   be read.
// - Any backticked snake_case name anywhere in the guide must be a live tool, except a name written
//   right after "no " (as in "There is no `name`"), which must not be one.
// Parameter names in the prose around a Params bullet are not read.
public partial class IntegratorGuideToolDriftTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private const string _guidePath = "docs/collattice/SKILL.md";

    private const string _cardReference = "a card ref";
    private const string _cardReferences = "card refs";

    private const string _patternBSite = "the bullets under \"### Pattern B\"";
    private const string _preambleSite = "the Tool Reference sentence beginning \"Card ref\" below means";
    private const string _bulkSite = "the \"### Bulk operations\" introduction";

    private const string _preambleAnchor = "\"Card ref\" below means";
    private const string _bulkAnchor = "Identify the cards with";

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

            if (!tool.ParenthesesClose)
            {
                findings.Add(At(tool.Line, $"`{tool.Name}`: a parenthesis on the Params bullet doesn't close, so what follows it can't be read."));
            }

            foreach (var parameter in tool.Parameters.Where(parameter => !liveParameters.Contains(parameter)).Order(StringComparer.Ordinal))
            {
                findings.Add
                (
                    At(tool.Line, $"`{tool.Name}`: the guide documents parameter `{parameter}`{Origin(guide, tool, parameter)}, which the live tool doesn't have. Live parameters: {List(liveParameters)}.")
                );
            }
        }

        findings.Count.ShouldBe(0, Explain(findings));
    }

    [Fact]
    public void CardReferenceDefinitions_EveryDefinition_NamesTheSameParameters()
    {
        var guide = LoadGuide();

        var findings = new List<string>();

        foreach (var (term, site) in RequiredDefinitionSites)
        {
            if (!guide.Definitions.Any(definition => definition.Term == term && definition.Site == site))
            {
                findings.Add($"the guide's definition of \"{term}\" in {site} wasn't found, so the names it gives can't be checked.");
            }
        }

        foreach (var definition in guide.Definitions.Where(definition => definition.Names.Count == 0))
        {
            findings.Add(At(definition.Line, $"{definition.Site} defines \"{definition.Term}\" but names no parameter."));
        }

        foreach (var group in guide.Definitions.GroupBy(definition => definition.Term, StringComparer.Ordinal))
        {
            var first = group.First();

            if (group.All(definition => definition.Names.SetEquals(first.Names)))
            {
                continue;
            }

            findings.Add($"\"{group.Key}\" is defined differently in different places, and agents read each one: {string.Join("; ", group.Select(Describe))}.");
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
    public void GuideText_EverySnakeCaseName_IsALiveToolUnlessSaidAbsent()
    {
        var live = LoadLiveTools();
        var guide = LoadGuide();

        var findings = new List<string>();

        foreach (var (name, line, saidAbsent) in guide.SnakeCaseNames)
        {
            if (saidAbsent && live.ContainsKey(name))
            {
                findings.Add(At(line, $"the guide says there is no `{name}`, but it is a live tool."));
            }
            else if (!saidAbsent && !live.ContainsKey(name))
            {
                findings.Add(At(line, $"the guide names `{name}`, which is not a live tool."));
            }
        }

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
        var lines = File.ReadAllLines(path);

        var tools = new List<GuideTool>();
        var tableTools = new List<(string Name, int Line)>();
        var snakeCaseNames = new List<(string Name, int Line, bool SaidAbsent)>();
        var definitions = new List<Definition>();
        var patternBBullets = new List<(string Text, int Line)>();
        int? statedToolCount = null;
        var section = "";
        var subsection = "";

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var lineNumber = index + 1;

            foreach (Match match in SnakeCaseName.Matches(line))
            {
                snakeCaseNames.Add((match.Groups["name"].Value, lineNumber, SaysThereIsNo(line, match.Index)));
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                section = line[3..].Trim();
                subsection = "";

                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                subsection = line[4..].Trim();

                continue;
            }

            if (subsection.StartsWith("Pattern B", StringComparison.Ordinal) && line.StartsWith("- ", StringComparison.Ordinal))
            {
                patternBBullets.Add((Paragraph(lines, index, 0), lineNumber));
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

        definitions.AddRange(AnchoredDefinitions(lines, _preambleAnchor, _cardReference, _preambleSite));
        definitions.AddRange(AnchoredDefinitions(lines, _bulkAnchor, _cardReferences, _bulkSite));

        if (patternBBullets.Count > 0)
        {
            var names = Identifiers(string.Join(' ', patternBBullets.Select(bullet => bullet.Text)));

            definitions.Add(new Definition(_cardReference, _patternBSite, patternBBullets[0].Line, names));
        }

        definitions.AddRange(tools.SelectMany(tool => tool.InlineDefinitions));

        foreach (var tool in tools.Where(tool => tool.CardReference is not null && tool.Parameters is not null))
        {
            tool.Parameters!.UnionWith(DefinedNames(definitions, tool.CardReference!));
        }

        var byName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        var resolved = tools.Select(tool => ResolveSameAs(tool, byName)).ToList();

        return new Guide(resolved, tableTools, statedToolCount, snakeCaseNames, definitions);
    }

    // Each place the anchor phrase appears, read as a definition up to the end of its paragraph. The
    // phrase is searched for in the text with its line breaks joined, so a phrase wrapped across two
    // lines is still found.
    private static List<Definition> AnchoredDefinitions(string[] lines, string anchor, string term, string site)
    {
        var joined = new StringBuilder();
        var starts = new int[lines.Length];

        for (var index = 0; index < lines.Length; index++)
        {
            starts[index] = joined.Length;
            joined.Append(lines[index].Trim()).Append(' ');
        }

        var text = joined.ToString();
        var definitions = new List<Definition>();

        for (var at = text.IndexOf(anchor, StringComparison.Ordinal); at >= 0; at = text.IndexOf(anchor, at + anchor.Length, StringComparison.Ordinal))
        {
            var index = Array.FindLastIndex(starts, start => start <= at);

            definitions.Add(new Definition(term, site, index + 1, Identifiers(Paragraph(lines, index, at - starts[index]))));
        }

        return definitions;
    }

    // The text from a column of a trimmed line to the end of its paragraph: the next blank line,
    // heading or list item.
    private static string Paragraph(string[] lines, int index, int column)
    {
        var text = new StringBuilder(lines[index].Trim()[column..]);

        for (var next = index + 1; next < lines.Length; next++)
        {
            var line = lines[next];

            if (line.Trim().Length == 0 || line.StartsWith('#') || line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            text.Append(' ').Append(line.Trim());
        }

        return text.ToString();
    }

    // True when the backticked name at this column follows the word "no", as in "There is no `name`".
    private static bool SaysThereIsNo(string line, int column) =>
        line[..column].EndsWith("no ", StringComparison.OrdinalIgnoreCase)
            && (column == 3 || !char.IsLetter(line[column - 4]));

    private static HashSet<string> Identifiers(string text) =>
        BacktickedIdentifier.Matches(text)
            .Select(match => match.Groups["name"].Value)
                .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> DefinedNames(IEnumerable<Definition> definitions, string term) =>
        definitions
            .Where(definition => definition.Term == term)
                .SelectMany(definition => definition.Names)
                    .ToHashSet(StringComparer.Ordinal);

    // Where a documented name came from, when it came from the definition of a card reference.
    private static string Origin(Guide guide, GuideTool tool, string parameter)
    {
        if (tool.CardReference is null)
        {
            return "";
        }

        var lines = guide.Definitions
            .Where(definition => definition.Term == tool.CardReference && definition.Names.Contains(parameter))
                .Select(definition => definition.Line.ToString(CultureInfo.InvariantCulture))
                    .ToList();

        return lines.Count == 0 ? "" : $" (from \"{tool.CardReference}\", defined at line {string.Join(", ", lines)})";
    }

    private static string Describe(Definition definition) =>
        string.Create(CultureInfo.InvariantCulture, $"line {definition.Line} ({definition.Site}) names {List(definition.Names)}");

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
            return new GuideTool(name, headingIndex + 1, null, null, null, [], true);
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

        var parameters = Identifiers(outsideParentheses);

        foreach (var bullet in nested)
        {
            var first = BacktickedIdentifier.Match(bullet);

            if (first.Success)
            {
                parameters.Add(first.Groups["name"].Value);
            }
        }

        var sameAs = SameAsPattern.Match(outsideParentheses);

        return new GuideTool
        (
            name,
            headingIndex + 1,
            parameters,
            sameAs.Success ? sameAs.Groups["name"].Value : null,
            CardReferenceTerm(outsideParentheses),
            ReadInlineDefinitions(name, main, start + 1),
            EveryParenthesisCloses(main)
        );
    }

    private static string? CardReferenceTerm(string paramsText) =>
        paramsText switch
        {
            _ when paramsText.Contains(_cardReferences, StringComparison.Ordinal) => _cardReferences,
            _ when paramsText.Contains("card ref", StringComparison.Ordinal) => _cardReference,
            _ => null,
        };

    // Each parenthesis right after "card ref" or "card refs", read whole up to the parenthesis that
    // closes it. One that names parameters defines the term where an agent reads it; one that names
    // none, such as "(the source)", is a note. One that never closes is left to the Params check,
    // which fails on it.
    private static List<Definition> ReadInlineDefinitions(string tool, string paramsText, int line)
    {
        var definitions = new List<Definition>();

        foreach (Match match in InlineCardReference.Matches(paramsText))
        {
            var open = match.Index + match.Length - 1;
            var close = ClosingParenthesis(paramsText, open);

            if (close < 0)
            {
                continue;
            }

            var names = Identifiers(paramsText[(open + 1)..close]);
            var term = match.Groups["term"].Value == _cardReferences ? _cardReferences : _cardReference;

            if (names.Count > 0)
            {
                definitions.Add(new Definition(term, $"the Params line of `{tool}`", line, names));
            }
        }

        return definitions;
    }

    // The index of the parenthesis that closes the one at the given index, counting the parentheses
    // inside it, or -1 when it never closes.
    private static int ClosingParenthesis(string text, int open)
    {
        var depth = 0;

        for (var index = open; index < text.Length; index++)
        {
            if (text[index] == '(')
            {
                depth++;
            }
            else if (text[index] == ')')
            {
                depth--;

                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static bool EveryParenthesisCloses(string text)
    {
        for (var open = text.IndexOf('(', StringComparison.Ordinal); open >= 0; open = text.IndexOf('(', open + 1))
        {
            if (ClosingParenthesis(text, open) < 0)
            {
                return false;
            }
        }

        return true;
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
            $"{_guidePath} disagrees with the live MCP tools in {findings.Count} place(s). Change the guide to match the tool (or the tool, if the guide is right). Values, defaults and notes go inside parentheses, and a tool the guide says doesn't exist is written \"no `name`\":{Environment.NewLine}{string.Join(Environment.NewLine, findings)}"
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

    [GeneratedRegex(@"(?<term>card refs?) \(", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex InlineCardReference { get; }

    private static (string Term, string Site)[] RequiredDefinitionSites =>
    [
        (_cardReference, _patternBSite),
        (_cardReference, _preambleSite),
        (_cardReferences, _bulkSite),
    ];

    private sealed record Definition(string Term, string Site, int Line, HashSet<string> Names);

    private sealed record GuideTool
    (
        string Name,
        int Line,
        HashSet<string>? Parameters,
        string? SameAs,
        string? CardReference,
        List<Definition> InlineDefinitions,
        bool ParenthesesClose
    );

    private sealed record Guide
    (
        List<GuideTool> Tools,
        List<(string Name, int Line)> TableTools,
        int? StatedToolCount,
        List<(string Name, int Line, bool SaidAbsent)> SnakeCaseNames,
        List<Definition> Definitions
    );
}
