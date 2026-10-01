using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// CollatticeApiFactory variant that swaps the production WebhookQueue for a
// CapturingWebhookSink, exposed via Sink so a test can read the events the seam
// enqueued. This capture path has no dispatcher/HTTP — the sink IS the observable.
public class WebhookTestFactory : CollatticeApiFactory
{
    public CapturingWebhookSink Sink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            // The broadcaster's typed publish path and BulkCardTools both resolve
            // IWebhookSink; replacing that single registration routes every enqueued event
            // into the capturing sink. WebhookQueue stays registered but unused here — the
            // dispatcher that drains it is covered by the delivery tests instead.
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IWebhookSink));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IWebhookSink>(Sink);
        });
    }
}
