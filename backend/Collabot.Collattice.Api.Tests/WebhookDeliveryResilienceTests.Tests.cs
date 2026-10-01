using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The delivery-time SSRF-blocked signal must survive the PRODUCTION HttpClient configuration.
//
// AddServiceDefaults wires AddStandardResilienceHandler onto every typed client via
// ConfigureHttpClientDefaults. For webhook delivery that handler reads the SSRF connect-throw
// (WebhookSsrfBlockedException, wrapped by SocketsHttpHandler in HttpRequestException) as a transient
// fault and retries it until HttpClient.Timeout — so the recorded delivery error becomes a generic
// timeout, MASKING the guard's authentic "resolves to a blocked address" message (the delivery-time
// blocked-target signal the delivery log and the admin UI's blocked-state read). Program.cs opts the
// webhook client out via .RemoveAllResilienceHandlers().
//
// The foundation test (WebhookSsrfGuardTests.BlockedConnect_SurfacesAsFailedDeliveryResult) constructs
// HttpWebhookSender with a BARE HttpClient — no resilience handler — so it never exercised the
// production-wrapped path and could not catch the masking. This test resolves the REAL IWebhookSender
// from the application's DI (the production client: resilience-default + the webhook-client opt-out + the SSRF
// connect callback) and asserts the recorded error is the guard's authentic phrasing, not a timeout.
//
// Mutation-revert check: drop .RemoveAllResilienceHandlers() in Program.cs and this test
// reds — the resilience handler retries the connect-throw to a 5s timeout and the authentic phrasing
// no longer appears in the recorded error.
public class WebhookDeliveryResilienceTests
{
    [Fact]
    public async Task BlockedConnect_RecordsAuthenticSsrfError_ThroughProductionClientConfiguration()
    {
        await using var factory = new CollatticeApiFactory();
        await factory.InitializeAsync();

        // The production-configured client — NOT a bare HttpClient. The base factory does not stub the
        // webhook handler, so this carries the real SSRF connect callback plus whatever resilience
        // wiring Program.cs settled (the webhook-client opt-out).
        var sender = factory.Services.GetRequiredService<IWebhookSender>();

        var boardEvent = SampleEvent();
        var target = new WebhookTarget("http://127.0.0.1:9/hook", Secret: null);

        var result = await sender.SendAsync(boardEvent, target, CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.StatusCode.ShouldBeNull();   // blocked at connect — no response, no status

        // The authentic guard phrasing the admin UI's "blocked — private target" state binds to —
        // present in the recorded error, not masked behind a generic resilience-retry timeout.
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("resolves to a blocked address");
        result.Error.ShouldContain("127.0.0.1");
    }

    private static BoardEvent SampleEvent() =>
        new
        (
            WebhookEventTypes.CardCreated,
            Ulid.NewUlid().ToString(),
            DateTimeOffset.UtcNow,
            "1",
            Guid.NewGuid(),
            "board-slug",
            new BoardEventActor(Guid.NewGuid(), "Tester", "Administrator"),
            new { card = new { id = Guid.NewGuid() } }
        );
}
