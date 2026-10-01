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

// A size create reads the board's sizes and then inserts, so a concurrent create on the same board
// can take the ordinal or the name in between. The interceptor commits that rival size
// deterministically, inside the real request, on REST and MCP alike: an appended create retries into
// the next free ordinal, an explicit ordinal or a taken name answers 409, and an appended create that
// keeps losing gives up with a clear 409 rather than a 500.
public class SizeCreateRaceTests(SizeCreateRaceFactory factory) : IClassFixture<SizeCreateRaceFactory>, IDisposable
{
    private readonly SizeCreateRaceFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly List<IServiceScope> _scopes = [];

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private SizeTools CreateMcpTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);

        return new SizeTools
        (
            scope.ServiceProvider.GetRequiredService<BoardDbContext>(),
            scope.ServiceProvider.GetRequiredService<McpAuthService>(),
            scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>()
        );
    }

    // A fresh board ships seeded with S/M/L/XL at ordinals 0-3, so the appended ordinal is 4 until a
    // rival takes it.
    private async Task<Guid> SeedBoardAsync()
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var response = await _client.PostAsJsonAsync("/api/v1/boards", new { name = $"Size Race {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<List<int>> OrdinalsAsync(Guid boardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        return await db.CardSizes
            .Where(s => s.BoardId == boardId)
            .OrderBy(s => s.Ordinal)
                .Select(s => s.Ordinal)
                    .ToListAsync();
    }

    private async Task<int> CountNamedAsync(Guid boardId, string name)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        return await db.CardSizes.CountAsync(s => s.BoardId == boardId && s.Name == name);
    }

    [Fact]
    public async Task CreateSize_NoOrdinalLosesTheRace_RetriesIntoTheNextOrdinalOnBothSurfaces()
    {
        // Arrange
        var restBoardId = await SeedBoardAsync();
        var mcpBoardId = await SeedBoardAsync();
        var tools = CreateMcpTools();

        // Act
        _factory.Interceptor.Arm(restBoardId);
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/sizes", new { name = "REST appended" });
        var restFired = _factory.Interceptor.FiredCount;

        _factory.Interceptor.Arm(mcpBoardId);
        var mcpResult = await tools.CreateSizeAsync(_factory.AdminAuthKey, mcpBoardId, "MCP appended");
        var mcpFired = _factory.Interceptor.FiredCount;

        // Assert — the rival took 4, the create re-resolved and landed at 5
        restFired.ShouldBe(1);
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await restResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ordinal").GetInt32().ShouldBe(5);
        (await OrdinalsAsync(restBoardId)).ShouldBe([0, 1, 2, 3, 4, 5]);

        mcpFired.ShouldBe(1);
        JsonSerializer.Deserialize<JsonElement>(mcpResult).GetProperty("ordinal").GetInt32().ShouldBe(5);
        (await OrdinalsAsync(mcpBoardId)).ShouldBe([0, 1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task CreateSize_ExplicitOrdinalLosesTheRace_ConflictsOnBothSurfaces()
    {
        // Arrange
        var restBoardId = await SeedBoardAsync();
        var mcpBoardId = await SeedBoardAsync();
        var tools = CreateMcpTools();

        // Act — 10 is free when checked, then a rival takes it before the insert
        _factory.Interceptor.Arm(restBoardId);
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/sizes", new { name = "REST explicit", ordinal = 10 });

        _factory.Interceptor.Arm(mcpBoardId);
        var mcpResult = await tools.CreateSizeAsync(_factory.AdminAuthKey, mcpBoardId, "MCP explicit", 10);

        // Assert — the same answer as an ordinal taken before the call
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("Ordinal already taken by another size");
        (await OrdinalsAsync(restBoardId)).ShouldBe([0, 1, 2, 3, 10]);

        mcpResult.ShouldContain("Error: Ordinal already taken by another size");
        (await OrdinalsAsync(mcpBoardId)).ShouldBe([0, 1, 2, 3, 10]);
    }

    [Fact]
    public async Task CreateSize_NameTakenDuringTheCreate_ConflictsOnBothSurfaces()
    {
        // Arrange
        var restBoardId = await SeedBoardAsync();
        var mcpBoardId = await SeedBoardAsync();
        var tools = CreateMcpTools();

        // Act — the name is free when checked, then a rival commits it under another ordinal
        _factory.Interceptor.Arm(restBoardId, SizeRivalCollision.Name);
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{restBoardId}/sizes", new { name = "Huge" });
        var restFired = _factory.Interceptor.FiredCount;

        _factory.Interceptor.Arm(mcpBoardId, SizeRivalCollision.Name);
        var mcpResult = await tools.CreateSizeAsync(_factory.AdminAuthKey, mcpBoardId, "Huge");

        // Assert — a name collision is answered as a name conflict on the first retry, not retried as
        // though it were an ordinal one
        restFired.ShouldBe(1);
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("A size with that name already exists on this board");
        (await CountNamedAsync(restBoardId, "Huge")).ShouldBe(1);

        mcpResult.ShouldContain("Error: A size with that name already exists on this board");
        (await CountNamedAsync(mcpBoardId, "Huge")).ShouldBe(1);
    }

    [Fact]
    public async Task CreateSize_NoOrdinalLosesEveryAttempt_ConflictsWithAReason()
    {
        // Arrange — losing on all eight attempts exhausts the budget; losing on seven still lands
        var exhaustedBoardId = await SeedBoardAsync();
        var lastAttemptBoardId = await SeedBoardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        _factory.Interceptor.Arm(exhaustedBoardId, collisions: 8);
        var exhausted = await _client.PostAsJsonAsync($"/api/v1/boards/{exhaustedBoardId}/sizes", new { name = "Never lands" });
        var exhaustedFired = _factory.Interceptor.FiredCount;

        _factory.Interceptor.Arm(lastAttemptBoardId, collisions: 7);
        var lastAttempt = await _client.PostAsJsonAsync($"/api/v1/boards/{lastAttemptBoardId}/sizes", new { name = "Lands last" });

        // Assert — seven rivals took 4 through 10, so the eighth attempt lands at 11
        exhaustedFired.ShouldBe(8);
        exhausted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await exhausted.Content.ReadAsStringAsync()).ShouldContain("being created on this board at the same time");

        lastAttempt.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await lastAttempt.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ordinal").GetInt32().ShouldBe(11);
    }
}
