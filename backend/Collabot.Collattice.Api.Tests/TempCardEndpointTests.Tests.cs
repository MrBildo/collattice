using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class TempCardEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> GetFirstLaneIdAsync()
        => await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

    private async Task<Guid> CreateTempCardAsync(Guid? laneId = null)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var effectiveLaneId = laneId ?? await GetFirstLaneIdAsync();

        var payload = new
        {
            name = "Temp Card",
            descriptionMarkdown = "A temp card for testing",
            laneId = effectiveLaneId,
            position = Random.Shared.Next(10000, 99999),
        };

        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards/temp",
            payload
        );
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateNormalCardAsync()
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var payload = new
        {
            name = "Normal Card",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999),
        };

        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            payload
        );
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task CreateTempCard_Returns201WithId()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var payload = new
        {
            name = "Temp Card 201 Test",
            descriptionMarkdown = "desc",
            laneId,
        };

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards/temp",
            payload
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        json.TryGetProperty("id", out var idProp).ShouldBeTrue();
        idProp.GetGuid().ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task TempCard_ExcludedFromCardListings()
    {
        // Arrange
        var tempCardId = await CreateTempCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>(TestAuthHelper.JsonOptions);
        paged.ShouldNotBeNull();

        var ids = paged.Items.Select(c => c.GetProperty("id").GetGuid()).ToList();
        ids.ShouldNotContain(tempCardId);
    }

    [Fact]
    public async Task TempCard_ExcludedFromCompositeView()
    {
        // Arrange
        var tempCardId = await CreateTempCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/board");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var cards = json.GetProperty("cards").EnumerateArray().ToList();
        var ids = cards.Select(c => c.GetProperty("id").GetGuid()).ToList();
        ids.ShouldNotContain(tempCardId);
    }

    [Fact]
    public async Task FinalizeTempCard_AssignsNumber()
    {
        // Arrange
        var tempCardId = await CreateTempCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsync
        (
            $"/api/v1/cards/{tempCardId}/finalize",
            null
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        json.GetProperty("id").GetGuid().ShouldBe(tempCardId);
        json.GetProperty("number").GetInt64().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task FinalizedCard_AppearsInListings()
    {
        // Arrange
        var tempCardId = await CreateTempCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        await _client.PostAsync($"/api/v1/cards/{tempCardId}/finalize", null);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>(TestAuthHelper.JsonOptions);
        paged.ShouldNotBeNull();

        var ids = paged.Items.Select(c => c.GetProperty("id").GetGuid()).ToList();
        ids.ShouldContain(tempCardId);
    }

    [Fact]
    public async Task FinalizeNonTempCard_Returns400()
    {
        // Arrange
        var normalCardId = await CreateNormalCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsync
        (
            $"/api/v1/cards/{normalCardId}/finalize",
            null
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task FinalizeByNonCreator_Returns403()
    {
        // Arrange
        var tempCardId = await CreateTempCardAsync();
        var otherUser = await TestAuthHelper.CreateUserAsync
        (
            _client,
            _factory,
            "Other User Finalize",
            UserRole.HumanUser
        );
        TestAuthHelper.SetAuth(_client, otherUser.AuthKey);

        // Act
        var response = await _client.PostAsync
        (
            $"/api/v1/cards/{tempCardId}/finalize",
            null
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CancelTempCard_Returns204()
    {
        // Arrange
        var tempCardId = await CreateTempCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsync
        (
            $"/api/v1/cards/{tempCardId}/cancel",
            null
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CancelTempCard_DeletesCardAndAttachments()
    {
        // Arrange
        var tempCardId = await CreateTempCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Upload an attachment to the temp card
        var fileContent = new MultipartFormDataContent();
        var byteContent = new ByteArrayContent([1, 2, 3, 4]);
        byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        fileContent.Add(byteContent, "file", "test-attachment.bin");

        var uploadResponse = await _client.PostAsync
        (
            $"/api/v1/cards/{tempCardId}/attachments",
            fileContent
        );
        uploadResponse.EnsureSuccessStatusCode();
        var uploadJson = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var attachmentId = uploadJson.GetProperty("id").GetGuid();

        // Act
        var cancelResponse = await _client.PostAsync
        (
            $"/api/v1/cards/{tempCardId}/cancel",
            null
        );

        // Assert
        cancelResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Card should be gone
        var getCardResponse = await _client.GetAsync($"/api/v1/cards/{tempCardId}");
        getCardResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Attachment should be gone
        var getAttachmentResponse = await _client.GetAsync($"/api/v1/attachments/{attachmentId}");
        getAttachmentResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CancelNonTempCard_Returns400()
    {
        // Arrange
        var normalCardId = await CreateNormalCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsync
        (
            $"/api/v1/cards/{normalCardId}/cancel",
            null
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReorderTempCard_Returns400()
    {
        // Arrange
        var tempCardId = await CreateTempCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/cards/{tempCardId}/reorder",
            new { laneId, index = 0 }
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ArchiveTempCard_Returns400()
    {
        // Arrange
        var tempCardId = await CreateTempCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsync
        (
            $"/api/v1/cards/{tempCardId}/archive",
            null
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UploadAttachmentToTempCard_Succeeds()
    {
        // Arrange
        var tempCardId = await CreateTempCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);

        var fileContent = new MultipartFormDataContent();
        var byteContent = new ByteArrayContent([10, 20, 30]);
        byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        fileContent.Add(byteContent, "file", "temp-card-attachment.bin");

        // Act
        var response = await _client.PostAsync
        (
            $"/api/v1/cards/{tempCardId}/attachments",
            fileContent
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        json.TryGetProperty("id", out var idProp).ShouldBeTrue();
        idProp.GetGuid().ShouldNotBe(Guid.Empty);
    }

    // The three tests below exercise the shared BuildCardAsync path.
    // Standard-create tests already cover name/lane/label validation via CardEndpointTests;
    // these confirm the temp-create path routes through the same logic identically.

    [Fact]
    public async Task CreateTempCard_EmptyName_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var payload = new { name = "  ", descriptionMarkdown = "", laneId };

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards/temp",
            payload
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTempCard_DefaultsToLowestOrdinalSize()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        // The smallest-ordinal size is "S" — omit sizeId and confirm the created card carries it
        var sizeIdS = await TestDataHelper.GetSizeIdByNameAsync(_client, _factory.DefaultBoardId, "S");

        var payload = new { name = "Size-Default Temp Card", descriptionMarkdown = "", laneId };

        var createResponse = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards/temp",
            payload
        );
        createResponse.EnsureSuccessStatusCode();
        var json = await createResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var tempCardId = json.GetProperty("id").GetGuid();

        // Finalize so we can inspect via GET /cards/{id}
        await _client.PostAsync($"/api/v1/cards/{tempCardId}/finalize", null);

        // Act
        var getResponse = await _client.GetAsync($"/api/v1/cards/{tempCardId}");

        // Assert
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await getResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        detail.GetProperty("card").GetProperty("sizeId").GetGuid().ShouldBe(sizeIdS);
    }

    [Fact]
    public async Task CreateTempCard_InvalidLabel_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var payload = new
        {
            name = "Temp Card With Bad Label",
            descriptionMarkdown = "",
            laneId,
            labelIds = new[] { Guid.NewGuid() },
        };

        // Act
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards/temp",
            payload
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
