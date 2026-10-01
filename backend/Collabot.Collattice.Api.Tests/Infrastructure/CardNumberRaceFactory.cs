using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// Adds the card-number race interceptor to the standard harness and changes nothing else, for the
// same reasons the revision-race factory is its own class: it commits a rival card mid-save, which no
// other test pays for or expects, and the interceptor has to be attached to the DbContext options
// explicitly to fire.
public class CardNumberRaceFactory : CollatticeApiFactory
{
    public CardNumberRaceInterceptor Interceptor => Services.GetRequiredService<CardNumberRaceInterceptor>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services => services.AddSingleton<CardNumberRaceInterceptor>());
    }

    protected override void ConfigureDbContext(IServiceProvider serviceProvider, DbContextOptionsBuilder options) =>
        options.AddInterceptors(serviceProvider.GetRequiredService<CardNumberRaceInterceptor>());
}
