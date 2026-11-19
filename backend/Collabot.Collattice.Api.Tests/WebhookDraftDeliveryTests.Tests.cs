using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// What a webhook subscriber receives for a card composed as a draft (the create dialog: the card is
// an unsaved draft while files are attached to it). Driven end to end: the REST calls the dialog
// makes, the hosted dispatcher, the real queue and sender, and a subscriber selecting every event.
public class WebhookDraftDeliveryTests
{
    private const string _url = "https://subscriber.test/hook";

    [Fact]
    public async Task DraftSaved_CardCreatedArrivesFirst_ThenWhatTheDraftStillHas()
    {
        // Arrange — a draft given two files and a comment, one file removed again before the save.
        await using var factory = await NewFactoryAsync();
        var client = AdminClient(factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        var draftId = await CreateDraftAsync(client, factory, laneId, "Composed with files");
        var kept = await UploadAsync(client, draftId, "kept.txt");
        var removed = await UploadAsync(client, draftId, "removed.txt");
        (await client.DeleteAsync($"/api/v1/attachments/{removed}")).EnsureSuccessStatusCode();
        await CommentAsync(client, draftId, "Noted while composing");

        // Act — the save.
        var number = await FinalizeAsync(client, draftId);
        var received = await WaitForRequestsAsync(factory, 3);

        // Assert — the card first, then the file and the comment it still has, each on the saved
        // card's real number; nothing about the removed file, and nothing from before the save.
        received.Select(EventType).ShouldBe(["card.created", "attachment.created", "comment.created"]);

        var attachment = Data(received[1]);
        attachment.GetProperty("attachment").GetProperty("id").GetGuid().ShouldBe(kept);
        attachment.GetProperty("attachment").GetProperty("fileName").GetString().ShouldBe("kept.txt");
        attachment.GetProperty("attachment").GetProperty("sizeBytes").GetInt64().ShouldBe(4);
        attachment.GetProperty("card").GetProperty("number").GetInt64().ShouldBe(number);

        var comment = Data(received[2]);
        comment.GetProperty("comment").GetProperty("contentMarkdown").GetString().ShouldBe("Noted while composing");
        comment.GetProperty("card").GetProperty("number").GetInt64().ShouldBe(number);

        await AssertNothingElseArrivesAsync(factory, 3);
    }

    [Fact]
    public async Task DraftCancelled_AfterAFileAndAComment_SubscriberHearsNothing()
    {
        // Arrange — a draft given a file and a comment, then cancelled.
        await using var factory = await NewFactoryAsync();
        var client = AdminClient(factory);
        var laneId = await TestDataHelper.GetFirstLaneIdAsync(client, factory.DefaultBoardId);

        var draftId = await CreateDraftAsync(client, factory, laneId, "Abandoned");
        await UploadAsync(client, draftId, "abandoned.txt");
        await CommentAsync(client, draftId, "Never saved");
        (await client.PostAsync($"/api/v1/cards/{draftId}/cancel", null)).EnsureSuccessStatusCode();

        // Act — a card created afterwards, whose event the dispatcher delivers after anything queued
        // before it.
        var sentinel = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards", new { name = "Created after the cancel", laneId });
        sentinel.EnsureSuccessStatusCode();
        var received = await WaitForRequestsAsync(factory, 1);

        // Assert — the first and only delivery is the later card.
        EventType(received[0]).ShouldBe("card.created");
        Data(received[0]).GetProperty("card").GetProperty("name").GetString().ShouldBe("Created after the cancel");

        await AssertNothingElseArrivesAsync(factory, 1);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<WebhookDeliveryFactory> NewFactoryAsync()
    {
        var factory = new WebhookDeliveryFactory
        {
            ConfigOverrides = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Webhooks:Endpoint"] = null,
                ["Webhooks:RetryBackoffBase"] = "00:00:00.010",
            },
        };

        await factory.InitializeAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

        db.WebhookSubscriptions.Add(new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Url = _url,
            Enabled = true,
            EventTypes = [WebhookEventTypes.Wildcard],
        });

        await db.SaveChangesAsync();

        return factory;
    }

    private static HttpClient AdminClient(WebhookDeliveryFactory factory)
    {
        var client = factory.CreateClient();
        TestAuthHelper.SetAdminAuth(client, factory);
        return client;
    }

    private static async Task<Guid> CreateDraftAsync(HttpClient client, WebhookDeliveryFactory factory, Guid laneId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/boards/{factory.DefaultBoardId}/cards/temp", new { name, laneId });
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> UploadAsync(HttpClient client, Guid cardId, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent([1, 2, 3, 4]);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", fileName);

        var response = await client.PostAsync($"/api/v1/cards/{cardId}/attachments", content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("id").GetGuid();
    }

    private static async Task CommentAsync(HttpClient client, Guid cardId, string text)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = text });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<long> FinalizeAsync(HttpClient client, Guid draftId)
    {
        var response = await client.PostAsync($"/api/v1/cards/{draftId}/finalize", null);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        return json.GetProperty("number").GetInt64();
    }

    private static async Task<List<CapturedRequest>> WaitForRequestsAsync(WebhookDeliveryFactory factory, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        List<CapturedRequest> received = [];

        try
        {
            while (received.Count < count)
            {
                received.Add(await factory.Handler.WaitForNextRequestAsync(timeout.Token));
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException($"Expected {count.ToString(CultureInfo.InvariantCulture)} webhook deliveries within 30 s, but {received.Count.ToString(CultureInfo.InvariantCulture)} arrived.", ex);
        }

        return received;
    }

    // The dispatcher polls its queue every half second, so a few polls without another delivery
    // means nothing else was queued.
    private static async Task AssertNothingElseArrivesAsync(WebhookDeliveryFactory factory, int delivered)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));

        factory.Handler.RequestCount.ShouldBe(delivered);
    }

    private static string? EventType(CapturedRequest request) =>
        JsonDocument.Parse(request.Body).RootElement
            .GetProperty("event")
                .GetString();

    private static JsonElement Data(CapturedRequest request) =>
        JsonDocument.Parse(request.Body).RootElement
            .GetProperty("data")
                .Clone();
}
