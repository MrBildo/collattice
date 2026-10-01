using System.Collections.Concurrent;
using Collabot.Collattice.Api.Hosting.UpdateCheck;
using Collabot.Collattice.Api.Hosting.Webhooks;
using Collabot.Collattice.Api.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

public class CollatticeApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private readonly ConcurrentQueue<Uri> _gitHubAttempts = new();

    public const string TestAdminAuthKey = "test-admin-auth-key-12345678";
    public string AdminAuthKey { get; private set; } = string.Empty;
    public Guid DefaultBoardId { get; private set; }

    // Every request this host attempted to api.github.com, in arrival order. Empty is the only
    // acceptable state at disposal.
    public IReadOnlyCollection<Uri> GitHubAttempts => _gitHubAttempts;

    // In-memory config overrides applied after the baseline test config. Lets a test
    // flip Hosting:ServeSpa, populate Cors:AllowedOrigins, set ASPNETCORE_ENVIRONMENT,
    // etc. Null (the IClassFixture default) preserves today's behavior.
    public IReadOnlyDictionary<string, string?>? ConfigOverrides { get; init; }

    // Forces the host environment. Null keeps WebApplicationFactory's default
    // (Development). "Production" exercises the non-dev CORS named-policy branch.
    public string? EnvironmentName { get; init; }

    public static CollatticeApiFactory WithConfig(IReadOnlyDictionary<string, string?> overrides) =>
        new() { ConfigOverrides = overrides };

    public static CollatticeApiFactory WithConfig
    (
        string environmentName,
        IReadOnlyDictionary<string, string?> overrides
    ) =>
        new() { EnvironmentName = environmentName, ConfigOverrides = overrides };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (EnvironmentName is not null)
        {
            builder.UseEnvironment(EnvironmentName);
        }

        // ConnectionStrings:Board is required configuration with no fallback;
        // Program.cs hard-fails at host-build time if it is unset.
        // That eager read happens before ConfigureServices swaps in the shared
        // in-memory connection, so the value must be visible to
        // builder.Configuration — i.e. injected via UseSetting, not
        // ConfigureAppConfiguration (the WAF eager-read seam). ":memory:" is
        // the special data source Program.cs's guard exempts from path
        // validation and directory creation; the actual test database is the
        // shared SqliteConnection swapped in below.
        builder.UseSetting("ConnectionStrings:Board", "Data Source=:memory:");

        // UseSetting feeds the web-host builder configuration, which
        // WebApplication.CreateBuilder incorporates BEFORE Program.cs reads
        // builder.Configuration. ConfigureAppConfiguration delegates run later and
        // are not visible to eager builder.Configuration reads (e.g. the AddCors
        // policy snapshot). Overrides must go through UseSetting to exercise the
        // same early-resolution path production uses via Cors__AllowedOrigins__N
        // environment variables.
        if (ConfigOverrides is not null)
        {
            foreach (var (key, value) in ConfigOverrides)
            {
                builder.UseSetting(key, value);
            }
        }

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:AuthKey"] = TestAdminAuthKey,

                // The temp-card and delivery-log sweeps run in every test host, and neither touches
                // the database until its first timer tick. Pinning the temp-card interval to a day
                // keeps that tick out of reach of any test host whatever the production default
                // becomes, so neither sweep can race a test thread on the shared connection. A test
                // that needs the running loop overrides this and brings a database of its own.
                ["TempCardSweep:SweepInterval"] = "1.00:00:00",
            });

            if (ConfigOverrides is not null)
            {
                config.AddInMemoryCollection(ConfigOverrides);
            }
        });

        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BoardDbContext>));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();

            services.AddDbContext<BoardDbContext>((serviceProvider, options) =>
            {
                UseTestDatabase(options);
                ConfigureDbContext(serviceProvider, options);
            });

            // Remove the webhook dispatcher hosted service by default. It queries WebhookSubscriptions
            // on every drained card event (Webhooks:Enabled defaults true — IsConfigured no
            // longer gates on a configured endpoint), and the WAF's single shared in-memory SQLite
            // connection is not concurrency-safe across threads — that background query races a test
            // thread's card SaveChanges under suite load (the "a BackgroundService must do no DB
            // work the test thread can race" discipline, extended from startup to per-drain work).
            // The webhook DELIVERY tests opt back in via WebhookDeliveryFactory (RunDispatcher).
            var dispatcher = services.SingleOrDefault(d =>
                d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType == typeof(WebhookDispatcherService));
            if (dispatcher is not null)
            {
                services.Remove(dispatcher);
            }

            // Keep every test host off the real GitHub API. The update-check hosted service still
            // runs (it does no database work, so it cannot race the shared connection), but it
            // polls a source that answers "no release known" instead of api.github.com. Tests that
            // need a release replace this source with their own.
            var versionSource = services.SingleOrDefault(d => d.ServiceType == typeof(ILatestVersionSource));
            if (versionSource is not null)
            {
                services.Remove(versionSource);
            }

            services.AddSingleton<ILatestVersionSource, NoNetworkVersionSource>();

            // Backstop for any path the swap above does not cover: every IHttpClientFactory client
            // refuses api.github.com and records the attempt, and DisposeAsync fails on a record.
            services.ConfigureHttpClientDefaults(http =>
                http.AddHttpMessageHandler(() => new GitHubEgressGuardHandler(_gitHubAttempts)));
        });
    }

    // Every context in a standard test host shares the one in-memory connection, so all database
    // work must stay on the test's own thread. A derived factory that runs a hosted service doing
    // database work alongside requests overrides this with a database each context opens its own
    // connection to: one SqliteConnection cannot be used from two threads, and opening a second
    // context on it while another thread's statement is active fails outright.
    protected virtual void UseTestDatabase(DbContextOptionsBuilder options) => options.UseSqlite(_connection);

    // Lets a derived factory add to the test DbContext without re-registering it. Re-registration
    // would drop the shared in-memory connection this class owns, which is the whole harness.
    // No-op here, so the standard factory behaves exactly as it did.
    protected virtual void ConfigureDbContext(IServiceProvider serviceProvider, DbContextOptionsBuilder options)
    {
    }

    public async Task InitializeAsync()
    {
        // Force host creation which triggers Program.cs seed logic
        _ = CreateClient();

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var admin = await db.Users.FirstAsync(u => u.Role == UserRole.Administrator);
        AdminAuthKey = admin.AuthKey;

        var board = await db.Set<Board>().FirstAsync();
        DefaultBoardId = board.Id;
    }

    public new async Task DisposeAsync()
    {
        // Stop the host — and every hosted BackgroundService — BEFORE disposing the shared
        // connection. The dispatcher and sweep services run DB work against this one connection;
        // disposing it while a service is mid-SaveChanges throws "Collection was modified" out of
        // the connection's own command bookkeeping. Base disposal awaits hosted-service shutdown,
        // so once it returns nothing can still touch the connection. (Disposing the connection first
        // was a latent teardown race, rare until a test proceeds to disposal the instant delivery
        // lands — before the dispatcher has finished persisting its attempt row.)
        await base.DisposeAsync();
        await _connection.DisposeAsync();

        // Checked after teardown so a failure here never leaks the host or the connection. An
        // IClassFixture's disposal failure is reported against its test class, so an attempt from
        // a hosted service (whose own failures are swallowed) still turns the run red.
        if (!_gitHubAttempts.IsEmpty)
        {
            throw new InvalidOperationException
            (
                $"A test host attempted {_gitHubAttempts.Count} request(s) to "
                + $"{GitHubEgressGuardHandler.BlockedHost}; tests must not reach the real GitHub API: "
                + string.Join(", ", _gitHubAttempts)
            );
        }
    }
}
