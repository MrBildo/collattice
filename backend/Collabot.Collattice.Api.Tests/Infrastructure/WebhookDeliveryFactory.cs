using Collabot.Collattice.Api.Hosting.Webhooks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// CollatticeApiFactory variant for the webhook DELIVERY tests. Unlike
// WebhookTestFactory (which swaps the sink to capture enqueued events without delivery), this
// keeps the REAL production pipeline — the WebhookQueue, the typed HttpClient, HMAC signing,
// retry, persistence — and only swaps the dispatcher's HttpClient primary handler for a
// CapturingHttpMessageHandler. So the test exercises the actual send path (serialize-once + sign
// + headers) producing real bytes; the handler captures those bytes instead of hitting a socket.
//
// Two test modes:
//  - RunDispatcher = true (default): the WebhookDispatcherService hosted service runs and drains
//    the queue. Used by the "delivery happens after the 201" / dark-no-op tests, which assert on
//    the capturing handler or on a count-of-zero (no DB read race).
//  - RunDispatcher = false: the hosted dispatcher is removed so the test OWNS the queue and drives
//    WebhookDispatcherService.DeliverEventAsync deterministically against a scope's DbContext —
//    the TempCardSweepService.SweepAsync pattern. The test then decides exactly when delivery
//    happens and when the rows it wrote are read back, which is the right way to verify persistence.
//
// Webhooks config (Endpoint / Secret / MaxAttempts / a near-zero RetryBackoffBase) flows through
// the base ConfigOverrides path — both UseSetting (early) and ConfigureAppConfiguration (late),
// per the WAF eager-read seam.
public sealed class WebhookDeliveryFactory : CollatticeApiFactory
{
    // A database file per host, opened fresh by every context. The running dispatcher queries the
    // subscription registry while the request that enqueued the event is still reading from its own
    // context; on the base harness's single shared connection, the dispatcher's context then failed
    // to initialise ("unable to delete/modify user-function due to active statements"), the tick
    // dropped the already-dequeued event, and the test waited out its 30 s backstop for a POST that
    // could never come. Separate connections over a WAL file are the production model, where a
    // reader does not stop another connection's work. Pooling stays on: with it off, every context
    // reopened the file and a card create took about ten times as long, enough to push the
    // slow-endpoint test past its 2 s bound on a loaded box.
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"collattice-delivery-{Guid.NewGuid():N}.db");

    public CapturingHttpMessageHandler Handler { get; } = new();

    public bool RunDispatcher { get; init; } = true;

    protected override void UseTestDatabase(DbContextOptionsBuilder options) =>
        options.UseSqlite($"Data Source={_databasePath}");

    // The delivery tests dispose through WebApplicationFactory's own DisposeAsync, so the files are
    // removed when the host stops rather than from a disposal override.
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        host.Services
            .GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopped.Register(() => PersistentDatabaseFactory.DeleteDatabaseFiles(_databasePath));

        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            // Re-point the dispatcher's typed HttpClient at the capturing handler. The last
            // ConfigurePrimaryHttpMessageHandler registration for the named client wins, so the
            // real HttpWebhookSender (serialize-once + sign + headers) runs against our stub.
            services
                .AddHttpClient<IWebhookSender, HttpWebhookSender>()
                .ConfigurePrimaryHttpMessageHandler(() => Handler);

            // The base CollatticeApiFactory removes the hosted dispatcher (it races the shared
            // in-memory connection). Re-add it for the end-to-end delivery tests that assert
            // on the running dispatcher; the persistence tests leave it off (RunDispatcher = false)
            // and drive the deterministic DeliverEventAsync seam directly, fully owning the queue.
            if (RunDispatcher)
            {
                services.AddHostedService<WebhookDispatcherService>();
            }
        });
    }
}
