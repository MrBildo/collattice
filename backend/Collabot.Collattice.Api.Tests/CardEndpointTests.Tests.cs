using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class CardEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    // Lane positions are unique per board; this class creates a lane on the shared default board, so
    // a random position could collide on the unique (BoardId, Position) index across the class run.
    // A monotonic counter avoids that. The many random positions elsewhere in this file are CARD
    // positions, which carry no unique index and are fine as-is.
    private static int _nextLanePosition = 10_000;
    private static int NextLanePosition() => Interlocked.Increment(ref _nextLanePosition);

    private async Task<Guid> GetFirstLaneIdAsync()
        => await TestDataHelper.GetFirstLaneIdAsync(_client, _factory.DefaultBoardId);

    private async Task<Guid> GetLaneIdByIndexAsync(int index)
        => await TestDataHelper.GetLaneIdByIndexAsync(_client, _factory.DefaultBoardId, index);

    private async Task<Guid> GetSizeIdByNameAsync(string sizeName)
        => await TestDataHelper.GetSizeIdByNameAsync(_client, _factory.DefaultBoardId, sizeName);

    [Fact]
    public async Task GetCards_BoardWithCards_ReturnsAllCards()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        // Create a card to ensure at least one exists
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "GetCards Test Card",
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        var cards = paged!.Items;
        cards.ShouldNotBeNull();
        cards.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetCards_WithLimit_ReturnsLimitedItemsAndTotalCount()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        for (var i = 0; i < 5; i++)
        {
            var r = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
            {
                name = $"Pagination-Limit-{Guid.NewGuid()}",
                descriptionMarkdown = "",
                laneId,
                position = Random.Shared.Next(10000, 99999)
            });
            r.EnsureSuccessStatusCode();
        }

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?limit=2");
        response.EnsureSuccessStatusCode();
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();

        // Assert
        paged.ShouldNotBeNull();
        paged.Items.Count.ShouldBe(2);
        paged.TotalCount.ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task GetCards_WithOffsetAndLimit_ReturnsCorrectPage()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        for (var i = 0; i < 3; i++)
        {
            var r = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
            {
                name = $"Pagination-Offset-{Guid.NewGuid()}",
                descriptionMarkdown = "",
                laneId,
                position = Random.Shared.Next(10000, 99999)
            });
            r.EnsureSuccessStatusCode();
        }

        // Act — get all, then get with offset, verify different first items
        var allResponse = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?limit=200");
        allResponse.EnsureSuccessStatusCode();
        var allPaged = await allResponse.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();

        var offsetResponse = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?offset=2&limit=2");
        offsetResponse.EnsureSuccessStatusCode();
        var offsetPaged = await offsetResponse.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();

        // Assert
        allPaged.ShouldNotBeNull();
        offsetPaged.ShouldNotBeNull();
        offsetPaged.TotalCount.ShouldBe(allPaged.TotalCount);
        offsetPaged.Items.Count.ShouldBeGreaterThanOrEqualTo(1);

        // First item of offset page should be the third item from the full list
        var thirdItemId = allPaged.Items[2].GetProperty("id").GetGuid();
        offsetPaged.Items[0].GetProperty("id").GetGuid().ShouldBe(thirdItemId);
    }

    [Fact]
    public async Task GetCards_OffsetBeyondTotal_ReturnsEmptyItemsWithTotalCount()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?offset=99999&limit=10");
        response.EnsureSuccessStatusCode();
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();

        // Assert
        paged.ShouldNotBeNull();
        paged.Items.ShouldBeEmpty();
        paged.TotalCount.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GetCards_LimitExceedsMax_ClampedTo200()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act — request limit=999, should be clamped to 200
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?limit=999");
        response.EnsureSuccessStatusCode();
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();

        // Assert — we can't directly verify the clamp, but the response should succeed
        // and return at most 200 items (board likely has fewer)
        paged.ShouldNotBeNull();
        paged.Items.Count.ShouldBeLessThanOrEqualTo(200);
    }

    [Fact]
    public async Task GetCards_LimitZero_ClampedToOne()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var r = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = $"Pagination-Zero-{Guid.NewGuid()}",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        r.EnsureSuccessStatusCode();

        // Act — limit=0 should be clamped to 1
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?limit=0");
        response.EnsureSuccessStatusCode();
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();

        // Assert
        paged.ShouldNotBeNull();
        paged.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetCards_NegativeOffset_ClampedToZero()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?offset=-5&limit=10");
        response.EnsureSuccessStatusCode();
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();

        // Assert — should behave same as offset=0
        paged.ShouldNotBeNull();
        paged.Items.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetCard_ById_ReturnsEnrichedResponse()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "GetById Card",
            descriptionMarkdown = "Find me",
            size = "S",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Add a comment
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Test comment" });

        // Add an attachment
        using var attachContent = new MultipartFormDataContent();
        attachContent.Add(new ByteArrayContent([1, 2, 3]), "file", "test.txt");
        await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", attachContent);

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("card").GetProperty("id").GetGuid().ShouldBe(cardId);
        body.GetProperty("card").GetProperty("name").GetString().ShouldBe("GetById Card");

        // User display names
        body.GetProperty("createdByUserName").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("lastUpdatedByUserName").GetString().ShouldNotBeNullOrEmpty();

        // v1 restores the legacy plain comments array (the v2.0.2 production shape), whole thread,
        // each comment carrying the additive-only createdAtUtc a v2.0.2 client ignores.
        var comments = body.GetProperty("comments");
        comments.ValueKind.ShouldBe(JsonValueKind.Array);
        comments.GetArrayLength().ShouldBeGreaterThan(0);
        comments[0].TryGetProperty("userName", out _).ShouldBeTrue();
        comments[0].TryGetProperty("createdAtUtc", out _).ShouldBeTrue();

        // Labels array present
        body.TryGetProperty("labels", out _).ShouldBeTrue();

        // Attachments
        var attachments = body.GetProperty("attachments");
        attachments.GetArrayLength().ShouldBeGreaterThan(0);
        attachments[0].GetProperty("fileName").GetString().ShouldBe("test.txt");
        attachments[0].TryGetProperty("payload", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task GetCardById_NonexistentCard_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var bogusId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{bogusId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostCard_AsHumanUser_Returns201WithAutoNumberAndTimestamps()
    {
        // Arrange
        var user = await TestAuthHelper.CreateUserAsync(_client, _factory, "HumanCardCreator", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, user.AuthKey);
        var laneId = await GetFirstLaneIdAsync();

        var request = new
        {
            name = "My First Card",
            descriptionMarkdown = "Some description",
            size = "L",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        card.GetProperty("number").GetInt64().ShouldBeGreaterThan(0);
        card.GetProperty("createdByUserId").GetGuid().ShouldBe(user.Id);
        card.GetProperty("createdAtUtc").GetDateTimeOffset().ShouldNotBe(default);
        card.GetProperty("lastUpdatedAtUtc").GetDateTimeOffset().ShouldNotBe(default);
    }

    [Fact]
    public async Task PostCard_ThreeCreates_AutoNumbersSequentially()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        // Act
        List<long> numbers = [];
        for (var i = 0; i < 3; i++)
        {
            var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
            {
                name = $"Sequential Card {i.ToString(CultureInfo.InvariantCulture)}",
                descriptionMarkdown = "",
                size = "S",
                laneId,
                position = Random.Shared.Next(10000, 99999)
            });
            response.EnsureSuccessStatusCode();
            var card = await response.Content.ReadFromJsonAsync<JsonElement>();
            numbers.Add(card.GetProperty("number").GetInt64());
        }

        // Assert
        numbers[1].ShouldBe(numbers[0] + 1);
        numbers[2].ShouldBe(numbers[1] + 1);
    }

    [Fact]
    public async Task PostCard_AsAgentUser_Returns201()
    {
        // Arrange
        var agent = await TestAuthHelper.CreateUserAsync(_client, _factory, "AgentCardCreator", UserRole.AgentUser);
        TestAuthHelper.SetAuth(_client, agent.AuthKey);
        var laneId = await GetFirstLaneIdAsync();

        var request = new
        {
            name = "Agent Created Card",
            descriptionMarkdown = "Created by agent",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task PatchCard_UpdatesName_Returns200WithUpdatedTimestamp()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Original Name",
            descriptionMarkdown = "desc",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();
        var originalTimestamp = created.GetProperty("lastUpdatedAtUtc").GetDateTimeOffset();

        await Task.Delay(50);

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { name = "Updated Name" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("name").GetString().ShouldBe("Updated Name");
        updated.GetProperty("lastUpdatedAtUtc").GetDateTimeOffset().ShouldBeGreaterThan(originalTimestamp);
    }

    [Fact]
    public async Task PatchCard_WithTargetLaneId_MovesToAnotherLane()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var sourceLaneId = await GetLaneIdByIndexAsync(0);
        var targetLaneId = await GetLaneIdByIndexAsync(1);
        var pos = Random.Shared.Next(10000, 99999);

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Movable Card",
            descriptionMarkdown = "will move",
            size = "M",
            laneId = sourceLaneId,
            position = pos
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act — move to target lane, keeping same position (unique in the new lane)
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { laneId = targetLaneId });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("laneId").GetGuid().ShouldBe(targetLaneId);
    }

    [Fact]
    public async Task PatchCard_NonexistentCard_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var bogusId = Guid.NewGuid();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{bogusId}", new { name = "Ghost" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchCard_PartialUpdate_PreservesUnchangedFields()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var pos = Random.Shared.Next(10000, 99999);

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Stable Card",
            descriptionMarkdown = "Original description",
            size = "M",
            laneId,
            position = pos
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();
        var originalDescription = created.GetProperty("descriptionMarkdown").GetString();

        // Act -- only update name
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { name = "Renamed Card" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("name").GetString().ShouldBe("Renamed Card");
        updated.GetProperty("descriptionMarkdown").GetString().ShouldBe(originalDescription);
        updated.GetProperty("sizeId").GetGuid().ShouldNotBe(Guid.Empty);
        updated.GetProperty("laneId").GetGuid().ShouldBe(laneId);
        updated.GetProperty("position").GetInt32().ShouldBe(pos);
    }

    [Fact]
    public async Task PatchCard_ExistingCard_ReturnsEnrichedCardSummary()
    {
        // Arrange — parity with POST /cards: PATCH returns sizeName, labels, commentCount, attachmentCount, isArchived
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var pos = Random.Shared.Next(10000, 99999);

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Enriched Patch Subject",
            descriptionMarkdown = "starts here",
            laneId,
            position = pos
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();
        var createdSizeName = created.GetProperty("sizeName").GetString();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { name = "Enriched Patch Result" });

        // Assert — every enriched-shape field present (matches POST /cards/{boardId}/cards)
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("name").GetString().ShouldBe("Enriched Patch Result");
        updated.GetProperty("sizeName").GetString().ShouldBe(createdSizeName);
        updated.GetProperty("commentCount").GetInt32().ShouldBe(0);
        updated.GetProperty("attachmentCount").GetInt32().ShouldBe(0);
        updated.GetProperty("isArchived").GetBoolean().ShouldBeFalse();
        updated.GetProperty("labels").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task DeleteCard_AsAdmin_Returns204()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Admin Delete Target",
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/cards/{cardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteCard_AsHumanUser_Returns204()
    {
        // Arrange
        var human = await TestAuthHelper.CreateUserAsync(_client, _factory, "HumanDeleter", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, human.AuthKey);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Human Delete Target",
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/cards/{cardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteCard_AsAgentUser_Returns403()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Agent Cannot Delete This",
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        var agent = await TestAuthHelper.CreateUserAsync(_client, _factory, "AgentNoDelete", UserRole.AgentUser);
        TestAuthHelper.SetAuth(_client, agent.AuthKey);

        // Act
        var response = await _client.DeleteAsync($"/api/v1/cards/{cardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteCard_NonexistentCard_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var bogusId = Guid.NewGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/v1/cards/{bogusId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostCard_InvalidSizeId_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var request = new
        {
            name = "Invalid Size Card",
            descriptionMarkdown = "",
            sizeId = Guid.NewGuid(),
            laneId,
            position = Random.Shared.Next(10000, 99999)
        };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("S")]
    [InlineData("M")]
    [InlineData("L")]
    [InlineData("XL")]
    public async Task PostCard_ValidSizes_AllAccepted(string size)
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var sizeId = await GetSizeIdByNameAsync(size);

        var request = new
        {
            name = $"Card Size {size}",
            descriptionMarkdown = "",
            sizeId,
            laneId,
            position = Random.Shared.Next(10000, 99999)
        };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        card.GetProperty("sizeId").GetGuid().ShouldBe(sizeId);
    }

    [Fact]
    public async Task PatchCard_SetPositionToZero_Works()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Position Zero Card",
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { position = 0 });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("position").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task PatchCard_InvalidSizeId_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card For Invalid Size Patch",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { sizeId = Guid.NewGuid() });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReorderCard_SameLane_MovesToNewIndex()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        List<Guid> cardIds = [];
        for (var i = 0; i < 3; i++)
        {
            var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
            {
                name = $"Reorder Same Lane Card {i.ToString(CultureInfo.InvariantCulture)}",
                descriptionMarkdown = "",
                size = "M",
                laneId,
                position = i * 10
            });
            createResponse.EnsureSuccessStatusCode();
            var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
            cardIds.Add(created.GetProperty("id").GetGuid());
        }

        // Act — move last card (index 2) to index 0
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardIds[2]}/reorder", new
        {
            laneId,
            index = 0
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var board = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cards = board.GetProperty("cards");
        List<JsonElement> reorderedCards = [];
        foreach (var c in cards.EnumerateArray())
        {
            if (c.GetProperty("laneId").GetGuid() == laneId && cardIds.Contains(c.GetProperty("id").GetGuid()))
            {
                reorderedCards.Add(c);
            }
        }

        reorderedCards.Count.ShouldBe(3);
        reorderedCards[0].GetProperty("id").GetGuid().ShouldBe(cardIds[2]);
        reorderedCards[0].GetProperty("position").GetInt32().ShouldBeLessThan(reorderedCards[1].GetProperty("position").GetInt32());
    }

    [Fact]
    public async Task ReorderCard_CrossLane_MovesToTargetLane()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var sourceLaneId = await GetLaneIdByIndexAsync(0);
        var targetLaneId = await GetLaneIdByIndexAsync(1);

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Cross Lane Reorder Card",
            descriptionMarkdown = "",
            size = "M",
            laneId = sourceLaneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act — move to target lane at index 0
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/reorder", new
        {
            laneId = targetLaneId,
            index = 0
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var board = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cards = board.GetProperty("cards");
        JsonElement? movedCard = null;
        foreach (var c in cards.EnumerateArray())
        {
            if (c.GetProperty("id").GetGuid() == cardId)
            {
                movedCard = c;
                break;
            }
        }

        movedCard.ShouldNotBeNull();
        movedCard.Value.GetProperty("laneId").GetGuid().ShouldBe(targetLaneId);
    }

    [Fact]
    public async Task ReorderCard_ToEmptyLane_Works()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var sourceLaneId = await GetFirstLaneIdAsync();

        // Create a new empty lane
        var laneResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/lanes", new
        {
            name = $"Empty Lane {Guid.NewGuid()}",
            position = NextLanePosition()
        });
        laneResponse.EnsureSuccessStatusCode();
        var lane = await laneResponse.Content.ReadFromJsonAsync<JsonElement>();
        var emptyLaneId = lane.GetProperty("id").GetGuid();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card To Empty Lane",
            descriptionMarkdown = "",
            size = "M",
            laneId = sourceLaneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act — move to empty lane at index 0
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/reorder", new
        {
            laneId = emptyLaneId,
            index = 0
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var board = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cards = board.GetProperty("cards");
        JsonElement? movedCard = null;
        foreach (var c in cards.EnumerateArray())
        {
            if (c.GetProperty("id").GetGuid() == cardId)
            {
                movedCard = c;
                break;
            }
        }

        movedCard.ShouldNotBeNull();
        movedCard.Value.GetProperty("laneId").GetGuid().ShouldBe(emptyLaneId);
        movedCard.Value.GetProperty("position").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task ReorderCard_NonexistentCard_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var bogusId = Guid.NewGuid();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{bogusId}/reorder", new
        {
            laneId,
            index = 0
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReorderCard_NonexistentLane_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card For Bad Lane Reorder",
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/reorder", new
        {
            laneId = Guid.NewGuid(),
            index = 0
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReorderCard_ExistingCard_ReturnsFullBoard()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Full Board Reorder Card",
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/reorder", new
        {
            laneId,
            index = 0
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var board = await response.Content.ReadFromJsonAsync<JsonElement>();
        board.TryGetProperty("lanes", out var lanes).ShouldBeTrue();
        board.TryGetProperty("cards", out var cards).ShouldBeTrue();
        lanes.GetArrayLength().ShouldBeGreaterThan(0);
        cards.GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ReorderCard_CrossBoard_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createCardResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Cross Board Reorder Card",
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createCardResponse.EnsureSuccessStatusCode();
        var card = await createCardResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = card.GetProperty("id").GetGuid();

        // Create a second board with a lane
        var boardResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = $"OtherBoard-{Guid.NewGuid()}" });
        boardResponse.EnsureSuccessStatusCode();
        var otherBoard = await boardResponse.Content.ReadFromJsonAsync<JsonElement>();
        var otherBoardId = otherBoard.GetProperty("id").GetGuid();

        var otherLaneResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{otherBoardId}/lanes", new { name = "Other Lane", position = 0 });
        otherLaneResponse.EnsureSuccessStatusCode();
        var otherLane = await otherLaneResponse.Content.ReadFromJsonAsync<JsonElement>();
        var otherLaneId = otherLane.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/reorder", new
        {
            laneId = otherLaneId,
            index = 0
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostCard_NewlinesInDescription_PreservedOnRoundTrip()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var description = "Line one\nLine two\nLine three";

        var request = new
        {
            name = "Newline Card",
            descriptionMarkdown = description,
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        card.GetProperty("descriptionMarkdown").GetString().ShouldBe(description);
    }

    [Fact]
    public async Task PatchCard_NewlinesInDescription_PreservedOnRoundTrip()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card For Newline Patch",
            descriptionMarkdown = "initial",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        var newDescription = "Updated line one\nUpdated line two\ttab here";

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { descriptionMarkdown = newDescription });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("descriptionMarkdown").GetString().ShouldBe(newDescription);
    }

    [Fact]
    public async Task GetCards_LabeledCard_IncludesLabelsAndCounts()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        // Create a label
        var labelResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels",
            new { name = $"EnrichLabel-{Guid.NewGuid()}", color = "green" });
        labelResponse.EnsureSuccessStatusCode();
        var label = await labelResponse.Content.ReadFromJsonAsync<JsonElement>();
        var labelId = label.GetProperty("id").GetGuid();

        // Create a card
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Enriched Card",
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Assign label to card
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId });

        // Add a comment
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Test comment" });

        // Add an attachment
        var attachContent = new MultipartFormDataContent
        {
            { new ByteArrayContent([1, 2, 3]), "file", "test.txt" },
        };
        await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", attachContent);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        var cards = paged!.Items;
        cards.ShouldNotBeNull();

        var enrichedCard = cards.First(c => c.GetProperty("id").GetGuid() == cardId);
        enrichedCard.GetProperty("labels").GetArrayLength().ShouldBe(1);
        enrichedCard.GetProperty("labels")[0].GetProperty("id").GetGuid().ShouldBe(labelId);
        enrichedCard.GetProperty("labels")[0].GetProperty("name").GetString()!.ShouldContain("EnrichLabel");
        enrichedCard.GetProperty("labels")[0].GetProperty("color").GetString().ShouldBe("green");
        enrichedCard.GetProperty("commentCount").GetInt32().ShouldBe(1);
        enrichedCard.GetProperty("attachmentCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task GetCards_CardWithNoLabelsOrCounts_ReturnsEmptyAndZero()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Bare Card No Extras",
            descriptionMarkdown = "",
            size = "S",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        var cards = paged!.Items;
        cards.ShouldNotBeNull();

        var bareCard = cards.First(c => c.GetProperty("id").GetGuid() == cardId);
        bareCard.GetProperty("labels").GetArrayLength().ShouldBe(0);
        bareCard.GetProperty("commentCount").GetInt32().ShouldBe(0);
        bareCard.GetProperty("attachmentCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task PatchCard_WithLabelIds_ReplacesLabels()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        // Create two labels (board-scoped)
        var label1Response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels", new { name = $"CardLabel1-{Guid.NewGuid()}", color = "red" });
        label1Response.EnsureSuccessStatusCode();
        var label1 = await label1Response.Content.ReadFromJsonAsync<JsonElement>();
        var label1Id = label1.GetProperty("id").GetGuid();

        var label2Response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels", new { name = $"CardLabel2-{Guid.NewGuid()}", color = "blue" });
        label2Response.EnsureSuccessStatusCode();
        var label2 = await label2Response.Content.ReadFromJsonAsync<JsonElement>();
        var label2Id = label2.GetProperty("id").GetGuid();

        // Create a card
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Labeled Card",
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Assign label1 via patch
        var patchResponse1 = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { labelIds = new[] { label1Id } });
        patchResponse1.EnsureSuccessStatusCode();

        // Act — replace with label2 only
        var patchResponse2 = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new { labelIds = new[] { label2Id } });

        // Assert
        patchResponse2.StatusCode.ShouldBe(HttpStatusCode.OK);

        var labelsResponse = await _client.GetAsync($"/api/v1/cards/{cardId}/labels");
        labelsResponse.EnsureSuccessStatusCode();
        var labels = await labelsResponse.Content.ReadFromJsonAsync<JsonElement>();
        labels.GetArrayLength().ShouldBe(1);
        labels[0].GetProperty("id").GetGuid().ShouldBe(label2Id);
    }

    [Fact]
    public async Task GetCard_WithAttachment_ReturnsAttachmentsWithoutPayload()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card With Attachment",
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        using var attachContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        attachContent.Add(fileContent, "file", "image.png");
        var attachResponse = await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", attachContent);
        attachResponse.EnsureSuccessStatusCode();

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var attachments = body.GetProperty("attachments");
        attachments.GetArrayLength().ShouldBe(1);

        var attachment = attachments[0];
        attachment.GetProperty("fileName").GetString().ShouldBe("image.png");
        attachment.GetProperty("contentType").GetString().ShouldBe("image/png");
        attachment.GetProperty("addedByUserId").GetGuid().ShouldNotBe(Guid.Empty);
        attachment.TryGetProperty("payload", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task GetCard_CreatedByHumanUser_ReturnsUserDisplayNames()
    {
        // Arrange
        var human = await TestAuthHelper.CreateUserAsync(_client, _factory, "DisplayNameUser", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, human.AuthKey);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card With User Names",
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Add a comment
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Hello" });

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("createdByUserName").GetString().ShouldBe("DisplayNameUser");
        body.GetProperty("lastUpdatedByUserName").GetString().ShouldBe("DisplayNameUser");

        // v1 legacy plain array (no commentsLimit param on v1)
        var comments = body.GetProperty("comments");
        comments.ValueKind.ShouldBe(JsonValueKind.Array);
        comments.GetArrayLength().ShouldBe(1);
        comments[0].GetProperty("userName").GetString().ShouldBe("DisplayNameUser");
    }

    [Fact]
    public async Task McpGetCard_ByCardNumber_ReturnsCard()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card Number Lookup",
            descriptionMarkdown = "Find by number",
            size = "S",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardNumber = created.GetProperty("number").GetInt64();

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<Mcp.McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<Events.BoardEventBroadcaster>();
        var cardTools = new Mcp.CardTools(db, authService, broadcaster);

        var result = await cardTools.GetCardAsync(_factory.AdminAuthKey, cardNumber: cardNumber, boardId: _factory.DefaultBoardId);

        // Assert
        var parsed = JsonSerializer.Deserialize<JsonElement>(result);
        parsed.GetProperty("card").GetProperty("name").GetString().ShouldBe("Card Number Lookup");
        parsed.GetProperty("card").GetProperty("number").GetInt64().ShouldBe(cardNumber);
    }

    [Fact]
    public async Task McpGetCard_NeitherIdNorNumber_ReturnsError()
    {
        // Arrange
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<Mcp.McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<Events.BoardEventBroadcaster>();
        var cardTools = new Mcp.CardTools(db, authService, broadcaster);

        // Act
        var result = await cardTools.GetCardAsync(_factory.AdminAuthKey);

        // Assert
        result.ShouldContain("Provide either cardId or cardNumber");
    }

    [Fact]
    public async Task McpGetCard_InvalidCardNumber_ReturnsError()
    {
        // Arrange
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<Mcp.McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<Events.BoardEventBroadcaster>();
        var cardTools = new Mcp.CardTools(db, authService, broadcaster);

        // Act
        var result = await cardTools.GetCardAsync(_factory.AdminAuthKey, cardNumber: 999999, boardId: _factory.DefaultBoardId);

        // Assert
        result.ShouldContain("not found");
    }

    [Fact]
    public async Task McpGetCard_EnrichedCard_IncludesAttachmentsAndUserNames()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "MCP Enriched Card",
            descriptionMarkdown = "",
            size = "M",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Add a comment
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "MCP test comment" });

        // Add an attachment via REST
        using var attachContent = new MultipartFormDataContent();
        attachContent.Add(new ByteArrayContent([1, 2, 3]), "file", "data.csv");
        await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", attachContent);

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<Mcp.McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<Events.BoardEventBroadcaster>();
        var cardTools = new Mcp.CardTools(db, authService, broadcaster);

        var result = await cardTools.GetCardAsync(_factory.AdminAuthKey, cardId: cardId);

        // Assert
        var parsed = JsonSerializer.Deserialize<JsonElement>(result);
        parsed.GetProperty("card").GetProperty("name").GetString().ShouldBe("MCP Enriched Card");

        // User names
        parsed.GetProperty("createdByUserName").GetString().ShouldNotBeNullOrEmpty();
        parsed.GetProperty("lastUpdatedByUserName").GetString().ShouldNotBeNullOrEmpty();

        // Comments with user names — no commentsLimit was passed, so this is the legacy plain array
        var comments = parsed.GetProperty("comments");
        comments.ValueKind.ShouldBe(JsonValueKind.Array);
        comments.GetArrayLength().ShouldBe(1);
        comments[0].GetProperty("userName").GetString().ShouldNotBeNullOrEmpty();

        // Attachments (metadata only)
        var attachments = parsed.GetProperty("attachments");
        attachments.GetArrayLength().ShouldBe(1);
        attachments[0].GetProperty("fileName").GetString().ShouldBe("data.csv");
    }

    // ── Field projection + comment paging (get_card, REST + MCP) ──────────────

    [Fact]
    public async Task GetCard_IncludeDescriptionFalse_OmitsBodyButKeepsMetadataAndHistoryCount()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Projection Card",
            descriptionMarkdown = "A heavy description body",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}?includeDescription=false");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The description body is dropped; the rest of the card metadata is intact
        body.GetProperty("card").GetProperty("descriptionMarkdown").GetString().ShouldBeEmpty();
        body.GetProperty("card").GetProperty("name").GetString().ShouldBe("Projection Card");

        // The history-count teaser stays in every projection
        body.TryGetProperty("descriptionHistoryCount", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task GetCardV2_CommentsLimitZero_ReturnsCountWithoutBodies()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "CountOnly Card",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "one" });
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "two" });

        // Act
        var response = await _client.GetAsync($"/api/v2/cards/{cardId}?commentsLimit=0");

        // Assert — the true total, no bodies loaded
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var comments = body.GetProperty("comments");
        comments.GetProperty("items").GetArrayLength().ShouldBe(0);
        comments.GetProperty("totalCount").GetInt32().ShouldBe(2);
        comments.GetProperty("limit").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task GetCardV2_CommentsPaged_NewestFirstAcrossOffsets()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Paged Comments Card",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        (await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "First" })).EnsureSuccessStatusCode();
        await Task.Delay(50);
        (await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Second" })).EnsureSuccessStatusCode();
        await Task.Delay(50);
        (await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Third" })).EnsureSuccessStatusCode();

        // Act — first page of two, newest first
        var firstPage = await _client.GetAsync($"/api/v2/cards/{cardId}?commentsLimit=2");
        firstPage.EnsureSuccessStatusCode();
        var firstComments = (await firstPage.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("comments");

        // Assert — the two newest; totalCount is the whole thread regardless of the page
        firstComments.GetProperty("totalCount").GetInt32().ShouldBe(3);
        firstComments.GetProperty("limit").GetInt32().ShouldBe(2);
        var firstItems = firstComments.GetProperty("items");
        firstItems.GetArrayLength().ShouldBe(2);
        firstItems[0].GetProperty("contentMarkdown").GetString().ShouldBe("Third");
        firstItems[1].GetProperty("contentMarkdown").GetString().ShouldBe("Second");
        firstItems[0].GetProperty("createdAtUtc").GetString().ShouldNotBeNullOrEmpty();

        // Act — the next page picks up where the first left off
        var secondPage = await _client.GetAsync($"/api/v2/cards/{cardId}?commentsLimit=2&commentsOffset=2");
        secondPage.EnsureSuccessStatusCode();
        var secondItems = (await secondPage.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("comments").GetProperty("items");

        // Assert — the oldest comment, alone on the last page
        secondItems.GetArrayLength().ShouldBe(1);
        secondItems[0].GetProperty("contentMarkdown").GetString().ShouldBe("First");
    }

    [Fact]
    public async Task McpGetCard_IncludeDescriptionFalse_OmitsBody()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "MCP Projection Card",
            descriptionMarkdown = "A heavy description body",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<Mcp.McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<Events.BoardEventBroadcaster>();
        var cardTools = new Mcp.CardTools(db, authService, broadcaster);

        var result = await cardTools.GetCardAsync(_factory.AdminAuthKey, cardId: cardId, includeDescription: false);

        // Assert
        var parsed = JsonSerializer.Deserialize<JsonElement>(result);
        parsed.GetProperty("card").GetProperty("descriptionMarkdown").GetString().ShouldBeEmpty();
        parsed.GetProperty("card").GetProperty("name").GetString().ShouldBe("MCP Projection Card");
        parsed.TryGetProperty("descriptionHistoryCount", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task McpGetCard_CommentsLimitZero_ReturnsCountOnly()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "MCP CountOnly Card",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "one" });
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "two" });

        // Act
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<Mcp.McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<Events.BoardEventBroadcaster>();
        var cardTools = new Mcp.CardTools(db, authService, broadcaster);

        var result = await cardTools.GetCardAsync(_factory.AdminAuthKey, cardId: cardId, commentsLimit: 0);

        // Assert
        var comments = JsonSerializer.Deserialize<JsonElement>(result).GetProperty("comments");
        comments.GetProperty("items").GetArrayLength().ShouldBe(0);
        comments.GetProperty("totalCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task GetCard_V2RestReturnsAllComments_WhileMcpWithLimitCaps()
    {
        // Arrange — a heavy thread of 21 comments on one card, seeded with ascending
        // timestamps so the newest is deterministic on both surfaces
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Heavy Thread Card",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        const int total = 21;
        await using (var seedScope = _factory.Services.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<BoardDbContext>();
            var authorId = (await seedDb.Cards.FindAsync(cardId))!.CreatedByUserId;
            var baseTime = DateTimeOffset.UtcNow;

            for (var i = 0; i < total; i++)
            {
                seedDb.Comments.Add(new CardComment
                {
                    Id = Guid.NewGuid(),
                    CardId = cardId,
                    UserId = authorId,
                    ContentMarkdown = $"Comment {i.ToString(CultureInfo.InvariantCulture)}",
                    CreatedAtUtc = baseTime.AddSeconds(i),
                    LastUpdatedAtUtc = baseTime.AddSeconds(i),
                });
            }

            await seedDb.SaveChangesAsync();
        }

        // Act — v2 REST omits the limit (whole thread); MCP passes commentsLimit to cap the page
        var restResponse = await _client.GetAsync($"/api/v2/cards/{cardId}");
        restResponse.EnsureSuccessStatusCode();
        var restComments = (await restResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("comments");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<Mcp.McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<Events.BoardEventBroadcaster>();
        var cardTools = new Mcp.CardTools(db, authService, broadcaster);
        var mcpResult = await cardTools.GetCardAsync(_factory.AdminAuthKey, cardId: cardId, commentsLimit: 20);
        var mcpComments = JsonSerializer.Deserialize<JsonElement>(mcpResult).GetProperty("comments");

        // Assert — v2 REST returns the whole thread, MCP caps at commentsLimit=20, both report the true total
        restComments.GetProperty("items").GetArrayLength().ShouldBe(total);
        restComments.GetProperty("totalCount").GetInt32().ShouldBe(total);

        mcpComments.GetProperty("items").GetArrayLength().ShouldBe(20);
        mcpComments.GetProperty("totalCount").GetInt32().ShouldBe(total);

        // Both surfaces lead with the same newest comment and carry its stamped creation time
        var newest = $"Comment {(total - 1).ToString(CultureInfo.InvariantCulture)}";
        restComments.GetProperty("items")[0].GetProperty("contentMarkdown").GetString().ShouldBe(newest);
        mcpComments.GetProperty("items")[0].GetProperty("contentMarkdown").GetString().ShouldBe(newest);
        mcpComments.GetProperty("items")[0].GetProperty("createdAtUtc").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetCardV2_CommentsLimitExceedsMax_ClampedTo200()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Comment Clamp Card",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act — an over-max commentsLimit clamps to the REST bound; the echoed limit is the
        // assertable, so no 200-comment thread needs seeding
        var response = await _client.GetAsync($"/api/v2/cards/{cardId}?commentsLimit=999");

        // Assert
        response.EnsureSuccessStatusCode();
        var comments = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("comments");
        comments.GetProperty("limit").GetInt32().ShouldBe(200);
    }

    [Fact]
    public async Task McpGetCard_CommentsLimitExceedsMax_ClampedTo500()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "MCP Comment Clamp Card",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act — an over-max commentsLimit clamps to the MCP bound; the echoed limit is the assertable
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<Mcp.McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<Events.BoardEventBroadcaster>();
        var cardTools = new Mcp.CardTools(db, authService, broadcaster);

        var result = await cardTools.GetCardAsync(_factory.AdminAuthKey, cardId: cardId, commentsLimit: 999);

        // Assert
        var comments = JsonSerializer.Deserialize<JsonElement>(result).GetProperty("comments");
        comments.GetProperty("limit").GetInt32().ShouldBe(500);
    }

    // ── Comments compat pivot: v1 legacy array + deprecation, v2 paged, MCP dual-path ────────

    [Fact]
    public async Task GetCard_V1_CarriesDeprecationHeaders()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Deprecation Header Card",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}");

        // Assert — RFC 9745 Deprecation (a structured-field date, @<epoch>) plus a Link to the paged v2
        // successor (RFC 8288 / RFC 5829 successor-version relation). No Sunset yet — no removal date.
        response.EnsureSuccessStatusCode();
        response.Headers.Contains("Deprecation").ShouldBeTrue();
        response.Headers.GetValues("Deprecation").Single().ShouldStartWith("@");
        response.Headers.Contains("Sunset").ShouldBeFalse();

        response.Headers.Contains("Link").ShouldBeTrue();
        var link = response.Headers.GetValues("Link").Single();
        link.ShouldContain($"</api/v2/cards/{cardId}>");
        link.ShouldContain("rel=\"successor-version\"");
    }

    [Fact]
    public async Task GetCardV1_NotFound_StillCarriesDeprecationHeaders()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var bogusId = Guid.NewGuid();

        // Act — deprecation is a property of the route, so even a 404 advertises the successor
        var response = await _client.GetAsync($"/api/v1/cards/{bogusId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.Contains("Deprecation").ShouldBeTrue();
        response.Headers.Contains("Link").ShouldBeTrue();
    }

    [Fact]
    public async Task GetCard_V1_LegacyArrayCarriesAdditiveFields()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Legacy Additive Card",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "a comment" });

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Assert — the legacy plain array is a strict SUPERSET of v2.0.2: the array shape is restored,
        // and the additive-only fields a v2.0.2 client ignores are present (per-comment createdAtUtc,
        // top-level descriptionHistoryCount).
        var comments = body.GetProperty("comments");
        comments.ValueKind.ShouldBe(JsonValueKind.Array);
        comments[0].TryGetProperty("createdAtUtc", out _).ShouldBeTrue();
        body.TryGetProperty("descriptionHistoryCount", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task GetCard_V2_DoesNotCarryDeprecationHeaders()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "V2 Successor Card",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.GetAsync($"/api/v2/cards/{cardId}");

        // Assert — v2 is the successor; it is not deprecated and carries the paged envelope
        response.EnsureSuccessStatusCode();
        response.Headers.Contains("Deprecation").ShouldBeFalse();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("comments").TryGetProperty("items", out _).ShouldBeTrue();
        body.GetProperty("comments").TryGetProperty("totalCount", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task GetCardV2_IncludeDescriptionFalse_OmitsBodyKeepsEnvelope()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "V2 Projection Card",
            descriptionMarkdown = "A heavy description body",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.GetAsync($"/api/v2/cards/{cardId}?includeDescription=false");

        // Assert — the projection lever works on v2, and comments stay a paged envelope
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("card").GetProperty("descriptionMarkdown").GetString().ShouldBeEmpty();
        body.GetProperty("comments").TryGetProperty("items", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task McpGetCard_NoCommentsLimit_ReturnsLegacyPlainArray()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "MCP Legacy Array Card",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        (await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "one" })).EnsureSuccessStatusCode();
        await Task.Delay(50);
        (await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "two" })).EnsureSuccessStatusCode();

        // Act — no commentsLimit → the DEPRECATED legacy path
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<Mcp.McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<Events.BoardEventBroadcaster>();
        var cardTools = new Mcp.CardTools(db, authService, broadcaster);

        var result = await cardTools.GetCardAsync(_factory.AdminAuthKey, cardId: cardId);

        // Assert — the whole thread as a plain array, oldest first, with the additive fields present
        var parsed = JsonSerializer.Deserialize<JsonElement>(result);
        var comments = parsed.GetProperty("comments");
        comments.ValueKind.ShouldBe(JsonValueKind.Array);
        comments.GetArrayLength().ShouldBe(2);
        comments[0].GetProperty("contentMarkdown").GetString().ShouldBe("one");
        comments[0].TryGetProperty("createdAtUtc", out _).ShouldBeTrue();
        parsed.TryGetProperty("descriptionHistoryCount", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task McpGetCard_CommentsOffsetWithoutLimit_ReturnsError()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "MCP Offset Guard Card",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act — commentsOffset without commentsLimit fails loud rather than silently ignoring the offset
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<Mcp.McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<Events.BoardEventBroadcaster>();
        var cardTools = new Mcp.CardTools(db, authService, broadcaster);

        var result = await cardTools.GetCardAsync(_factory.AdminAuthKey, cardId: cardId, commentsOffset: 5);

        // Assert
        result.ShouldContain("commentsOffset requires commentsLimit");
    }

    [Fact]
    public async Task McpGetCard_WithCommentsLimit_ReturnsPagedEnvelopeNewestFirst()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "MCP Paged Card",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        (await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "First" })).EnsureSuccessStatusCode();
        await Task.Delay(50);
        (await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Second" })).EnsureSuccessStatusCode();
        await Task.Delay(50);
        (await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Third" })).EnsureSuccessStatusCode();

        // Act — commentsLimit present → the paged envelope, newest activity first
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<Mcp.McpAuthService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<Events.BoardEventBroadcaster>();
        var cardTools = new Mcp.CardTools(db, authService, broadcaster);

        var result = await cardTools.GetCardAsync(_factory.AdminAuthKey, cardId: cardId, commentsLimit: 2);

        // Assert
        var comments = JsonSerializer.Deserialize<JsonElement>(result).GetProperty("comments");
        comments.GetProperty("totalCount").GetInt32().ShouldBe(3);
        comments.GetProperty("limit").GetInt32().ShouldBe(2);
        var items = comments.GetProperty("items");
        items.GetArrayLength().ShouldBe(2);
        items[0].GetProperty("contentMarkdown").GetString().ShouldBe("Third");
    }

    // ── Hardened enrichment tests (query optimization) ───────────────────────

    [Fact]
    public async Task GetCards_CardWithSizeL_ReturnsCorrectSizeName()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var sizeId = await GetSizeIdByNameAsync("L");

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = $"SizeName-Test-{Guid.NewGuid()}",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999),
            sizeId
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards");
        response.EnsureSuccessStatusCode();
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        var cards = paged!.Items;

        // Assert
        var card = cards!.First(c => c.GetProperty("id").GetGuid() == cardId);
        card.GetProperty("sizeName").GetString().ShouldBe("L");
        card.GetProperty("sizeId").GetGuid().ShouldBe(sizeId);
    }

    [Fact]
    public async Task GetCards_LabeledCard_ReturnsCorrectLabelSummaries()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var label1Response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels",
            new { name = $"QOpt1-{Guid.NewGuid()}", color = "red" });
        label1Response.EnsureSuccessStatusCode();
        var label1 = await label1Response.Content.ReadFromJsonAsync<JsonElement>();
        var label1Id = label1.GetProperty("id").GetGuid();
        var label1Name = label1.GetProperty("name").GetString();

        var label2Response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels",
            new { name = $"QOpt2-{Guid.NewGuid()}", color = "blue" });
        label2Response.EnsureSuccessStatusCode();
        var label2 = await label2Response.Content.ReadFromJsonAsync<JsonElement>();
        var label2Id = label2.GetProperty("id").GetGuid();
        var label2Name = label2.GetProperty("name").GetString();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = $"LabelSummary-Test-{Guid.NewGuid()}",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId = label1Id });
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/labels", new { labelId = label2Id });

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards");
        response.EnsureSuccessStatusCode();
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        var cards = paged!.Items;

        // Assert
        var card = cards!.First(c => c.GetProperty("id").GetGuid() == cardId);
        var labels = card.GetProperty("labels");
        labels.GetArrayLength().ShouldBe(2);

        var labelIds = labels.EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList();
        labelIds.ShouldContain(label1Id);
        labelIds.ShouldContain(label2Id);

        var labelNames = labels.EnumerateArray().Select(l => l.GetProperty("name").GetString()).ToList();
        labelNames.ShouldContain(label1Name);
        labelNames.ShouldContain(label2Name);

        var labelColors = labels.EnumerateArray().Select(l => l.GetProperty("color").GetString()).ToList();
        labelColors.ShouldContain("red");
        labelColors.ShouldContain("blue");
    }

    [Fact]
    public async Task GetCards_CardWithComments_ReturnsCorrectCommentCount()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = $"CommentCount-Test-{Guid.NewGuid()}",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Comment 1" });
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Comment 2" });

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards");
        response.EnsureSuccessStatusCode();
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        var cards = paged!.Items;

        // Assert
        var card = cards!.First(c => c.GetProperty("id").GetGuid() == cardId);
        card.GetProperty("commentCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task GetCards_CardWithAttachments_ReturnsCorrectAttachmentCount()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = $"AttachCount-Test-{Guid.NewGuid()}",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        using var attachContent = new MultipartFormDataContent();
        attachContent.Add(new ByteArrayContent([1, 2, 3]), "file", "file1.txt");
        await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", attachContent);

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards");
        response.EnsureSuccessStatusCode();
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        var cards = paged!.Items;

        // Assert
        var card = cards!.First(c => c.GetProperty("id").GetGuid() == cardId);
        card.GetProperty("attachmentCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task GetCard_V1_CommentsAreOldestFirstPlainArray()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = $"CommentOrder-Test-{Guid.NewGuid()}",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Add 3 comments with slight delays so timestamps are distinct
        var c1 = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "First comment" });
        c1.EnsureSuccessStatusCode();
        await Task.Delay(50);

        var c2 = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Second comment" });
        c2.EnsureSuccessStatusCode();
        await Task.Delay(50);

        var c3 = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Third comment" });
        c3.EnsureSuccessStatusCode();

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Assert — v1 is the restored legacy shape: a plain array, oldest activity first, exactly as
        // v2.0.2 (production) served it. The newest-first paged order is the v2 surface's — see the
        // GetCardV2_ paging tests.
        var comments = body.GetProperty("comments");
        comments.ValueKind.ShouldBe(JsonValueKind.Array);
        comments.GetArrayLength().ShouldBe(3);

        var timestamps = comments.EnumerateArray()
            .Select(c => DateTimeOffset.Parse(c.GetProperty("lastUpdatedAtUtc").GetString()!, CultureInfo.InvariantCulture))
                .ToList();

        // Oldest activity first — timestamps ascending down the array
        for (var i = 1; i < timestamps.Count; i++)
        {
            timestamps[i].ShouldBeGreaterThanOrEqualTo(timestamps[i - 1]);
        }

        // Content order matches creation order (oldest first)
        comments[0].GetProperty("contentMarkdown").GetString().ShouldBe("First comment");
        comments[1].GetProperty("contentMarkdown").GetString().ShouldBe("Second comment");
        comments[2].GetProperty("contentMarkdown").GetString().ShouldBe("Third comment");
    }

    [Fact]
    public async Task GetComments_SeveralComments_ReturnsOrderedByDate()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = $"CommentEndpointOrder-{Guid.NewGuid()}",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Alpha" });
        await Task.Delay(50);
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Beta" });
        await Task.Delay(50);
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Gamma" });

        // Act
        var response = await _client.GetAsync($"/api/v1/cards/{cardId}/comments");
        response.EnsureSuccessStatusCode();
        var comments = await response.Content.ReadFromJsonAsync<JsonElement[]>();

        // Assert
        comments!.Length.ShouldBe(3);
        comments[0].GetProperty("contentMarkdown").GetString().ShouldBe("Alpha");
        comments[1].GetProperty("contentMarkdown").GetString().ShouldBe("Beta");
        comments[2].GetProperty("contentMarkdown").GetString().ShouldBe("Gamma");

        var timestamps = comments.Select(c => DateTimeOffset.Parse(c.GetProperty("lastUpdatedAtUtc").GetString()!)).ToList();
        for (var i = 1; i < timestamps.Count; i++)
        {
            timestamps[i].ShouldBeGreaterThanOrEqualTo(timestamps[i - 1]);
        }
    }

    [Fact]
    public async Task GetCards_MultipleCardsWithDifferentEnrichment_AllCorrect()
    {
        // Arrange — create two cards: one with labels+comments, one bare
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var labelResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels",
            new { name = $"MultiCard-{Guid.NewGuid()}", color = "teal" });
        labelResponse.EnsureSuccessStatusCode();
        var labelId = (await labelResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Card A: with label and 2 comments
        var cardAResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = $"MultiA-{Guid.NewGuid()}",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        cardAResponse.EnsureSuccessStatusCode();
        var cardAId = (await cardAResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardAId}/labels", new { labelId });
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardAId}/comments", new { contentMarkdown = "C1" });
        await _client.PostAsJsonAsync($"/api/v1/cards/{cardAId}/comments", new { contentMarkdown = "C2" });

        // Card B: bare (no labels, no comments, no attachments)
        var cardBResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = $"MultiB-{Guid.NewGuid()}",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        cardBResponse.EnsureSuccessStatusCode();
        var cardBId = (await cardBResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards");
        response.EnsureSuccessStatusCode();
        var paged = await response.Content.ReadFromJsonAsync<PagedResult<JsonElement>>();
        var cards = paged!.Items;

        // Assert
        var cardA = cards!.First(c => c.GetProperty("id").GetGuid() == cardAId);
        cardA.GetProperty("labels").GetArrayLength().ShouldBe(1);
        cardA.GetProperty("commentCount").GetInt32().ShouldBe(2);
        cardA.GetProperty("attachmentCount").GetInt32().ShouldBe(0);
        cardA.GetProperty("sizeName").GetString().ShouldNotBe("?");

        var cardB = cards!.First(c => c.GetProperty("id").GetGuid() == cardBId);
        cardB.GetProperty("labels").GetArrayLength().ShouldBe(0);
        cardB.GetProperty("commentCount").GetInt32().ShouldBe(0);
        cardB.GetProperty("attachmentCount").GetInt32().ShouldBe(0);
        cardB.GetProperty("sizeName").GetString().ShouldNotBe("?");
    }

    // ── Validation tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task PostCard_WithoutLaneId_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "No Lane Card"
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostCard_WithoutName_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostCard_WithEmptyName_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "   ",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostReorder_WithoutLaneId_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Reorder No LaneId",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/reorder", new
        {
            index = 0
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostReorder_WithoutIndex_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Reorder No Index",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/cards/{cardId}/reorder", new
        {
            laneId
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchCard_WithNonexistentLaneId_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Patch Bad Lane",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new
        {
            laneId = Guid.NewGuid()
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchCard_WithEmptyName_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Patch Empty Name",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new
        {
            name = "  "
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostCard_WithoutPosition_DefaultsToBottomOfLane()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        // Create an existing card with a known position
        var existingResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Existing Card",
            laneId,
            position = 100
        });
        existingResponse.EnsureSuccessStatusCode();

        // Act — create card without specifying position
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Bottom Card",
            laneId
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        card.GetProperty("position").GetInt32().ShouldBeGreaterThan(100);
    }

    [Fact]
    public async Task PostCard_WithExplicitPosition_UsesProvidedValue()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Explicit Position Card",
            laneId,
            position = 42
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        card.GetProperty("position").GetInt32().ShouldBe(42);
    }

    [Fact]
    public async Task PatchCard_LaneChangeWithoutPosition_DefaultsToBottomOfLane()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var sourceLaneId = await GetLaneIdByIndexAsync(0);
        var targetLaneId = await GetLaneIdByIndexAsync(1);

        // Create an existing card in the target lane
        var existingResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Target Lane Card",
            laneId = targetLaneId,
            position = 50
        });
        existingResponse.EnsureSuccessStatusCode();

        // Create the card to move
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card To Move",
            laneId = sourceLaneId,
            position = 10
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = created.GetProperty("id").GetGuid();

        // Act — change lane without specifying position
        var response = await _client.PatchAsJsonAsync($"/api/v1/cards/{cardId}", new
        {
            laneId = targetLaneId
        });

        // Assert — the moved card is last in the target lane. Its stored number is not pinned: a move
        // to another lane renumbers the target lane from 0.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        card.GetProperty("laneId").GetGuid().ShouldBe(targetLaneId);

        var laneResponse = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards?laneId={targetLaneId}");
        laneResponse.EnsureSuccessStatusCode();
        var lane = await laneResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lastInLane = lane.GetProperty("items")
            .EnumerateArray()
            .OrderBy(c => c.GetProperty("position").GetInt32())
                .Last();

        lastInLane.GetProperty("id").GetGuid().ShouldBe(cardId);
    }

    // ── Create card with labels (atomic label attachment) ────────────────

    [Fact]
    public async Task PostCard_WithLabelIds_ReturnsCardWithLabels()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var label1Response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels",
            new { name = $"CreateLabel1-{Guid.NewGuid()}", color = "red" });
        label1Response.EnsureSuccessStatusCode();
        var label1 = await label1Response.Content.ReadFromJsonAsync<JsonElement>();
        var label1Id = label1.GetProperty("id").GetGuid();

        var label2Response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels",
            new { name = $"CreateLabel2-{Guid.NewGuid()}", color = "blue" });
        label2Response.EnsureSuccessStatusCode();
        var label2 = await label2Response.Content.ReadFromJsonAsync<JsonElement>();
        var label2Id = label2.GetProperty("id").GetGuid();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card With Labels At Create",
            descriptionMarkdown = "Created with labels",
            laneId,
            position = Random.Shared.Next(10000, 99999),
            labelIds = new[] { label1Id, label2Id }
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        card.GetProperty("labels").GetArrayLength().ShouldBe(2);

        HashSet<Guid> returnedLabelIds = [];
        foreach (var label in card.GetProperty("labels").EnumerateArray())
        {
            returnedLabelIds.Add(label.GetProperty("id").GetGuid());
        }

        returnedLabelIds.ShouldContain(label1Id);
        returnedLabelIds.ShouldContain(label2Id);
    }

    [Fact]
    public async Task PostCard_WithLabelIds_LabelsPersistedInDatabase()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        var labelResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/labels",
            new { name = $"PersistLabel-{Guid.NewGuid()}", color = "green" });
        labelResponse.EnsureSuccessStatusCode();
        var label = await labelResponse.Content.ReadFromJsonAsync<JsonElement>();
        var labelId = label.GetProperty("id").GetGuid();

        // Act
        var createResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card Labels Persist",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999),
            labelIds = new[] { labelId }
        });
        createResponse.EnsureSuccessStatusCode();
        var card = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cardId = card.GetProperty("id").GetGuid();

        // Assert — verify via GET card labels endpoint
        var labelsResponse = await _client.GetAsync($"/api/v1/cards/{cardId}/labels");
        labelsResponse.EnsureSuccessStatusCode();
        var labels = await labelsResponse.Content.ReadFromJsonAsync<JsonElement>();
        labels.GetArrayLength().ShouldBe(1);
        labels[0].GetProperty("id").GetGuid().ShouldBe(labelId);
    }

    [Fact]
    public async Task PostCard_WithInvalidLabelId_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card With Bad Label",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999),
            labelIds = new[] { Guid.NewGuid() }
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostCard_WithCrossBoardLabelId_Returns400()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        // Create a second board with a label
        var boardResponse = await _client.PostAsJsonAsync("/api/v1/boards", new { name = $"CrossBoardLabel-{Guid.NewGuid()}" });
        boardResponse.EnsureSuccessStatusCode();
        var otherBoard = await boardResponse.Content.ReadFromJsonAsync<JsonElement>();
        var otherBoardId = otherBoard.GetProperty("id").GetGuid();

        var labelResponse = await _client.PostAsJsonAsync($"/api/v1/boards/{otherBoardId}/labels",
            new { name = $"OtherBoardLabel-{Guid.NewGuid()}", color = "red" });
        labelResponse.EnsureSuccessStatusCode();
        var label = await labelResponse.Content.ReadFromJsonAsync<JsonElement>();
        var labelId = label.GetProperty("id").GetGuid();

        // Act — try to create a card on the default board with a label from the other board
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card With Cross-Board Label",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999),
            labelIds = new[] { labelId }
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostCard_WithNoLabelIds_ReturnsEmptyLabels()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Card No Labels",
            descriptionMarkdown = "",
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();
        card.GetProperty("labels").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task PostCard_WithSizeId_ReturnsEnrichedSummary()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var laneId = await GetFirstLaneIdAsync();
        var sizeId = await GetSizeIdByNameAsync("L");

        // Act
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards", new
        {
            name = "Enriched Create Card",
            descriptionMarkdown = "Has enriched fields",
            sizeId,
            laneId,
            position = Random.Shared.Next(10000, 99999)
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var card = await response.Content.ReadFromJsonAsync<JsonElement>();

        card.GetProperty("sizeName").GetString().ShouldBe("L");
        card.GetProperty("labels").GetArrayLength().ShouldBe(0);
        card.GetProperty("commentCount").GetInt32().ShouldBe(0);
        card.GetProperty("attachmentCount").GetInt32().ShouldBe(0);
        card.GetProperty("isArchived").GetBoolean().ShouldBeFalse();
    }
}
