using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class SizeEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetSizes_DefaultBoard_ReturnsOrderedList()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var sizes = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        sizes.ShouldNotBeNull();
        sizes.ShouldNotBeEmpty();

        // Verify ordering by ordinal
        for (var i = 1; i < sizes.Length; i++)
        {
            sizes[i].GetProperty("ordinal").GetInt32()
                .ShouldBeGreaterThanOrEqualTo(sizes[i - 1].GetProperty("ordinal").GetInt32());
        }
    }

    [Fact]
    public async Task GetSizes_DefaultBoard_HasSMLXL()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes");
        response.EnsureSuccessStatusCode();

        var sizes = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        var names = sizes!.Select(s => s.GetProperty("name").GetString()).ToList();

        // Assert
        names.ShouldContain("S");
        names.ShouldContain("M");
        names.ShouldContain("L");
        names.ShouldContain("XL");
    }

    [Fact]
    public async Task GetSize_ById_Returns200()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name = "GetById Size", ordinal = 200 });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sizeId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/sizes/{sizeId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var size = await response.Content.ReadFromJsonAsync<JsonElement>();
        size.GetProperty("id").GetGuid().ShouldBe(sizeId);
        size.GetProperty("name").GetString().ShouldBe("GetById Size");
    }

    [Fact]
    public async Task GetSizeById_NonexistentSize_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/sizes/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostSize_AsAdmin_Returns201()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name = "XXL", ordinal = 100 });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var size = await response.Content.ReadFromJsonAsync<JsonElement>();
        size.GetProperty("name").GetString().ShouldBe("XXL");
        size.GetProperty("ordinal").GetInt32().ShouldBe(100);
    }

    [Fact]
    public async Task PostSize_AsHumanUser_Returns403()
    {
        // Arrange
        var user = await TestAuthHelper.CreateUserAsync(_client, _factory, "HumanSizeTester", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, user.AuthKey);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name = "Blocked", ordinal = 50 });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PatchSize_UpdatesName_Returns200()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name = "PatchBefore", ordinal = 201 });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sizeId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/sizes/{sizeId}", new { name = "PatchAfter" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var size = await response.Content.ReadFromJsonAsync<JsonElement>();
        size.GetProperty("name").GetString().ShouldBe("PatchAfter");
    }

    [Fact]
    public async Task PatchSize_ConflictingOrdinal_Returns409()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name = "OrdConflict1", ordinal = 300 });

        var size2Response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name = "OrdConflict2", ordinal = 301 });
        size2Response.EnsureSuccessStatusCode();
        var size2 = await size2Response.Content.ReadFromJsonAsync<JsonElement>();
        var size2Id = size2.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/sizes/{size2Id}", new { ordinal = 300 });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DeleteSize_UnusedSize_Returns204()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name = "Disposable", ordinal = 999 });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sizeId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/sizes/{sizeId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteSize_SizeInUseByCards_Returns409()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Get a size that's in use (create a card with it)
        var sizesResponse = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes");
        sizesResponse.EnsureSuccessStatusCode();
        var sizes = await sizesResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        var sizeId = sizes![0].GetProperty("id").GetGuid();

        var lanesResponse = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/board");
        lanesResponse.EnsureSuccessStatusCode();
        var board = await lanesResponse.Content.ReadFromJsonAsync<JsonElement>();
        var laneId = board.GetProperty("lanes")[0].GetProperty("id").GetGuid();

        await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Blocks Size Delete",
            laneId,
            sizeId,
            position = 0
        });

        // Act
        var response = await _client.DeleteAsync($"/api/v1/sizes/{sizeId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DeleteSize_NonexistentSize_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.DeleteAsync($"/api/v1/sizes/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteSize_AsHumanUser_Returns403()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name = "HumanCantDelete", ordinal = 998 });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sizeId = created.GetProperty("id").GetGuid();

        var user = await TestAuthHelper.CreateUserAsync(_client, _factory, "HumanSizeDeleter", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, user.AuthKey);

        // Act
        var response = await _client.DeleteAsync($"/api/v1/sizes/{sizeId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PostSize_WithEmptyName_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name = "  ", ordinal = 500 });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchSize_WithEmptyName_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name = "PatchEmptySize", ordinal = 501 });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sizeId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/sizes/{sizeId}", new { name = "" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostSize_WithoutOrdinal_AutoAssignsNextOrdinal()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Get current max ordinal
        var sizesResponse = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes");
        sizesResponse.EnsureSuccessStatusCode();
        var existingSizes = await sizesResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        var maxOrdinal = existingSizes!.Max(s => s.GetProperty("ordinal").GetInt32());

        // Act — post without specifying ordinal (ordinal defaults to 0, triggers auto-assign)
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name = $"AutoOrd-{Guid.NewGuid()}" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var size = await response.Content.ReadFromJsonAsync<JsonElement>();
        size.GetProperty("ordinal").GetInt32().ShouldBe(maxOrdinal + 1);
    }

    [Fact]
    public async Task PostSize_WithExplicitOrdinal_RespectsProvidedValue()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act — explicitly pass ordinal: 5 (should NOT be auto-assigned)
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/sizes", new { name = $"Explicit-{Guid.NewGuid()}", ordinal = 5 });

        // Assert — should keep ordinal 5, not auto-assign to max+1
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var size = await response.Content.ReadFromJsonAsync<JsonElement>();
        size.GetProperty("ordinal").GetInt32().ShouldBe(5);
    }
}
