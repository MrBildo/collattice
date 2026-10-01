using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Mcp;
using Collabot.Collattice.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Cross-surface parity tests for card-size CRUD.
// SizeEndpoints.cs and SizeTools.cs
// re-encode auto-ordinal assignment, ordinal-collision, and size-in-use-before-delete
// independently with no shared service. These tests feed the same input to both
// surfaces and assert identical outcomes — both the accept path (auto-ordinal lands on
// the same value) and the reject paths (collision, in-use).
public class SizeCrudParityTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly List<IServiceScope> _scopes = [];

    private (BoardDbContext Db, SizeTools Tools, string AuthKey) CreateMcpTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        return (db, new SizeTools(db, auth, broadcaster), _factory.AdminAuthKey);
    }

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // A fresh board ships seeded with the default sizes S/M/L/XL (ordinals 0-3), so
    // a no-ordinal create must auto-assign ordinal 4 on either surface.
    private async Task<Guid> SeedBoardAsync()
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync("/api/v1/boards", new { name = $"Size Parity {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        var board = await response.Content.ReadFromJsonAsync<JsonElement>();
        return board.GetProperty("id").GetGuid();
    }

    private async Task<(Guid SizeId, int Ordinal)> CreateSizeAsync(Guid boardId, string name, int ordinal)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/sizes", new { name, ordinal });
        response.EnsureSuccessStatusCode();
        var size = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (size.GetProperty("id").GetGuid(), size.GetProperty("ordinal").GetInt32());
    }

    // A freshly POSTed board seeds only the archive lane (hidden from /board), so it
    // has no working lane to place a card in. Create one explicitly.
    private async Task<Guid> CreateLaneAsync(Guid boardId)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes", new { name = "Work", position = 0 });
        response.EnsureSuccessStatusCode();
        var lane = await response.Content.ReadFromJsonAsync<JsonElement>();
        return lane.GetProperty("id").GetGuid();
    }

    // ── create_size: auto-ordinal lands on the same value on both surfaces ────
    // No ordinal supplied → both surfaces assign (current max + 1). Two equivalent
    // boards isolate the surfaces; the resolved ordinal must match.

    [Fact]
    public async Task CreateSize_AutoOrdinal_MatchesOnBothSurfaces()
    {
        // Arrange — two boards each seeded with S/M/L/XL (ordinals 0-3)
        var restBoardId = await SeedBoardAsync();
        var mcpBoardId = await SeedBoardAsync();
        var (_, tools, authKey) = CreateMcpTools();

        // Act — REST: omit ordinal entirely so the auto-assign branch runs
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/sizes", new { name = "XXL" });

        // Act — MCP: ordinal omitted
        var mcpResult = await tools.CreateSizeAsync(authKey, mcpBoardId, "XXL");

        // Assert — both auto-assign ordinal 4 (one past the seeded XL at ordinal 3)
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var restSize = await restResponse.Content.ReadFromJsonAsync<JsonElement>();
        restSize.GetProperty("ordinal").GetInt32().ShouldBe(4);

        mcpResult.ShouldNotContain("Error");
        var mcpSize = JsonSerializer.Deserialize<JsonElement>(mcpResult);
        mcpSize.GetProperty("ordinal").GetInt32().ShouldBe(4);
    }

    // ── create_size: a taken ordinal or name is a conflict, never a save failure ──
    // The unique (BoardId, Ordinal) and (BoardId, Name) indexes would otherwise answer a 500.

    [Fact]
    public async Task CreateSize_TakenOrdinal_ConflictsOnBothSurfacesWithTheUpdateMessage()
    {
        // Arrange — the seeded S holds ordinal 0 on both boards
        var restBoardId = await SeedBoardAsync();
        var mcpBoardId = await SeedBoardAsync();
        var (db, tools, authKey) = CreateMcpTools();

        // Act
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/sizes", new { name = "Huge", ordinal = 0 });
        var mcpResult = await tools.CreateSizeAsync(authKey, mcpBoardId, "Huge", 0);

        // Assert — 409 on REST, "Error: ..." on MCP, the message the update paths use
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("Ordinal already taken by another size");

        mcpResult.ShouldContain("Error: Ordinal already taken by another size");

        (await db.CardSizes.AnyAsync(s => s.BoardId == restBoardId && s.Name == "Huge")).ShouldBeFalse();
        (await db.CardSizes.AnyAsync(s => s.BoardId == mcpBoardId && s.Name == "Huge")).ShouldBeFalse();
    }

    [Fact]
    public async Task CreateSize_TakenName_ConflictsOnBothSurfaces()
    {
        // Arrange — the seeded M exists on both boards; a free ordinal isolates the name rule
        var restBoardId = await SeedBoardAsync();
        var mcpBoardId = await SeedBoardAsync();
        var (db, tools, authKey) = CreateMcpTools();

        // Act
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/sizes", new { name = "M", ordinal = 40 });
        var mcpResult = await tools.CreateSizeAsync(authKey, mcpBoardId, "M", 40);

        // Assert
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("A size with that name already exists on this board");

        mcpResult.ShouldContain("Error: A size with that name already exists on this board");

        (await db.CardSizes.CountAsync(s => s.BoardId == restBoardId && s.Name == "M")).ShouldBe(1);
        (await db.CardSizes.CountAsync(s => s.BoardId == mcpBoardId && s.Name == "M")).ShouldBe(1);
    }

    [Fact]
    public async Task CreateSize_NoOrdinalAfterTheHighestPossible_RejectedOnBothSurfaces()
    {
        // Arrange — one past int.MaxValue would wrap to int.MinValue and sort the new size first
        var restBoardId = await SeedBoardAsync();
        var mcpBoardId = await SeedBoardAsync();
        await CreateSizeAsync(restBoardId, "Top", int.MaxValue);
        await CreateSizeAsync(mcpBoardId, "Top", int.MaxValue);
        var (db, tools, authKey) = CreateMcpTools();

        // Act
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/sizes", new { name = "Beyond" });
        var mcpResult = await tools.CreateSizeAsync(authKey, mcpBoardId, "Beyond");

        // Assert — 400, not a conflict: no rival holds the slot, there is no slot
        restResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("No ordinal is free after the board's highest size");

        mcpResult.ShouldContain("Error: No ordinal is free after the board's highest size");

        (await db.CardSizes.AnyAsync(s => (s.BoardId == restBoardId || s.BoardId == mcpBoardId) && s.Name == "Beyond")).ShouldBeFalse();
    }

    // ── update_size: ordinal collision rejected on both surfaces ──────────────
    // REST categorizes this as 409 Conflict; MCP returns the "Error: ..." string.

    [Fact]
    public async Task UpdateSize_OrdinalCollision_RejectedOnBothSurfaces()
    {
        // Arrange — two extra sizes per board (ordinals 10 and 11); move the second
        // onto the first's ordinal to collide
        var restBoardId = await SeedBoardAsync();
        var mcpBoardId = await SeedBoardAsync();
        await CreateSizeAsync(restBoardId, "REST-A", 10);
        var (restSecondSizeId, _) = await CreateSizeAsync(restBoardId, "REST-B", 11);
        await CreateSizeAsync(mcpBoardId, "MCP-A", 10);
        var (mcpSecondSizeId, _) = await CreateSizeAsync(mcpBoardId, "MCP-B", 11);
        var (_, tools, authKey) = CreateMcpTools();

        // Act — REST
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PatchAsJsonAsync($"/api/v1/sizes/{restSecondSizeId}", new { ordinal = 10 });

        // Act — MCP
        var mcpResult = await tools.UpdateSizeAsync(authKey, mcpSecondSizeId, ordinal: 10);

        // Assert — REST 409 Conflict, MCP "Error: ..." — same rule, same message
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("Ordinal already taken by another size");
        restBody.ShouldNotContain("Error:");

        mcpResult.ShouldContain("Error: Ordinal already taken by another size");
    }

    // ── delete_size: size in use by a card rejected on both surfaces ──────────
    // REST categorizes this as 409 Conflict; MCP returns the "Error: ..." string.

    [Fact]
    public async Task DeleteSize_InUse_RejectedOnBothSurfaces()
    {
        // Arrange — a custom size assigned to one card on each board
        var restBoardId = await SeedBoardAsync();
        var mcpBoardId = await SeedBoardAsync();
        var (restSizeId, _) = await CreateSizeAsync(restBoardId, "REST-Used", 20);
        var (mcpSizeId, _) = await CreateSizeAsync(mcpBoardId, "MCP-Used", 20);
        var restLaneId = await CreateLaneAsync(restBoardId);
        var mcpLaneId = await CreateLaneAsync(mcpBoardId);

        TestAuthHelper.SetAdminAuth(_client, _factory);
        (await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/cards", new { name = "Uses REST size", laneId = restLaneId, sizeId = restSizeId }))
            .EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync($"/api/v1/boards/{mcpBoardId}/cards", new { name = "Uses MCP size", laneId = mcpLaneId, sizeId = mcpSizeId }))
            .EnsureSuccessStatusCode();

        var (db, tools, authKey) = CreateMcpTools();

        // Act — REST
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.DeleteAsync($"/api/v1/sizes/{restSizeId}");

        // Act — MCP
        var mcpResult = await tools.DeleteSizeAsync(authKey, mcpSizeId);

        // Assert — REST 409 Conflict, MCP "Error: ..." — same rule, same message
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var restBody = await restResponse.Content.ReadAsStringAsync();
        restBody.ShouldContain("Size is in use by cards");
        restBody.ShouldNotContain("Error:");

        mcpResult.ShouldContain("Error: Size is in use by cards");

        // Neither surface deleted its in-use size
        (await db.CardSizes.AnyAsync(s => s.Id == restSizeId)).ShouldBeTrue();
        (await db.CardSizes.AnyAsync(s => s.Id == mcpSizeId)).ShouldBeTrue();
    }
}
