using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Collabot.Collattice.Api.Auth;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Mcp;
using Collabot.Collattice.Api.Models;
using Collabot.Collattice.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// duplicate_card must produce a normal new card: the source's name, description, labels and size,
// a fresh board-scoped number, the bottom of its lane exactly where create_card would put it, and a
// card.created event — and none of the source's comments, attachments, description history or
// position. The capturing sink is the observable for the emission.
public class McpDuplicateCardToolTests(WebhookTestFactory factory) : IClassFixture<WebhookTestFactory>
{
    private readonly WebhookTestFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task DuplicateCard_CopiesNameDescriptionLabelsSize_AndNothingElse()
    {
        var laneId = await CreateEmptyLaneAsync($"Dup Copy {Guid.NewGuid():N}");
        var labelA = await SeedLabelAsync($"dup-a-{Guid.NewGuid():N}");
        var labelB = await SeedLabelAsync($"dup-b-{Guid.NewGuid():N}");
        var (sizeId, sizeName) = await GetLargestSizeAsync();

        var sourceId = await CreateCardViaRestAsync(laneId, "Source card", "Original body", sizeId, [labelA, labelB]);

        // Give the source everything that must NOT travel: a description history, a comment, an
        // attachment — and a sibling below it, so the copy's position cannot coincide with the source's.
        (await _client.PatchAsJsonAsync($"/api/v1/cards/{sourceId}", new { descriptionMarkdown = "Edited body" })).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync($"/api/v1/cards/{sourceId}/comments", new { contentMarkdown = "Do not copy me" })).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/v1/cards/{sourceId}/attachments", CreateFileUpload())).EnsureSuccessStatusCode();
        await CreateCardViaRestAsync(laneId, "Sibling below", "", sizeId, []);

        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateTools(scope);

        var result = await tools.DuplicateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: sourceId);

        result.ShouldNotStartWith("Error");
        var copy = JsonDocument.Parse(result).RootElement;
        var copyId = copy.GetProperty("id").GetGuid();

        copyId.ShouldNotBe(sourceId);
        copy.GetProperty("name").GetString().ShouldBe("Source card");
        copy.GetProperty("descriptionMarkdown").GetString().ShouldBe("Edited body");
        copy.GetProperty("sizeId").GetGuid().ShouldBe(sizeId);
        copy.GetProperty("sizeName").GetString().ShouldBe(sizeName);
        copy.GetProperty("laneId").GetGuid().ShouldBe(laneId);
        copy.GetProperty("isArchived").GetBoolean().ShouldBeFalse();
        copy.GetProperty("commentCount").GetInt32().ShouldBe(0);
        copy.GetProperty("attachmentCount").GetInt32().ShouldBe(0);

        var copyLabelIds = copy.GetProperty("labels")
            .EnumerateArray()
                .Select(l => l.GetProperty("id").GetGuid())
                    .ToHashSet();

        copyLabelIds.SetEquals([labelA, labelB]).ShouldBeTrue();

        var detail = await GetV2DetailAsync(copyId);
        detail.GetProperty("descriptionHistoryCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task DuplicateCard_GetsNextBoardNumber_AndLandsAtBottomOfLane_LikeCreateCard()
    {
        var laneId = await CreateEmptyLaneAsync($"Dup Position {Guid.NewGuid():N}");
        var (sizeId, _) = await GetLargestSizeAsync();
        var sourceId = await CreateCardViaRestAsync(laneId, "Top of lane", "", sizeId, []);
        await CreateCardViaRestAsync(laneId, "Middle", "", sizeId, []);
        await CreateCardViaRestAsync(laneId, "Bottom", "", sizeId, []);

        var expectedNumber = await MaxCardNumberAsync() + 1;

        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateTools(scope);

        var result = await tools.DuplicateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: sourceId);

        result.ShouldNotStartWith("Error");
        var copy = JsonDocument.Parse(result).RootElement;
        copy.GetProperty("number").GetInt64().ShouldBe(expectedNumber);

        var positions = await LanePositionsAsync(laneId);
        var copyPosition = copy.GetProperty("position").GetInt32();

        positions.Count.ShouldBe(4);
        copyPosition.ShouldBe(positions.Max());
    }

    [Fact]
    public async Task DuplicateCard_EmitsCardCreated_ForTheCopy()
    {
        var laneId = await CreateEmptyLaneAsync($"Dup Emit {Guid.NewGuid():N}");
        var (sizeId, _) = await GetLargestSizeAsync();
        var sourceId = await CreateCardViaRestAsync(laneId, "Emitter", "", sizeId, []);

        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateTools(scope);
        _factory.Sink.Clear();

        var result = await tools.DuplicateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: sourceId);

        result.ShouldNotStartWith("Error");
        var copyId = JsonDocument.Parse(result).RootElement.GetProperty("id").GetGuid();

        var captured = _factory.Sink.Captured;
        captured.Select(e => e.EventType).ShouldBe(["card.created"]);

        var wire = JsonDocument.Parse(JsonSerializer.Serialize(captured[0], JsonSerializerOptions.Web)).RootElement;
        wire.GetProperty("data").GetProperty("card").GetProperty("id").GetGuid().ShouldBe(copyId);
    }

    [Fact]
    public async Task DuplicateCard_ArchivedSourceWithoutLaneId_FailsLoud_AndCreatesNothing()
    {
        var laneId = await CreateEmptyLaneAsync($"Dup Archived {Guid.NewGuid():N}");
        var (sizeId, _) = await GetLargestSizeAsync();
        var sourceId = await CreateCardViaRestAsync(laneId, "Archived source", "", sizeId, []);
        (await _client.PostAsync($"/api/v1/cards/{sourceId}/archive", null)).EnsureSuccessStatusCode();

        var cardCountBefore = await BoardCardCountAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateTools(scope);
        _factory.Sink.Clear();

        var result = await tools.DuplicateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: sourceId);

        result.ShouldStartWith("Error:");
        result.ShouldContain("archived");
        result.ShouldContain("laneId");
        (await BoardCardCountAsync()).ShouldBe(cardCountBefore);
        _factory.Sink.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task DuplicateCard_ArchivedSourceWithLaneId_CreatesCopyInThatLane()
    {
        var sourceLaneId = await CreateEmptyLaneAsync($"Dup Archived Src {Guid.NewGuid():N}");
        var targetLaneId = await CreateEmptyLaneAsync($"Dup Archived Dst {Guid.NewGuid():N}");
        var labelId = await SeedLabelAsync($"dup-arch-{Guid.NewGuid():N}");
        var (sizeId, _) = await GetLargestSizeAsync();
        var sourceId = await CreateCardViaRestAsync(sourceLaneId, "Archived with labels", "Kept body", sizeId, [labelId]);
        (await _client.PostAsync($"/api/v1/cards/{sourceId}/archive", null)).EnsureSuccessStatusCode();

        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateTools(scope);

        var result = await tools.DuplicateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: sourceId, laneId: targetLaneId);

        result.ShouldNotStartWith("Error");
        var copy = JsonDocument.Parse(result).RootElement;
        copy.GetProperty("laneId").GetGuid().ShouldBe(targetLaneId);
        copy.GetProperty("isArchived").GetBoolean().ShouldBeFalse();
        copy.GetProperty("descriptionMarkdown").GetString().ShouldBe("Kept body");
        copy.GetProperty("labels")[0].GetProperty("id").GetGuid().ShouldBe(labelId);
    }

    [Fact]
    public async Task DuplicateCard_ArchiveLaneTarget_IsRejected()
    {
        var laneId = await CreateEmptyLaneAsync($"Dup To Archive {Guid.NewGuid():N}");
        var (sizeId, _) = await GetLargestSizeAsync();
        var sourceId = await CreateCardViaRestAsync(laneId, "Aimed at archive", "", sizeId, []);
        var archiveLaneId = await ArchiveLaneIdAsync(_factory.DefaultBoardId);
        var cardCountBefore = await BoardCardCountAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateTools(scope);

        var result = await tools.DuplicateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: sourceId, laneId: archiveLaneId);

        result.ShouldBe("Error: Cards cannot be created in the archive lane.");
        (await BoardCardCountAsync()).ShouldBe(cardCountBefore);
    }

    [Fact]
    public async Task DuplicateCard_LaneOnAnotherBoard_IsRejected()
    {
        var laneId = await CreateEmptyLaneAsync($"Dup Cross {Guid.NewGuid():N}");
        var (sizeId, _) = await GetLargestSizeAsync();
        var sourceId = await CreateCardViaRestAsync(laneId, "Stays home", "", sizeId, []);
        var foreignLaneId = await SeedForeignBoardLaneAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateTools(scope);

        var result = await tools.DuplicateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: sourceId, laneId: foreignLaneId);

        result.ShouldBe("Error: Lane does not belong to this board.");
    }

    [Fact]
    public async Task DuplicateCard_Overrides_ReplaceNameSizeAndLabels()
    {
        var laneId = await CreateEmptyLaneAsync($"Dup Override {Guid.NewGuid():N}");
        var labelId = await SeedLabelAsync($"dup-ovr-{Guid.NewGuid():N}");
        var (largestSizeId, _) = await GetLargestSizeAsync();
        var (smallestSizeId, smallestSizeName) = await GetSmallestSizeAsync();
        var sourceId = await CreateCardViaRestAsync(laneId, "Before override", "Same body", largestSizeId, [labelId]);

        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateTools(scope);

        var result = await tools.DuplicateCardAsync
        (
            CollatticeApiFactory.TestAdminAuthKey,
            cardId: sourceId,
            name: "After override",
            sizeName: smallestSizeName,
            labelIds: ""
        );

        result.ShouldNotStartWith("Error");
        var copy = JsonDocument.Parse(result).RootElement;
        copy.GetProperty("name").GetString().ShouldBe("After override");
        copy.GetProperty("descriptionMarkdown").GetString().ShouldBe("Same body");
        copy.GetProperty("sizeId").GetGuid().ShouldBe(smallestSizeId);
        copy.GetProperty("labels").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task DuplicateCard_ByCardNumberAndBoardSlug_ResolvesTheSource()
    {
        var laneId = await CreateEmptyLaneAsync($"Dup By Number {Guid.NewGuid():N}");
        var (sizeId, _) = await GetLargestSizeAsync();
        var sourceId = await CreateCardViaRestAsync(laneId, "Found by number", "", sizeId, []);
        var (sourceNumber, boardSlug) = await CardNumberAndBoardSlugAsync(sourceId);

        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateTools(scope);

        var result = await tools.DuplicateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardNumber: sourceNumber, boardSlug: boardSlug);

        result.ShouldNotStartWith("Error");
        var copy = JsonDocument.Parse(result).RootElement;
        copy.GetProperty("name").GetString().ShouldBe("Found by number");
        copy.GetProperty("number").GetInt64().ShouldNotBe(sourceNumber);
    }

    [Fact]
    public async Task DuplicateCard_TempCard_IsRejected()
    {
        var laneId = await CreateEmptyLaneAsync($"Dup Temp {Guid.NewGuid():N}");
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync($"/api/v1/boards/{_factory.DefaultBoardId}/cards/temp", new { name = "Draft", laneId });
        response.EnsureSuccessStatusCode();
        var tempId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await using var scope = _factory.Services.CreateAsyncScope();
        var tools = CreateTools(scope);

        var result = await tools.DuplicateCardAsync(CollatticeApiFactory.TestAdminAuthKey, cardId: tempId);

        result.ShouldBe("Error: Temp cards (unsaved drafts) cannot be duplicated.");
    }

    private static DuplicateCardTools CreateTools(AsyncServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        return new DuplicateCardTools(db, new McpAuthService(new UserResolver(db)), broadcaster);
    }

    private async Task<Guid> CreateCardViaRestAsync(Guid laneId, string name, string description, Guid sizeId, Guid[] labelIds)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.PostAsJsonAsync
        (
            $"/api/v1/boards/{_factory.DefaultBoardId}/cards",
            new { name, descriptionMarkdown = description, laneId, sizeId, labelIds }
        );
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> GetV2DetailAsync(Guid cardId)
    {
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var response = await _client.GetAsync($"/api/v2/cards/{cardId}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static MultipartFormDataContent CreateFileUpload()
    {
        var fileContent = new ByteArrayContent([1, 2, 3, 4]);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        return new MultipartFormDataContent { { fileContent, "file", "source.bin" } };
    }

    private async Task<Guid> CreateEmptyLaneAsync(string name)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var maxPosition = await db.Lanes
            .Where(l => l.BoardId == _factory.DefaultBoardId && !l.IsArchiveLane)
                .MaxAsync(l => (int?)l.Position) ?? -1;

        var lane = new Lane { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = name, Position = maxPosition + 1 };
        db.Lanes.Add(lane);
        await db.SaveChangesAsync();

        return lane.Id;
    }

    private async Task<Guid> SeedLabelAsync(string name)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var label = new Label { Id = Guid.NewGuid(), BoardId = _factory.DefaultBoardId, Name = name, Color = "#123456" };
        db.Labels.Add(label);
        await db.SaveChangesAsync();

        return label.Id;
    }

    private async Task<Guid> SeedForeignBoardLaneAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var board = new Board { Id = Guid.NewGuid(), Name = $"Foreign {suffix}", Slug = $"foreign-{suffix}", CreatedAtUtc = DateTimeOffset.UtcNow };
        var lane = new Lane { Id = Guid.NewGuid(), BoardId = board.Id, Name = "Elsewhere", Position = 0 };
        db.Boards.Add(board);
        db.Lanes.Add(lane);
        await db.SaveChangesAsync();

        return lane.Id;
    }

    private async Task<(Guid Id, string Name)> GetLargestSizeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var size = await db.CardSizes
            .Where(s => s.BoardId == _factory.DefaultBoardId)
            .OrderByDescending(s => s.Ordinal)
                .FirstAsync();

        return (size.Id, size.Name);
    }

    private async Task<(Guid Id, string Name)> GetSmallestSizeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var size = await db.CardSizes
            .Where(s => s.BoardId == _factory.DefaultBoardId)
            .OrderBy(s => s.Ordinal)
                .FirstAsync();

        return (size.Id, size.Name);
    }

    private async Task<Guid> ArchiveLaneIdAsync(Guid boardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.Lanes
            .Where(l => l.BoardId == boardId && l.IsArchiveLane)
                .Select(l => l.Id)
                    .SingleAsync();
    }

    private async Task<long> MaxCardNumberAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.Cards
            .Where(c => c.BoardId == _factory.DefaultBoardId)
                .MaxAsync(c => c.Number);
    }

    private async Task<int> BoardCardCountAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.Cards.CountAsync(c => c.BoardId == _factory.DefaultBoardId);
    }

    private async Task<List<int>> LanePositionsAsync(Guid laneId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        return await db.Cards
            .Where(c => c.LaneId == laneId)
                .Select(c => c.Position)
                    .ToListAsync();
    }

    private async Task<(long Number, string BoardSlug)> CardNumberAndBoardSlugAsync(Guid cardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var number = await db.Cards
            .Where(c => c.Id == cardId)
                .Select(c => c.Number)
                    .SingleAsync();

        var slug = await db.Boards
            .Where(b => b.Id == _factory.DefaultBoardId)
                .Select(b => b.Slug)
                    .SingleAsync();

        return (number, slug);
    }
}
