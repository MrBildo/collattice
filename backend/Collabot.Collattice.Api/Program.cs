using System.Globalization;
using System.Reflection;
using Collabot.Collattice.Api;
using Collabot.Collattice.Api.Auth;
using Collabot.Collattice.Api.Configuration;
using Collabot.Collattice.Api.Endpoints;
using Collabot.Collattice.Api.Events;
using Collabot.Collattice.Api.Hosting;
using Collabot.Collattice.Api.Hosting.UpdateCheck;
using Collabot.Collattice.Api.Hosting.Webhooks;
using Collabot.Collattice.Api.Installation;
using Collabot.Collattice.Api.Mcp;
using Collabot.Collattice.Api.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

// --version flag
if (args.Contains("--version"))
{
    var raw = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";
    Console.WriteLine($"Collattice {raw.Split('+')[0]}");
    return;
}

// Installer-invoked subcommand: merge a freshly-shipped appsettings.json against the operator's
// on-disk file (preserve edits, refresh untouched defaults, add new keys). Runs before any host
// setup so this path never touches the database, the host, or the network.
if (args.Length > 0 && args[0] == "--merge-appsettings")
{
    Environment.Exit(AppSettingsMergeCli.Run(args[1..], Console.Out, Console.Error));
}

var builder = WebApplication.CreateBuilder(args);

// WebApplication.CreateBuilder adds an env-var provider at builder-construction time. The
// re-add below ensures env vars sit at the top of the provider chain even if a future JSON
// source is added after construction (ConfigPrecedenceTests locks the ordering). The
// resolved precedence: env (Section__Key) > appsettings.json > hardcoded default — the
// .Local.json overlay channel was retired.
builder.Configuration.AddEnvironmentVariables();

// Listen-address dual-pattern: `urls` / `ASPNETCORE_URLS` wins (Aspire dev, hosting-injected,
// operator override); otherwise the structured Hosting: settings build the bind URL. Runs
// before AddServiceDefaults so the host's URL story is settled before Aspire's hooks register.
var bindUrl = HostingBindResolver.Resolve(builder.Configuration);
if (bindUrl is not null)
{
    builder.WebHost.UseUrls(bindUrl);
}

builder.AddServiceDefaults();

builder.Services.AddOpenApi();

builder.Services.Configure<HostingSettings>(builder.Configuration.GetSection(HostingSettings.SectionName));
builder.Services.Configure<CorsSettings>(builder.Configuration.GetSection(CorsSettings.SectionName));

var corsSettings = builder.Configuration
    .GetSection(CorsSettings.SectionName)
    .Get<CorsSettings>() ?? new CorsSettings();

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicies.Default, policy =>
    {
        if (corsSettings.AllowedOrigins.Count == 0)
        {
            // Empty allow-list: no origins authorized. The middleware still runs and
            // simply emits no Access-Control-Allow-Origin header. Same-origin requests
            // are unaffected (browser doesn't preflight same-origin).
            return;
        }

        policy
            .WithOrigins([.. corsSettings.AllowedOrigins])
            .AllowAnyMethod()
            .AllowAnyHeader()           // X-User-Key is in there
            .AllowCredentials();        // cookies + auth headers permitted
    });
});

// The writable database location is a told input — never derived from the working
// directory, the binary directory, or $HOME. ConnectionStrings:Board is required
// configuration with no fallback; an absolute path is required. Misconfiguration
// fails loud and early here, naming the key and the offending value, rather than
// degrading to a cwd-relative guess that lands unpredictably under a hardened
// deployment. Each actionable failure carries a copy-paste-ready remedy in
// both forms a user might use — environment variable and appsettings.json — so a
// manual-download user can fix it in one step.
static string ExampleDbConnectionString() =>
    OperatingSystem.IsWindows()
        ? @"Data Source=C:\collattice\data\collattice.db"
        : "Data Source=/var/lib/collattice/collattice.db";

static string ConfigRemedy()
{
    var example = ExampleDbConnectionString();
    var jsonValue = example.Replace(@"\", @"\\", StringComparison.Ordinal);

    return $$"""
        To fix this, set 'ConnectionStrings:Board' to an absolute path in either form:

          - Environment variable:
              ConnectionStrings__Board={{example}}

          - appsettings.json (next to the executable; edits survive upgrades via the smart-merge):
              {
                "ConnectionStrings": {
                  "Board": "{{jsonValue}}"
                }
              }
        """;
}

var connectionString = builder.Configuration.GetConnectionString("Board");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException
    (
        "Required configuration 'ConnectionStrings:Board' is not set. The application "
        + "does not derive a database path from the working or binary directory.\n\n"
        + ConfigRemedy()
    );
}

var dbPath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;

// SQLite special data sources (`:memory:`, empty/temp) carry no filesystem path —
// the in-memory test database relies on this. Real filesystem paths must be
// absolute and writable; anything else fails loud here, before EF opens the DB.
var isSpecialDataSource = string.IsNullOrEmpty(dbPath) || dbPath == ":memory:";
if (!isSpecialDataSource)
{
    if (!Path.IsPathRooted(dbPath))
    {
        throw new InvalidOperationException
        (
            $"Configuration 'ConnectionStrings:Board' resolves to a relative data "
            + $"source '{dbPath}'. An absolute path is required so the database "
            + "location does not depend on the process working directory.\n\n"
            + ConfigRemedy()
        );
    }

    var dbDir = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrEmpty(dbDir))
    {
        try
        {
            Directory.CreateDirectory(dbDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException
            (
                $"The database directory '{dbDir}' (from 'ConnectionStrings:Board' "
                + $"= '{dbPath}') could not be created or is not writable: {ex.Message}. "
                + "Point 'ConnectionStrings:Board' at an absolute path the process "
                + $"can write under this deployment's sandbox.\n\n{ConfigRemedy()}",
                ex
            );
        }
    }
}

builder.Services.AddDbContext<BoardDbContext>(options => options.UseSqlite(connectionString));

builder.Services.Configure<AttachmentSettings>(builder.Configuration.GetSection(AttachmentSettings.SectionName));
builder.Services.Configure<TempCardSweepSettings>(builder.Configuration.GetSection(TempCardSweepSettings.SectionName));
builder.Services.AddHttpContextAccessor();

// Webhooks — the in-memory sink the broadcaster's typed Publish path enqueues to;
// the dispatcher drains it. Registered as IWebhookSink (the durable-outbox swap-point) AND
// as the concrete WebhookQueue so the dispatcher can drain via TryDequeue. Both resolve the
// same singleton instance.
builder.Services.AddSingleton<WebhookQueue>();
builder.Services.AddSingleton<IWebhookSink>(sp => sp.GetRequiredService<WebhookQueue>());
builder.Services.AddSingleton<BoardEventBroadcaster>();

// Webhook delivery: the dispatcher (a singleton BackgroundService) drains the queue and
// hands each enriched event to IWebhookSender — a typed HttpClient behind a seam (mirroring the
// UpdateCheck ILatestVersionSource shape) so the HTTP send is stubbable in tests and the slow-
// GitHub timeout pattern carries over. The sender serializes once, signs (HMAC) when a secret is
// set, and POSTs; the dispatcher owns bounded retry, the persisted delivery-attempt log, and the
// loud drop. Dark-by-default — no Webhooks:Endpoint (or Webhooks:Enabled=false) means no outbound
// calls (the kill switch). The per-POST timeout is the typed client's Timeout so a slow endpoint
// is a failed attempt, not a wait.
builder.Services.Configure<WebhookSettings>(builder.Configuration.GetSection(WebhookSettings.SectionName));

var webhookSettings = builder.Configuration
    .GetSection(WebhookSettings.SectionName)
    .Get<WebhookSettings>() ?? new WebhookSettings();

// EXTEXP0001: RemoveAllResilienceHandlers is marked [Experimental]; it is the documented Aspire
// mechanism to opt a single client out of the standard resilience handler, and the diagnostic's own
// guidance is to suppress to proceed. Pinned at Microsoft.Extensions.Http.Resilience 10.4.0; a future
// rename surfaces as a loud compile error, not a silent runtime regression. Scoped to this one
// registration so no other accidental experimental-API use is silenced.
#pragma warning disable EXTEXP0001
builder.Services
    .AddHttpClient<IWebhookSender, HttpWebhookSender>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Collattice-Webhooks");
        client.Timeout = webhookSettings.DeliveryTimeout;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // SSRF floor controls 3-4: refuse redirects (a 302-to-internal would walk around the
        // IP checks) and pin every connection to a validated IP (the DNS-rebind defense). The
        // allowPrivate flag is read from the startup-bound settings so it agrees with the store's
        // registration validator.
        AllowAutoRedirect = false,
        ConnectCallback = SsrfGuard.CreateConnectCallback(webhookSettings.AllowPrivateNetworkTargets),
    })
    // Opt this client OUT of ServiceDefaults' standard resilience handler. AddServiceDefaults
    // wires AddStandardResilienceHandler onto EVERY typed client via ConfigureHttpClientDefaults; for
    // webhook delivery that handler is both redundant and harmful. Redundant: WebhookDispatcherService
    // already owns the bounded retry loop (its MaxAttempts loop is the single retry authority), so the
    // standard handler retries on top of it — a double-retry. Harmful: the SSRF connect guard throws
    // WebhookSsrfBlockedException, SocketsHttpHandler wraps it in HttpRequestException, and the standard
    // handler reads that as a transient fault and retries it until HttpClient.Timeout (DeliveryTimeout)
    // elapses — so the recorded WebhookDeliveryAttempt carries a generic timeout instead of the guard's
    // authentic "resolves to a blocked address" error (the delivery-time blocked-target signal the
    // delivery log and the admin UI's blocked-state read). Removing it restores one connect attempt per
    // send and records the authentic error.
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

// The shared CRUD/validation core both the REST endpoints and MCP tools delegate to, so the
// SSRF validation and the write-only-secret projection are un-bypassable. WebhookTester is the
// shared test-delivery (ping) seam both surfaces delegate to — it dials through the same
// SSRF-guarded IWebhookSender, so a ping cannot bypass the delivery guard.
builder.Services.AddScoped<WebhookSubscriptionStore>();
builder.Services.AddScoped<WebhookTester>();
builder.Services.AddHostedService<WebhookDispatcherService>();
// Ages out old WebhookDeliveryAttempt rows (Webhooks:DeliveryLogRetentionDays; dormant
// when 0). The catalog × subscription fan-out makes the log grow faster than v1's single endpoint.
builder.Services.AddHostedService<WebhookDeliveryLogSweepService>();
builder.Services.AddScoped<IUserResolver, UserResolver>();
builder.Services.AddScoped<McpAuthService>();
builder.Services.AddHostedService<TempCardSweepService>();

// Update check: a single backend poll of the GitHub Releases API per instance feeds a
// server-side cache that the /version/status endpoint reads. The kill switch
// (UpdateCheck:Enabled = false) gates the hosted service off entirely so no outbound call is
// ever made. The cache is a singleton (shared between the writer hosted service and the reader
// endpoint); the source is a typed HttpClient behind the ILatestVersionSource seam so the
// GitHub dependency can be swapped without touching the endpoint or the frontend.
builder.Services.Configure<UpdateCheckSettings>(builder.Configuration.GetSection(UpdateCheckSettings.SectionName));
builder.Services.AddSingleton<VersionStatusCache>();
builder.Services
    .AddHttpClient<ILatestVersionSource, GitHubReleaseVersionSource>(client =>
    {
        // The unauthenticated GitHub REST API requires a User-Agent and an explicit API
        // version header. The client owns its own short timeout so a slow GitHub never holds
        // up the poll loop (the timer cadence, not retries, governs re-checks).
        // api.github.com is the fixed egress target; the mirror/Source seam is deferred.
        // S1075 is suppressed here because the URL is deliberate and correct.
#pragma warning disable S1075 // URIs should not be hardcoded
        client.BaseAddress = new Uri("https://api.github.com/");
#pragma warning restore S1075
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Collattice-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.Timeout = TimeSpan.FromSeconds(10);
    });
builder.Services.AddHostedService<UpdateCheckService>();

// The request-body limits must track the configured REST upload cap — Kestrel and
// FormOptions reject oversize bodies with a 413 before the endpoint's friendlier 400
// runs, so binding both to MaxRestUploadBytes keeps the framework floor in lockstep
// with the application cap instead of duplicating a magic number.
var attachmentSettings = builder.Configuration
    .GetSection(AttachmentSettings.SectionName)
    .Get<AttachmentSettings>() ?? new AttachmentSettings();

builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = attachmentSettings.MaxRestUploadBytes);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = attachmentSettings.MaxRestUploadBytes);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly()
    .WithRequestFilters(filters =>
    {
        // Default SDK behaviour swallows non-McpException tool failures
        // as "An error occurred invoking '<tool>'." with no detail — a typo'd
        // parameter name then drives multi-step false-alarm investigations.
        // McpErrorTranslator.WrapForCallTool catches the input-validation
        // shapes and rethrows as McpException so the wrapper renders
        // "<tool>': <Type — Message>". Server-internal failures (DB, EF,
        // downstream) deliberately fall through, preserving the body-less
        // wrapper response so infrastructure detail does not leak.
        filters.AddCallToolFilter(McpErrorTranslator.WrapForCallTool);
    });

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

    // Auto-backup SQLite DB before applying pending migrations
    var pendingMigrations = await db.Database.GetPendingMigrationsAsync();
    if (pendingMigrations.Any())
    {
        var currentConnectionString = db.Database.GetConnectionString();
        var currentDbPath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(currentConnectionString).DataSource;
        if (File.Exists(currentDbPath))
        {
            var backupPath = $"{currentDbPath}.bak-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
            File.Copy(currentDbPath, backupPath);
            app.Logger.LogInformation
            (
                "Database backed up to {BackupPath} before applying {Count} pending migration(s)",
                backupPath,
                pendingMigrations.Count()
            );
        }
    }

    await db.Database.MigrateAsync();

    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = 'wal';");
    await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout = 5000;");

    if (!await db.Users.AnyAsync())
    {
        var adminAuthKey = app.Configuration.GetValue<string>("Admin:AuthKey")
            ?? Ulid.NewUlid().ToString();

        var adminUser = new BoardUser
        {
            Id = Guid.NewGuid(),
            Name = "Admin",
            AuthKey = adminAuthKey,
            Role = UserRole.Administrator,
        };
        db.Users.Add(adminUser);

        var defaultBoard = new Board
        {
            Id = Guid.NewGuid(),
            Name = "Default",
            Slug = "default",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Boards.Add(defaultBoard);

        // Shared scaffold every board gets: archive lane, S/M/L/XL sizes, starter
        // labels (Feature/Bug/Chore). Routed through BoardSeeder so the install seed
        // and the API/MCP create_board path can't drift on the shared portion —
        // REST/MCP/install seed drift is the top bug class here, gated by
        // BoardCreateParityTests. The install-only first-run extras (the three visible
        // lanes + the welcome sample card) are layered on below.
        BoardSeeder.Seed(db, defaultBoard);

        var backlogLane = new Lane { Id = Guid.NewGuid(), BoardId = defaultBoard.Id, Name = "Backlog", Position = 0 };

        db.Lanes.AddRange
        (
            backlogLane,
            new Lane { Id = Guid.NewGuid(), BoardId = defaultBoard.Id, Name = "In Progress", Position = 1 },
            new Lane { Id = Guid.NewGuid(), BoardId = defaultBoard.Id, Name = "Done", Position = 2 }
        );

        // Welcome sample card — install-only first-run onboarding. A real,
        // openable card in Backlog that teaches how a card works (markdown body + a
        // label in situ), explicitly a deletable sample. Install-only by design: a
        // programmatic create_board (used by admins/agents who already know the
        // product) should not auto-litter a sample card to delete. References the
        // lowest-ordinal size (S) and the Feature starter label, both seeded above.
        // Read from the change tracker's Local view — BoardSeeder.Seed added these
        // but nothing is persisted until the single SaveChangesAsync below, so a DB
        // query would not yet see them.
        var smallSize = db.CardSizes.Local
            .Where(s => s.BoardId == defaultBoard.Id)
            .OrderBy(s => s.Ordinal)
                .First();

        var featureLabel = db.Labels.Local
            .Single(l => l.BoardId == defaultBoard.Id && l.Name == "Feature");

        var welcomeCard = new CardItem
        {
            Id = Guid.NewGuid(),
            Number = 1,
            BoardId = defaultBoard.Id,
            Name = "Welcome to Collattice — here's how a card works",
            DescriptionMarkdown =
                "**This is a sample card.** Feel free to delete it once you've had a look around.\n\n" +
                "A card is the basic unit of work on a board. Here's what you can do with one:\n\n" +
                "- **Open it** by clicking — you're reading the description right now.\n" +
                "- **Describe it** in Markdown: lists, **bold**, `code`, and links all render.\n" +
                "- **Label it** to tag the kind of work — this card carries the green **Feature** label.\n" +
                "- **Size it** (S / M / L / XL) to capture rough effort.\n" +
                "- **Drag it** between lanes as the work moves from Backlog toward Done.\n\n" +
                "When you're ready, delete this card and create your own.",
            SizeId = smallSize.Id,
            LaneId = backlogLane.Id,
            Position = 0,
            CreatedByUserId = adminUser.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastUpdatedByUserId = adminUser.Id,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Cards.Add(welcomeCard);

        db.CardLabels.Add(new CardLabel { CardId = welcomeCard.Id, LabelId = featureLabel.Id });

        await db.SaveChangesAsync();
    }

    // Always log the admin auth key at startup
    var admin = await db.Users.FirstOrDefaultAsync(u => u.Role == UserRole.Administrator);
    if (admin is not null)
    {
        app.Logger.LogInformation("Admin auth key: {AuthKey}", admin.AuthKey);
    }

    // Migrate the v1 single configured webhook endpoint into the subscription registry on
    // first v2 boot. DELIBERATELY independent of the !Users.AnyAsync() fresh-install block above:
    // production already has users, so reusing that gate would never fire on upgrade and would
    // silently drop the working prod webhook. Gated instead on an empty subscription table.
    await WebhookConfigSeeder.SeedAsync
    (
        db,
        app.Configuration.GetValue<string>("Webhooks:Endpoint"),
        app.Configuration.GetValue<string>("Webhooks:Secret"),
        CancellationToken.None
    );
}

// Outermost so it observes the final status of every request: fills a bodyless 405 from
// routing with a readable message naming the allowed methods, leaving the Allow header and
// every other response untouched.
app.UseMethodNotAllowedBody();

// Behind a TLS-terminating reverse proxy (production: Collabhost's Caddy, dialing this process
// over plain HTTP on localhost) the request scheme Kestrel sees is http, so anything that echoes
// the public address — get_api_info's base URL — advertised http:// for an https-only host. Honor
// X-Forwarded-Proto, and only that: the client IP and Host are not taken from forwarded headers.
// The framework default trusts the header only from loopback peers, which is exactly a
// same-box proxy; a request arriving from any other address keeps the scheme it connected with.
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto });

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseCors(policy => policy
        .AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader());
}
else
{
    app.UseCors(CorsPolicies.Default);
}

// Serve the embedded SPA from wwwroot — gated on Hosting:ServeSpa so headless
// (hosted-separately) deployments 404 unmatched routes instead of the SPA shell.
var serveSpa = app.Configuration
    .GetValue($"{HostingSettings.SectionName}:{nameof(HostingSettings.ServeSpa)}", true);

if (serveSpa)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

var api = app.MapGroup("/api/v1");

api.MapGet("/version", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-cache, no-store";
    var raw = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";
    var version = raw.Split('+')[0];
    return Results.Ok(new { version });
});

// Current-vs-latest update status. Served from the server-side cache the
// UpdateCheckService refreshes out-of-band — never blocks on a live GitHub call. Kept as a
// separate endpoint from /version so the existing /version contract (consumed by tooling)
// stays unchanged. Unauthenticated, consistent with /version: the running version is already
// public in the gear menu and the releases are a public repo, so "an update is available" is
// non-sensitive. no-store so browsers always reflect fresh server state.
api.MapGet("/version/status", (HttpContext context, VersionStatusCache cache) =>
{
    context.Response.Headers.CacheControl = "no-cache, no-store";
    return Results.Ok(cache.GetStatus());
});

api.MapBoardEndpoints();
api.MapUserEndpoints();
api.MapLaneEndpoints();
api.MapSizeEndpoints();
api.MapCardEndpoints();
api.MapCardHistoryEndpoints();
api.MapLabelEndpoints();
api.MapCommentEndpoints();
api.MapAttachmentEndpoints();
api.MapPruneEndpoints();
api.MapSearchEndpoints();
api.MapWebhookEndpoints();
api.MapWebhookSubscriptionEndpoints();

// Sparse versioning: only the card-detail resource has a v2 shape (paged comment sub-envelope +
// field projection). v1 GET /cards/{id} keeps the v2.0.2 plain-array shape and is deprecated in
// favour of this. No full-surface v2 alias — every other endpoint is v1-only.
var apiV2 = app.MapGroup("/api/v2");
apiV2.MapCardV2Endpoints();

app.MapEventEndpoints();

app.MapMcp("/mcp");

app.MapDefaultEndpoints();

// SPA fallback — serve index.html for any unmatched routes (must be after API/MCP routes).
// Gated on Hosting:ServeSpa: headless deployments skip it so unmatched routes 404.
if (serveSpa)
{
    app.MapFallbackToFile("index.html");
}

// Complete all SSE channels on shutdown so streamed connections close promptly
app.Lifetime.ApplicationStopping.Register(() =>
    app.Services.GetRequiredService<BoardEventBroadcaster>().CompleteAll());

await app.RunAsync();
