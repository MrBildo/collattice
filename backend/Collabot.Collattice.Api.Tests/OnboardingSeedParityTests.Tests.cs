using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Cross-surface parity tests for the onboarding seed.
//
// Three board-creation front doors seed boards: the install-time first-run seed
// (Program.cs EnsureSeed), REST POST /boards, and the MCP create_board tool. The
// shared scaffold (archive lane, S/M/L/XL sizes, starter labels) flows through
// BoardSeeder.Seed so the three paths can't silently drift — REST/MCP/install seed
// drift is the top bug class on this codebase. These tests assert the lockstep
// property: the starter labels are identical across all three paths, AND the
// install-only welcome sample card is present on the install board but on neither
// programmatically-created board (it's first-run onboarding, not a per-board
// fixture). The property gated here is "the seed paths don't silently drift," not
// "the seed paths are byte-identical" — the three default visible lanes and the
// welcome card are deliberately install-only.
public class OnboardingSeedParityTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly List<IServiceScope> _scopes = [];

    private (BoardDbContext Db, BoardTools Tools, string AuthKey) CreateMcpTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        return (db, new BoardTools(db, auth, scope.ServiceProvider.GetRequiredService<IWebhookSink>()), _factory.AdminAuthKey);
    }

    private BoardDbContext NewDbScope()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<BoardDbContext>();
    }

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static readonly (string Name, string Color)[] _expectedStarterLabels =
        BoardSeeder.StarterLabels;

    private async Task<Guid> CreateBoardViaRestAsync()
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync("/api/v1/boards", new { name = $"Seed Parity REST {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        var board = await response.Content.ReadFromJsonAsync<JsonElement>();
        return board.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateBoardViaMcpAsync()
    {
        var (_, tools, authKey) = CreateMcpTools();
        var result = await tools.CreateBoardAsync(authKey, $"Seed Parity MCP {Guid.NewGuid():N}");
        result.ShouldNotContain("Error:");
        var board = JsonSerializer.Deserialize<JsonElement>(result);
        return board.GetProperty("id").GetGuid();
    }

    // ── starter labels: identical across install / REST / MCP ──────────────────

    [Fact]
    public async Task StarterLabels_SeededIdentically_AcrossInstallRestAndMcp()
    {
        // Arrange — one board per front door. The install board already exists
        // (Program.cs seeded it at host startup); create the other two now.
        var restBoardId = await CreateBoardViaRestAsync();
        var mcpBoardId = await CreateBoardViaMcpAsync();

        var db = NewDbScope();

        // Act — pull each board's labels as a comparable (name, color) set
        async Task<List<(string Name, string? Color)>> LabelsFor(Guid boardId) =>
            await db.Labels
                .Where(l => l.BoardId == boardId)
                .OrderBy(l => l.Name)
                    .Select(l => new ValueTuple<string, string?>(l.Name, l.Color))
                    .ToListAsync();

        var installLabels = await LabelsFor(_factory.DefaultBoardId);
        var restLabels = await LabelsFor(restBoardId);
        var mcpLabels = await LabelsFor(mcpBoardId);

        // Assert — every path carries exactly the starter set, byte-for-byte
        var expected = _expectedStarterLabels
            .Select(l => new ValueTuple<string, string?>(l.Name, l.Color))
            .OrderBy(l => l.Item1)
            .ToList();

        installLabels.ShouldBe(expected);
        restLabels.ShouldBe(expected);
        mcpLabels.ShouldBe(expected);
    }

    [Fact]
    public async Task StarterLabels_AreNonEmpty_SoFirstCardIsLabelable()
    {
        // A fresh board with zero labels is the exact gap the starter-label seed closes — the
        // create-card Labels section would be hidden. Guard the floor explicitly.
        _expectedStarterLabels.ShouldNotBeEmpty();
        _expectedStarterLabels.Length.ShouldBeInRange(3, 4);
    }

    // ── welcome card: install-only, absent from programmatic boards ────────────

    [Fact]
    public async Task WelcomeCard_IsInstallOnly_AbsentFromRestAndMcpBoards()
    {
        // Arrange
        var restBoardId = await CreateBoardViaRestAsync();
        var mcpBoardId = await CreateBoardViaMcpAsync();

        var db = NewDbScope();

        // Act + Assert — install board has exactly one (the welcome card);
        // programmatically-created boards seed no cards at all.
        (await db.Cards.CountAsync(c => c.BoardId == _factory.DefaultBoardId)).ShouldBe(1);
        (await db.Cards.CountAsync(c => c.BoardId == restBoardId)).ShouldBe(0);
        (await db.Cards.CountAsync(c => c.BoardId == mcpBoardId)).ShouldBe(0);
    }

    [Fact]
    public async Task WelcomeCard_IsAWellFormedDeletableSample_OnTheInstallBoard()
    {
        // Arrange
        var db = NewDbScope();

        var welcome = await db.Cards.SingleAsync(c => c.BoardId == _factory.DefaultBoardId);

        // Act — resolve the card's referenced lane, size, and label assignment
        var lane = await db.Lanes.SingleAsync(l => l.Id == welcome.LaneId);
        var size = await db.CardSizes.SingleAsync(s => s.Id == welcome.SizeId);
        var labelIds = await db.CardLabels
            .Where(cl => cl.CardId == welcome.Id)
            .Select(cl => cl.LabelId)
                .ToListAsync();
        var labelNames = await db.Labels
            .Where(l => labelIds.Contains(l.Id))
            .Select(l => l.Name)
                .ToListAsync();

        // Assert — first card (Number 1, top of Backlog), real size + a starter
        // label in situ, and self-identifies as a deletable sample in its body.
        welcome.Number.ShouldBe(1);
        welcome.IsTemp.ShouldBeFalse();
        welcome.Position.ShouldBe(0);

        lane.Name.ShouldBe("Backlog");
        lane.IsArchiveLane.ShouldBeFalse();

        size.Ordinal.ShouldBe(0);
        size.Name.ShouldBe("S");

        labelNames.ShouldBe(["Feature"]);

        welcome.DescriptionMarkdown.ShouldContain("sample card");
        welcome.DescriptionMarkdown.ShouldContain("delete");
    }
}
