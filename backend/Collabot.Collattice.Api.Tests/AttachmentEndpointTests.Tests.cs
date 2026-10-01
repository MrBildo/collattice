using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class AttachmentEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> CreateCardAsync()
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

        var cardPayload = new
        {
            name = "Test Card",
            descriptionMarkdown = "Card for attachment tests",
            laneId,
            position = Random.Shared.Next(10000, 99999),
        };

        var cardResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", cardPayload);
        cardResponse.EnsureSuccessStatusCode();
        var card = await cardResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return card.GetProperty("id").GetGuid();
    }

    private static MultipartFormDataContent CreateFileUpload(byte[]? data = null, string fileName = "test.bin")
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(data ?? [1, 2, 3, 4]);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", fileName);
        return content;
    }

    private async Task<Guid> UploadAttachmentAsync(Guid cardId)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var upload = CreateFileUpload();
        var response = await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", upload);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task GetCardAttachments_ReturnsMetadataWithoutPayload()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        await UploadAttachmentAsync(cardId);
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/attachments");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var attachments = await response.Content.ReadFromJsonAsync<JsonElement[]>(TestAuthHelper.JsonOptions);
        attachments.ShouldNotBeNull();
        attachments.ShouldNotBeEmpty();

        var first = attachments[0];
        first.TryGetProperty("id", out _).ShouldBeTrue();
        first.TryGetProperty("fileName", out _).ShouldBeTrue();
        first.TryGetProperty("contentType", out _).ShouldBeTrue();
        first.TryGetProperty("fileSize", out var fileSizeProp).ShouldBeTrue();
        fileSizeProp.GetInt64().ShouldBe(4);
        first.TryGetProperty("addedByUserId", out _).ShouldBeTrue();
        first.TryGetProperty("addedAtUtc", out _).ShouldBeTrue();
        first.TryGetProperty("payload", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task GetCardAttachments_NonexistentCard_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var fakeCardId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{fakeCardId}/attachments");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostAttachment_OnExistingCard_Returns201()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var upload = CreateFileUpload(fileName: "document.bin");

        // Act
        var response = await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", upload);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        json.TryGetProperty("id", out var idProp).ShouldBeTrue();
        idProp.GetGuid().ShouldNotBe(Guid.Empty);
        json.GetProperty("fileName").GetString().ShouldBe("document.bin");
    }

    [Fact]
    public async Task PostAttachment_SixMb_OverMcpCapUnderRestCap_Succeeds()
    {
        // Arrange — 6MB exceeds the 5MB MCP cap but is well under the 50MB REST cap;
        // the REST path must accept it (the doc'd 5MB-MCP / 50MB-REST split).
        var cardId = await CreateCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var sixMb = new byte[6 * 1024 * 1024];
        var upload = CreateFileUpload(sixMb, "six-mb.bin");

        // Act
        var response = await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", upload);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        json.GetProperty("fileName").GetString().ShouldBe("six-mb.bin");
    }

    [Fact]
    public async Task PostAttachment_OnNonexistentCard_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var fakeCardId = Guid.NewGuid();
        var upload = CreateFileUpload();

        // Act
        var response = await _client.PostAsync($"/api/v1/cards/{fakeCardId}/attachments", upload);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAttachment_ReturnsFileWithCorrectContent()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var fileBytes = new byte[] { 10, 20, 30, 40, 50 };
        var upload = CreateFileUpload(fileBytes, "payload.bin");

        var uploadResponse = await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", upload);
        uploadResponse.EnsureSuccessStatusCode();
        var uploadJson = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var attachmentId = uploadJson.GetProperty("id").GetGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/attachments/{attachmentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var downloadedBytes = await response.Content.ReadAsByteArrayAsync();
        downloadedBytes.ShouldBe(fileBytes);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/octet-stream");
        response.Content.Headers.ContentDisposition.ShouldNotBeNull();
        response.Content.Headers.ContentDisposition.ToString().ShouldContain("payload.bin");
    }

    [Fact]
    public async Task GetAttachment_NonexistentAttachment_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var fakeId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/attachments/{fakeId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAttachment_Unauthenticated_Returns401()
    {
        // Arrange — a real attachment exists; the auth gate must fire before it is served.
        // This locks the documented posture: GET /attachments/{id} requires auth.
        // The endpoint is consumed only via the authenticated download path, never a
        // browser-native <img> tag, so the SSE-unauth precedent does not apply.
        var cardId = await CreateCardAsync();
        var attachmentId = await UploadAttachmentAsync(cardId);
        _client.DefaultRequestHeaders.Remove("X-User-Key");

        // Act
        var response = await _client.GetAsync($"/api/v1/attachments/{attachmentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteAttachment_AsAdmin_Returns204()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        var attachmentId = await UploadAttachmentAsync(cardId);
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.DeleteAsync($"/api/v1/attachments/{attachmentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteAttachment_OwnAttachment_Returns204()
    {
        // Arrange — human uploads their own attachment
        var cardId = await CreateCardAsync();
        var human = await TestAuthHelper.CreateUserAsync(_client, _factory, "Human Deleter", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, human.AuthKey);

        var upload = CreateFileUpload();
        var uploadResponse = await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", upload);
        uploadResponse.EnsureSuccessStatusCode();
        var uploadJson = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        var attachmentId = uploadJson.GetProperty("id").GetGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/attachments/{attachmentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteAttachment_AsAgentUser_Returns403()
    {
        // Arrange
        var cardId = await CreateCardAsync();
        var attachmentId = await UploadAttachmentAsync(cardId);
        var agent = await TestAuthHelper.CreateUserAsync(_client, _factory, "Agent Forbidden Delete", UserRole.AgentUser);
        TestAuthHelper.SetAuth(_client, agent.AuthKey);

        // Act
        var response = await _client.DeleteAsync($"/api/v1/attachments/{attachmentId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteAttachment_NonexistentAttachment_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var fakeId = Guid.NewGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/attachments/{fakeId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
