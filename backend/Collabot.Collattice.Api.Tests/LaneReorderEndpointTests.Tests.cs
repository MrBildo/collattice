using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// REST sibling for whole-board lane reordering
// (POST /boards/{boardId}/lanes/reorder). Each test builds a fresh board so it
// owns the exact non-archive lane set, then drives the reorder over the wire.
// The load-bearing case is the swap: reversing two adjacent lanes forces an
// intermediate state where each wants the other's position, which the unique
// (BoardId, Position) index rejects on a naive single-phase save. The two-phase
// renumber in LaneReorderHelper is what makes it persist.
public class LaneReorderEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> CreateBoardAsync(string name)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync("/api/v1/boards", new { name });
        response.EnsureSuccessStatusCode();
        var board = await response.Content.ReadFromJsonAsync<JsonElement>();
        return board.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateLaneAsync(Guid boardId, string name, int position)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes", new { name, position });
        response.EnsureSuccessStatusCode();
        var lane = await response.Content.ReadFromJsonAsync<JsonElement>();
        return lane.GetProperty("id").GetGuid();
    }

    private async Task<List<(Guid Id, int Position, string Name)>> GetLanesAsync(Guid boardId)
    {
        var response = await _client.GetAsync($"/api/v1/boards/{boardId}/lanes");
        response.EnsureSuccessStatusCode();
        var lanes = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        return [.. lanes!.Select(l => (l.GetProperty("id").GetGuid(), l.GetProperty("position").GetInt32(), l.GetProperty("name").GetString()!))];
    }

    [Fact]
    public async Task Reorder_SwapsTwoAdjacentLanes_PersistsUnderUniqueIndex()
    {
        // Arrange — exactly two lanes at 0 and 1 (the case that collides on a swap)
        var boardId = await CreateBoardAsync($"reorder-swap-{Guid.NewGuid():N}");
        var laneA = await CreateLaneAsync(boardId, "A", 0);
        var laneB = await CreateLaneAsync(boardId, "B", 1);

        // Act — reverse the order
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes/reorder", new { laneIds = new[] { laneB, laneA } });

        // Assert — the swap persisted, dense 0..1 in the requested order
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var lanes = await GetLanesAsync(boardId);
        lanes.Count.ShouldBe(2);
        lanes[0].Id.ShouldBe(laneB);
        lanes[0].Position.ShouldBe(0);
        lanes[1].Id.ShouldBe(laneA);
        lanes[1].Position.ShouldBe(1);
    }

    [Fact]
    public async Task Reorder_FullReverse_AssignsDensePositions()
    {
        // Arrange — five lanes, 0..4
        var boardId = await CreateBoardAsync($"reorder-reverse-{Guid.NewGuid():N}");
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await CreateLaneAsync(boardId, "L" + i.ToString(CultureInfo.InvariantCulture), i));
        }

        var reversed = Enumerable.Reverse(ids).ToArray();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes/reorder", new { laneIds = reversed });

        // Assert — exact reverse order, dense 0..4
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var lanes = await GetLanesAsync(boardId);
        lanes.Select(l => l.Id).ShouldBe(reversed);
        lanes.Select(l => l.Position).ShouldBe([0, 1, 2, 3, 4]);
    }

    [Fact]
    public async Task Reorder_LanesWithGappedPositions_NormalizesExistingGap()
    {
        // Arrange — lanes with a gap (0, 2, 5); the reorder should densify to 0..2
        var boardId = await CreateBoardAsync($"reorder-gap-{Guid.NewGuid():N}");
        var lane0 = await CreateLaneAsync(boardId, "G0", 0);
        var lane2 = await CreateLaneAsync(boardId, "G2", 2);
        var lane5 = await CreateLaneAsync(boardId, "G5", 5);

        // Act — keep the same visual order, which still normalizes the gaps
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes/reorder", new { laneIds = new[] { lane0, lane2, lane5 } });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var lanes = await GetLanesAsync(boardId);
        lanes.Select(l => l.Id).ShouldBe([lane0, lane2, lane5]);
        lanes.Select(l => l.Position).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task Reorder_MissingLane_FailsLoudAndMutatesNothing()
    {
        // Arrange — three lanes, but the request omits one
        var boardId = await CreateBoardAsync($"reorder-missing-{Guid.NewGuid():N}");
        var laneA = await CreateLaneAsync(boardId, "A", 0);
        var laneB = await CreateLaneAsync(boardId, "B", 1);
        var laneC = await CreateLaneAsync(boardId, "C", 2);

        var before = await GetLanesAsync(boardId);

        // Act — set is short by one (a stale/mismatched set)
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes/reorder", new { laneIds = new[] { laneB, laneA } });

        // Assert — rejected, nothing changed
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var after = await GetLanesAsync(boardId);
        after.Select(l => (l.Id, l.Position)).ShouldBe(before.Select(l => (l.Id, l.Position)));
        _ = laneC;
    }

    [Fact]
    public async Task Reorder_ExtraUnknownLane_FailsLoudAndMutatesNothing()
    {
        // Arrange
        var boardId = await CreateBoardAsync($"reorder-extra-{Guid.NewGuid():N}");
        var laneA = await CreateLaneAsync(boardId, "A", 0);
        var laneB = await CreateLaneAsync(boardId, "B", 1);

        var before = await GetLanesAsync(boardId);

        // Act — include a lane id that isn't on this board
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes/reorder", new { laneIds = new[] { laneA, laneB, Guid.NewGuid() } });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var after = await GetLanesAsync(boardId);
        after.Select(l => (l.Id, l.Position)).ShouldBe(before.Select(l => (l.Id, l.Position)));
    }

    [Fact]
    public async Task Reorder_DuplicateLane_FailsLoud()
    {
        // Arrange
        var boardId = await CreateBoardAsync($"reorder-dupe-{Guid.NewGuid():N}");
        var laneA = await CreateLaneAsync(boardId, "A", 0);
        var laneB = await CreateLaneAsync(boardId, "B", 1);

        // Act — same id twice
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes/reorder", new { laneIds = new[] { laneA, laneA } });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _ = laneB;
    }

    [Fact]
    public async Task Reorder_AsHumanUser_Returns403()
    {
        // Arrange
        var boardId = await CreateBoardAsync($"reorder-role-{Guid.NewGuid():N}");
        var laneA = await CreateLaneAsync(boardId, "A", 0);
        var laneB = await CreateLaneAsync(boardId, "B", 1);

        var user = await TestAuthHelper.CreateUserAsync(_client, _factory, $"reorder-human-{Guid.NewGuid():N}", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, user.AuthKey);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes/reorder", new { laneIds = new[] { laneB, laneA } });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Reorder_NonexistentBoard_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{Guid.NewGuid()}/lanes/reorder", new { laneIds = new[] { Guid.NewGuid() } });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
