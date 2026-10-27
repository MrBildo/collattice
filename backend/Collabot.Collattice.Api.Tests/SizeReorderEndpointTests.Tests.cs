using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// REST tests for whole-board size reordering
// (POST /boards/{boardId}/sizes/reorder), mirroring the lane reorder.
// Each test creates a fresh board so it owns the exact size set the board was
// seeded with (S/M/L/XL at ordinals 0..3), then drives the reorder over the
// wire. The load-bearing case is the swap: reversing two adjacent sizes forces
// an intermediate state where each wants the other's ordinal, which the unique
// (BoardId, Ordinal) index rejects on a naive single-phase save. The two-phase
// renumber in SizeReorderHelper is what makes it persist.
public class SizeReorderEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
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

    private async Task<Guid> CreateSizeAsync(Guid boardId, string name)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/sizes", new { name });
        response.EnsureSuccessStatusCode();
        var size = await response.Content.ReadFromJsonAsync<JsonElement>();
        return size.GetProperty("id").GetGuid();
    }

    private async Task<List<(Guid Id, int Ordinal, string Name)>> GetSizesAsync(Guid boardId)
    {
        var response = await _client.GetAsync($"/api/v1/boards/{boardId}/sizes");
        response.EnsureSuccessStatusCode();
        var sizes = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        return [.. sizes!.Select(s => (s.GetProperty("id").GetGuid(), s.GetProperty("ordinal").GetInt32(), s.GetProperty("name").GetString()!))];
    }

    [Fact]
    public async Task Reorder_SwapsTwoAdjacentSizes_PersistsUnderUniqueIndex()
    {
        // Arrange — a fresh board with the default S/M/L/XL set; take the first two
        var boardId = await CreateBoardAsync($"size-reorder-swap-{Guid.NewGuid():N}");
        var before = await GetSizesAsync(boardId);
        before.Count.ShouldBe(4);
        var (firstId, _, _) = before[0];
        var (secondId, _, _) = before[1];

        // Act — swap the first two (the case that collides on a naive save)
        var requested = new[] { secondId, firstId, before[2].Id, before[3].Id };
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/sizes/reorder", new { sizeIds = requested });

        // Assert — the swap persisted, dense 0..3 in the requested order
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await GetSizesAsync(boardId);
        after.Select(s => s.Id).ShouldBe(requested);
        after.Select(s => s.Ordinal).ShouldBe([0, 1, 2, 3]);
    }

    [Fact]
    public async Task Reorder_FullReverse_AssignsDenseOrdinals()
    {
        // Arrange — the default four sizes, 0..3
        var boardId = await CreateBoardAsync($"size-reorder-reverse-{Guid.NewGuid():N}");
        var before = await GetSizesAsync(boardId);
        var reversed = before.Select(s => s.Id).Reverse().ToArray();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/sizes/reorder", new { sizeIds = reversed });

        // Assert — exact reverse order, dense 0..3
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await GetSizesAsync(boardId);
        after.Select(s => s.Id).ShouldBe(reversed);
        after.Select(s => s.Ordinal).ShouldBe([0, 1, 2, 3]);
    }

    [Fact]
    public async Task Reorder_SizesWithGappedOrdinals_NormalizesExistingGap()
    {
        // Arrange — a board whose ordinals have a gap. The default set is dense
        // 0..3; deleting the middle two and adding fresh ones via the auto-ordinal
        // path (MaxOrdinal + 1) produces a sparse set (0, 3, 4, 5). A reorder
        // keeping visual order should still densify to 0..3.
        var boardId = await CreateBoardAsync($"size-reorder-gap-{Guid.NewGuid():N}");
        var seeded = await GetSizesAsync(boardId);

        // Delete M (ordinal 1) and L (ordinal 2), leaving S (0) and XL (3).
        foreach (var (sizeId, _, _) in seeded.Where(s => s.Name is "M" or "L"))
        {
            (await _client.DeleteAsync($"/api/v1/sizes/{sizeId}")).EnsureSuccessStatusCode();
        }

        // Add two new sizes — auto-ordinal puts them at 4 and 5 (max 3 + 1, + 1).
        await CreateSizeAsync(boardId, "Huge");
        await CreateSizeAsync(boardId, "Tiny");

        var sparse = await GetSizesAsync(boardId);
        sparse.Select(s => s.Ordinal).ShouldBe([0, 3, 4, 5]);

        // Act — keep the same visual order, which still normalizes the gaps
        var requested = sparse.Select(s => s.Id).ToArray();
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/sizes/reorder", new { sizeIds = requested });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await GetSizesAsync(boardId);
        after.Select(s => s.Id).ShouldBe(requested);
        after.Select(s => s.Ordinal).ShouldBe([0, 1, 2, 3]);
    }

    [Fact]
    public async Task Reorder_MissingSize_FailsLoudAndMutatesNothing()
    {
        // Arrange — the four default sizes, but the request omits one
        var boardId = await CreateBoardAsync($"size-reorder-missing-{Guid.NewGuid():N}");
        var before = await GetSizesAsync(boardId);

        // Act — set is short by one (a stale/mismatched set)
        var requested = before.Take(3).Select(s => s.Id).ToArray();
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/sizes/reorder", new { sizeIds = requested });

        // Assert — rejected, nothing changed
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var after = await GetSizesAsync(boardId);
        after.Select(s => (s.Id, s.Ordinal)).ShouldBe(before.Select(s => (s.Id, s.Ordinal)));
    }

    [Fact]
    public async Task Reorder_ExtraUnknownSize_FailsLoudAndMutatesNothing()
    {
        // Arrange
        var boardId = await CreateBoardAsync($"size-reorder-extra-{Guid.NewGuid():N}");
        var before = await GetSizesAsync(boardId);

        // Act — include a size id that isn't on this board
        var requested = before.Select(s => s.Id).Append(Guid.NewGuid()).ToArray();
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/sizes/reorder", new { sizeIds = requested });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var after = await GetSizesAsync(boardId);
        after.Select(s => (s.Id, s.Ordinal)).ShouldBe(before.Select(s => (s.Id, s.Ordinal)));
    }

    [Fact]
    public async Task Reorder_DuplicateSize_FailsLoud()
    {
        // Arrange
        var boardId = await CreateBoardAsync($"size-reorder-dupe-{Guid.NewGuid():N}");
        var before = await GetSizesAsync(boardId);

        // Act — same id twice (and short the set, but the duplicate is caught first)
        var requested = new[] { before[0].Id, before[0].Id };
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/sizes/reorder", new { sizeIds = requested });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reorder_AsHumanUser_Returns403()
    {
        // Arrange
        var boardId = await CreateBoardAsync($"size-reorder-role-{Guid.NewGuid():N}");
        var before = await GetSizesAsync(boardId);
        var requested = before.Select(s => s.Id).Reverse().ToArray();

        var user = await TestAuthHelper.CreateUserAsync(_client, _factory, $"size-reorder-human-{Guid.NewGuid():N}", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, user.AuthKey);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/sizes/reorder", new { sizeIds = requested });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Reorder_AsAgentAdministrator_Returns200()
    {
        // Arrange — confirms the admin-level gate admits AgentAdministrator, not
        // just Administrator (matches the lane reorder gate semantics).
        var boardId = await CreateBoardAsync($"size-reorder-agentadmin-{Guid.NewGuid():N}");
        var before = await GetSizesAsync(boardId);
        var requested = before.Select(s => s.Id).Reverse().ToArray();

        var user = await TestAuthHelper.CreateUserAsync(_client, _factory, $"size-reorder-agentadmin-{Guid.NewGuid():N}", UserRole.AgentAdministrator);
        TestAuthHelper.SetAuth(_client, user.AuthKey);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/sizes/reorder", new { sizeIds = requested });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reorder_NonexistentBoard_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{Guid.NewGuid()}/sizes/reorder", new { sizeIds = new[] { Guid.NewGuid() } });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
