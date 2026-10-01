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

// A size, label or lane update checks that its new name, ordinal or position is free and then saves,
// so a concurrent create, rename or move can take the value in between. The interceptor commits that rival deterministically,
// inside the real request, on REST and MCP alike. The update that loses answers 409 with the same
// message as a value taken before the call, never a 500, and leaves the row as it was.
public class RenameRaceTests(RenameRaceFactory factory) : IClassFixture<RenameRaceFactory>, IDisposable
{
    private readonly RenameRaceFactory _factory = factory;
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

    private T CreateMcpTools<T>(Func<BoardDbContext, McpAuthService, BoardEventBroadcaster, T> create)
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);

        return create
        (
            scope.ServiceProvider.GetRequiredService<BoardDbContext>(),
            scope.ServiceProvider.GetRequiredService<McpAuthService>(),
            scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>()
        );
    }

    private async Task<Guid> PostForIdAsync(string url, object body)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var response = await _client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    // A fresh board ships seeded with S/M/L/XL at ordinals 0-3; the size under test sits at 10.
    private async Task<(Guid BoardId, Guid SizeId)> SeedSizeAsync()
    {
        var boardId = await PostForIdAsync("/api/v1/boards", new { name = $"Rename Race {Guid.NewGuid():N}" });
        var sizeId = await PostForIdAsync($"/api/v1/boards/{boardId}/sizes", new { name = "Original", ordinal = 10 });

        return (boardId, sizeId);
    }

    private async Task<(Guid BoardId, Guid LabelId)> SeedLabelAsync()
    {
        var boardId = await PostForIdAsync("/api/v1/boards", new { name = $"Rename Race {Guid.NewGuid():N}" });
        var labelId = await PostForIdAsync($"/api/v1/boards/{boardId}/labels", new { name = "Original", color = "#111111" });

        return (boardId, labelId);
    }

    // A fresh board ships with only its archive lane; the lane under test sits at 3.
    private async Task<(Guid BoardId, Guid LaneId)> SeedLaneAsync()
    {
        var boardId = await PostForIdAsync("/api/v1/boards", new { name = $"Rename Race {Guid.NewGuid():N}" });
        var laneId = await PostForIdAsync($"/api/v1/boards/{boardId}/lanes", new { name = "Original", position = 3 });

        return (boardId, laneId);
    }

    private async Task<(string Name, int Position)> ReadLaneAsync(Guid laneId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var lane = await db.Lanes.SingleAsync(l => l.Id == laneId);

        return (lane.Name, lane.Position);
    }

    private async Task<(string Name, int Ordinal)> ReadSizeAsync(Guid sizeId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var size = await db.CardSizes.SingleAsync(s => s.Id == sizeId);

        return (size.Name, size.Ordinal);
    }

    private async Task<(string Name, string? Color)> ReadLabelAsync(Guid labelId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        var label = await db.Labels.SingleAsync(l => l.Id == labelId);

        return (label.Name, label.Color);
    }

    [Fact]
    public async Task UpdateSize_NameTakenDuringTheRename_ConflictsOnBothSurfaces()
    {
        // Arrange
        var (restBoardId, restSizeId) = await SeedSizeAsync();
        var (mcpBoardId, mcpSizeId) = await SeedSizeAsync();
        var tools = CreateMcpTools((db, auth, broadcaster) => new SizeTools(db, auth, broadcaster));

        // Act — "Huge" is free when checked, then a rival size commits it before the save
        _factory.Interceptor.Arm(restBoardId, RenameRivalCollision.SizeName);
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PatchAsJsonAsync($"/api/v1/sizes/{restSizeId}", new { name = "Huge" });
        var restFired = _factory.Interceptor.FiredCount;

        _factory.Interceptor.Arm(mcpBoardId, RenameRivalCollision.SizeName);
        var mcpResult = await tools.UpdateSizeAsync(_factory.AdminAuthKey, mcpSizeId, name: "Huge");
        var mcpFired = _factory.Interceptor.FiredCount;

        // Assert — the same answer as a name taken before the call, and the size unchanged
        restFired.ShouldBe(1);
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("A size with that name already exists on this board");
        (await ReadSizeAsync(restSizeId)).ShouldBe(("Original", 10));

        mcpFired.ShouldBe(1);
        mcpResult.ShouldBe("Error: A size with that name already exists on this board.");
        (await ReadSizeAsync(mcpSizeId)).ShouldBe(("Original", 10));
    }

    [Fact]
    public async Task UpdateSize_AfterLosingTheRace_TheNextUpdateOnTheSameContextSavesOnlyItsOwnChange()
    {
        // Arrange — one MCP tools instance, so both calls share a DbContext, as calls in one scope do
        var (boardId, sizeId) = await SeedSizeAsync();
        var tools = CreateMcpTools((db, auth, broadcaster) => new SizeTools(db, auth, broadcaster));

        _factory.Interceptor.Arm(boardId, RenameRivalCollision.SizeName);
        var lost = await tools.UpdateSizeAsync(_factory.AdminAuthKey, sizeId, name: "Huge");

        // Act — the lost rename must not still be pending on the tracked size
        var next = await tools.UpdateSizeAsync(_factory.AdminAuthKey, sizeId, ordinal: 12);

        // Assert
        lost.ShouldStartWith("Error: A size with that name already exists");
        next.ShouldNotStartWith("Error:");
        (await ReadSizeAsync(sizeId)).ShouldBe(("Original", 12));
    }

    [Fact]
    public async Task UpdateSize_OrdinalTakenDuringTheUpdate_ConflictsOnBothSurfaces()
    {
        // Arrange
        var (restBoardId, restSizeId) = await SeedSizeAsync();
        var (mcpBoardId, mcpSizeId) = await SeedSizeAsync();
        var tools = CreateMcpTools((db, auth, broadcaster) => new SizeTools(db, auth, broadcaster));

        // Act — 20 is free when checked, then a rival size commits it before the save
        _factory.Interceptor.Arm(restBoardId, RenameRivalCollision.SizeOrdinal);
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PatchAsJsonAsync($"/api/v1/sizes/{restSizeId}", new { ordinal = 20 });

        _factory.Interceptor.Arm(mcpBoardId, RenameRivalCollision.SizeOrdinal);
        var mcpResult = await tools.UpdateSizeAsync(_factory.AdminAuthKey, mcpSizeId, ordinal: 20);

        // Assert
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("Ordinal already taken by another size");
        (await ReadSizeAsync(restSizeId)).ShouldBe(("Original", 10));

        mcpResult.ShouldBe("Error: Ordinal already taken by another size.");
        (await ReadSizeAsync(mcpSizeId)).ShouldBe(("Original", 10));
    }

    [Fact]
    public async Task UpdateLabel_NameTakenDuringTheRename_ConflictsOnBothSurfaces()
    {
        // Arrange
        var (restBoardId, restLabelId) = await SeedLabelAsync();
        var (mcpBoardId, mcpLabelId) = await SeedLabelAsync();
        var tools = CreateMcpTools((db, auth, broadcaster) => new LabelTools(db, auth, broadcaster));

        // Act — "Urgent" is free when checked, then a rival label commits it before the save; the color
        // rides the same save, so it must not land either
        _factory.Interceptor.Arm(restBoardId, RenameRivalCollision.LabelName);
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PatchAsJsonAsync($"/api/v1/boards/{restBoardId}/labels/{restLabelId}", new { name = "Urgent", color = "#ff0000" });
        var restFired = _factory.Interceptor.FiredCount;

        _factory.Interceptor.Arm(mcpBoardId, RenameRivalCollision.LabelName);
        var mcpResult = await tools.UpdateLabelAsync(_factory.AdminAuthKey, mcpLabelId, name: "Urgent", color: "#ff0000");
        var mcpFired = _factory.Interceptor.FiredCount;

        // Assert
        restFired.ShouldBe(1);
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("A label with that name already exists on this board");
        (await ReadLabelAsync(restLabelId)).ShouldBe(("Original", "#111111"));

        mcpFired.ShouldBe(1);
        mcpResult.ShouldBe("Error: A label with that name already exists on this board.");
        (await ReadLabelAsync(mcpLabelId)).ShouldBe(("Original", "#111111"));
    }

    [Fact]
    public async Task UpdateLabel_AfterLosingTheRace_TheNextUpdateOnTheSameContextSavesOnlyItsOwnChange()
    {
        // Arrange — one MCP tools instance, so both calls share a DbContext, as calls in one scope do
        var (boardId, labelId) = await SeedLabelAsync();
        var tools = CreateMcpTools((db, auth, broadcaster) => new LabelTools(db, auth, broadcaster));

        _factory.Interceptor.Arm(boardId, RenameRivalCollision.LabelName);
        var lost = await tools.UpdateLabelAsync(_factory.AdminAuthKey, labelId, name: "Urgent", color: "#ff0000");

        // Act — the lost rename and color must not still be pending on the tracked label
        var next = await tools.UpdateLabelAsync(_factory.AdminAuthKey, labelId, color: "#222222");

        // Assert
        lost.ShouldStartWith("Error: A label with that name already exists");
        next.ShouldNotStartWith("Error:");
        (await ReadLabelAsync(labelId)).ShouldBe(("Original", "#222222"));
    }

    [Fact]
    public async Task UpdateSizeAndLabel_ValueFreeAgainByTheReread_ConflictsWithTryAgain()
    {
        // Arrange — the rival takes the value for the save and is gone again before the re-read, so the
        // update cannot name what it collided with
        var (sizeBoardId, sizeId) = await SeedSizeAsync();
        var (labelBoardId, labelId) = await SeedLabelAsync();
        var labelTools = CreateMcpTools((db, auth, broadcaster) => new LabelTools(db, auth, broadcaster));

        // Act
        _factory.Interceptor.Arm(sizeBoardId, RenameRivalCollision.SizeName, transient: true);
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var sizeResponse = await _client.PatchAsJsonAsync($"/api/v1/sizes/{sizeId}", new { name = "Huge" });

        _factory.Interceptor.Arm(labelBoardId, RenameRivalCollision.LabelName, transient: true);
        var labelResult = await labelTools.UpdateLabelAsync(_factory.AdminAuthKey, labelId, name: "Urgent");

        // Assert — a 409 that says to retry, and nothing written
        sizeResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await sizeResponse.Content.ReadAsStringAsync()).ShouldContain("landed at the same time; try again");
        (await ReadSizeAsync(sizeId)).ShouldBe(("Original", 10));

        labelResult.ShouldBe("Error: Another change to this board's labels landed at the same time; try again.");
        (await ReadLabelAsync(labelId)).ShouldBe(("Original", "#111111"));
    }

    [Fact]
    public async Task UpdateLane_PositionTakenDuringTheMove_ConflictsOnBothSurfaces()
    {
        // Arrange
        var (restBoardId, restLaneId) = await SeedLaneAsync();
        var (mcpBoardId, mcpLaneId) = await SeedLaneAsync();
        var tools = CreateMcpTools((db, auth, broadcaster) => new LaneTools(db, auth, broadcaster));

        // Act — 7 is free when checked, then a rival lane commits it before the save; the name rides the
        // same save, so it must not land either
        _factory.Interceptor.Arm(restBoardId, RenameRivalCollision.LanePosition);
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PatchAsJsonAsync($"/api/v1/lanes/{restLaneId}", new { name = "Moved", position = 7 });
        var restFired = _factory.Interceptor.FiredCount;

        _factory.Interceptor.Arm(mcpBoardId, RenameRivalCollision.LanePosition);
        var mcpResult = await tools.UpdateLaneAsync(_factory.AdminAuthKey, mcpLaneId, name: "Moved", position: 7);
        var mcpFired = _factory.Interceptor.FiredCount;

        // Assert — the same answer as a position taken before the call, and the lane unchanged
        restFired.ShouldBe(1);
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("Position already taken by another lane");
        (await ReadLaneAsync(restLaneId)).ShouldBe(("Original", 3));

        mcpFired.ShouldBe(1);
        mcpResult.ShouldBe("Error: Position already taken by another lane.");
        (await ReadLaneAsync(mcpLaneId)).ShouldBe(("Original", 3));
    }

    [Fact]
    public async Task UpdateLane_AfterLosingTheRace_TheNextUpdateOnTheSameContextSavesOnlyItsOwnChange()
    {
        // Arrange — one MCP tools instance, so both calls share a DbContext, as calls in one scope do
        var (boardId, laneId) = await SeedLaneAsync();
        var tools = CreateMcpTools((db, auth, broadcaster) => new LaneTools(db, auth, broadcaster));

        _factory.Interceptor.Arm(boardId, RenameRivalCollision.LanePosition);
        var lost = await tools.UpdateLaneAsync(_factory.AdminAuthKey, laneId, name: "Moved", position: 7);

        // Act — the lost move and name must not still be pending on the tracked lane
        var next = await tools.UpdateLaneAsync(_factory.AdminAuthKey, laneId, name: "Renamed");

        // Assert
        lost.ShouldStartWith("Error: Position already taken");
        next.ShouldNotStartWith("Error:");
        (await ReadLaneAsync(laneId)).ShouldBe(("Renamed", 3));
    }

    [Fact]
    public async Task UpdateLane_PositionFreeAgainByTheReread_ConflictsWithTryAgainOnBothSurfaces()
    {
        // Arrange — the rival takes the position for the save and is gone again before the re-read, so
        // the move cannot name what it collided with
        var (restBoardId, restLaneId) = await SeedLaneAsync();
        var (mcpBoardId, mcpLaneId) = await SeedLaneAsync();
        var tools = CreateMcpTools((db, auth, broadcaster) => new LaneTools(db, auth, broadcaster));

        // Act
        _factory.Interceptor.Arm(restBoardId, RenameRivalCollision.LanePosition, transient: true);
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var restResponse = await _client.PatchAsJsonAsync($"/api/v1/lanes/{restLaneId}", new { position = 7 });

        _factory.Interceptor.Arm(mcpBoardId, RenameRivalCollision.LanePosition, transient: true);
        var mcpResult = await tools.UpdateLaneAsync(_factory.AdminAuthKey, mcpLaneId, position: 7);

        // Assert — a 409 that says to retry, and nothing written
        restResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await restResponse.Content.ReadAsStringAsync()).ShouldContain("landed at the same time; try again");
        (await ReadLaneAsync(restLaneId)).ShouldBe(("Original", 3));

        mcpResult.ShouldBe("Error: Another change to this board's lanes landed at the same time; try again.");
        (await ReadLaneAsync(mcpLaneId)).ShouldBe(("Original", 3));
    }
}
