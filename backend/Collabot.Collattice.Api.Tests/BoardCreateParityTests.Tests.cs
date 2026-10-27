using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Cross-surface parity tests for board create.
// REST POST /boards and the MCP create_board tool independently
// re-encode the shared create rules — name-required and slug-uniqueness — and both
// route the seed through BoardSeeder.Seed. These tests
// feed the same invalid input to both surfaces and assert both reject identically.
public class BoardCreateParityTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
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

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // ── create_board: empty name rejected on both surfaces ────────────────────

    [Fact]
    public async Task CreateBoard_EmptyName_RejectedOnBothSurfaces()
    {
        // Arrange
        var (_, tools, authKey) = CreateMcpTools();

        // Act — REST
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = "   " });

        // Act — MCP
        var mcpResult = await tools.CreateBoardAsync(authKey, "   ");

        // Assert — both reject the blank name, each in its own idiom
        restResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("Name is required");
        restBody.ShouldNotContain("Error:");

        mcpResult.ShouldContain("Error: Name is required");
    }

    // ── create_board: duplicate slug rejected on both surfaces ────────────────
    // The slug is derived from the name; a second board whose name slugifies to an
    // existing slug collides. REST categorizes this as 409 Conflict; MCP returns the
    // "Error: ..." string.

    [Fact]
    public async Task CreateBoard_DuplicateSlug_RejectedOnBothSurfaces()
    {
        // Arrange — an existing board both surfaces will collide against. Two distinct
        // display names that slugify to the same slug prove the collision is on the
        // derived slug, not the raw name.
        var unique = Guid.NewGuid().ToString("N");
        var takenName = $"Parity Board {unique}";

        TestAuthHelper.SetAdminAuth(_client, _factory);
        (await _client.PostAsJsonAsync("/api/v1/boards", new { name = takenName })).EnsureSuccessStatusCode();

        var (db, tools, authKey) = CreateMcpTools();
        var boardsBefore = await db.Boards.CountAsync();

        // Act — REST: same display name → same derived slug → collision
        var restResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = takenName });

        // Act — MCP: an upper-cased variant that slugifies identically
        var mcpResult = await tools.CreateBoardAsync(authKey, takenName.ToUpperInvariant());

        // Assert — REST 409 Conflict, MCP "Error: ..." — same rule, same message
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("A board with that slug already exists");
        restBody.ShouldNotContain("Error:");

        mcpResult.ShouldContain("Error: A board with that slug already exists");

        // Neither rejected create added a board
        (await db.Boards.CountAsync()).ShouldBe(boardsBefore);
    }
}
