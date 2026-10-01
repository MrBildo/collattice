using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class McpBoardToolTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private BoardTools CreateBoardTools(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<McpAuthService>();
        return new BoardTools(db, authService, scope.ServiceProvider.GetRequiredService<IWebhookSink>());
    }

    [Fact]
    public async Task GetLanes_LanesWithCards_ReturnsCardCountPerLane()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var boardId = _factory.DefaultBoardId;

        // Create two lanes
        var lane1Response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes", new { name = "McpLane1", position = 500 });
        lane1Response.EnsureSuccessStatusCode();
        var lane1 = await lane1Response.Content.ReadFromJsonAsync<JsonElement>();
        var lane1Id = lane1.GetProperty("id").GetGuid();

        var lane2Response = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes", new { name = "McpLane2", position = 501 });
        lane2Response.EnsureSuccessStatusCode();
        var lane2 = await lane2Response.Content.ReadFromJsonAsync<JsonElement>();
        var lane2Id = lane2.GetProperty("id").GetGuid();

        // Add 3 cards to lane1, 1 card to lane2
        for (var i = 0; i < 3; i++)
        {
            var cardResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/cards", new
            {
                name = $"McpCard-L1-{i.ToString(CultureInfo.InvariantCulture)}",
                descriptionMarkdown = "",
                laneId = lane1Id,
                position = i,
                size = "M"
            });
            cardResponse.EnsureSuccessStatusCode();
        }

        var singleCardResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/cards", new
        {
            name = "McpCard-L2-0",
            descriptionMarkdown = "",
            laneId = lane2Id,
            position = 0,
            size = "M"
        });
        singleCardResponse.EnsureSuccessStatusCode();

        // Act — call the MCP tool directly
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateBoardTools(scope);
        var result = await tools.GetLanesAsync(_factory.AdminAuthKey, boardId);

        // Assert
        var lanes = JsonSerializer.Deserialize<JsonElement[]>(result)!;
        var mcpLane1 = lanes.First(l => l.GetProperty("id").GetGuid() == lane1Id);
        var mcpLane2 = lanes.First(l => l.GetProperty("id").GetGuid() == lane2Id);

        mcpLane1.GetProperty("cardCount").GetInt32().ShouldBe(3);
        mcpLane2.GetProperty("cardCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task GetLanes_EmptyLane_ReturnsZeroCardCount()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var boardId = _factory.DefaultBoardId;

        var laneResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{boardId}/lanes", new { name = "McpEmptyLane", position = 502 });
        laneResponse.EnsureSuccessStatusCode();
        var lane = await laneResponse.Content.ReadFromJsonAsync<JsonElement>();
        var laneId = lane.GetProperty("id").GetGuid();

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateBoardTools(scope);
        var result = await tools.GetLanesAsync(_factory.AdminAuthKey, boardId);

        // Assert
        var lanes = JsonSerializer.Deserialize<JsonElement[]>(result)!;
        var emptyLane = lanes.First(l => l.GetProperty("id").GetGuid() == laneId);
        emptyLane.GetProperty("cardCount").GetInt32().ShouldBe(0);
    }
}
