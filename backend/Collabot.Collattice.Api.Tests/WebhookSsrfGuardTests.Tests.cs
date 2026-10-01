using System.Net;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// SSRF guard tests. IsBlockedAddress and the connect-pin are the pure-function carve-out —
// no DbContext, no DB — so they are unit-tested directly. The connect-pin's DNS resolution is
// injected so a DNS-rebind (a host resolving to loopback at delivery) is simulated without real
// DNS. One end-to-end test drives a real SocketsHttpHandler with the connect callback to prove the
// guard throws and surfaces as a Failed delivery attempt.
public sealed class WebhookSsrfGuardTests
{
    // ── IsBlockedAddress — the denylist table (control 2) ────────────────────────

    [Theory]
    [InlineData("127.0.0.1")]            // loopback v4
    [InlineData("127.5.6.7")]            // 127.0.0.0/8
    [InlineData("::1")]                  // loopback v6
    [InlineData("10.0.0.1")]             // RFC1918 10/8
    [InlineData("172.16.0.1")]           // RFC1918 172.16/12 low
    [InlineData("172.31.255.254")]       // RFC1918 172.16/12 high
    [InlineData("192.168.1.1")]          // RFC1918 192.168/16
    [InlineData("169.254.169.254")]      // link-local / cloud metadata
    [InlineData("0.0.0.0")]              // unspecified
    [InlineData("224.0.0.1")]            // multicast
    [InlineData("239.255.255.255")]      // multicast high
    [InlineData("255.255.255.255")]      // broadcast / reserved
    [InlineData("::")]                   // v6 unspecified
    [InlineData("fe80::1")]              // v6 link-local
    [InlineData("fc00::1")]              // v6 unique-local
    [InlineData("fd12:3456::1")]         // v6 unique-local
    [InlineData("::ffff:127.0.0.1")]     // IPv4-mapped loopback (must unwrap)
    [InlineData("::ffff:10.0.0.1")]      // IPv4-mapped RFC1918 (must unwrap)
    public void IsBlockedAddress_BlocksInternalAndSpecialRanges(string ip) =>
        SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip)).ShouldBeTrue($"{ip} should be blocked");

    [Theory]
    [InlineData("8.8.8.8")]              // public DNS
    [InlineData("1.1.1.1")]              // public DNS
    [InlineData("93.184.216.34")]        // public host
    [InlineData("172.15.255.255")]       // just below 172.16/12
    [InlineData("172.32.0.1")]           // just above 172.16/12
    [InlineData("192.167.255.255")]      // just below 192.168/16
    [InlineData("223.255.255.255")]      // just below multicast
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]   // public v6
    public void IsBlockedAddress_AllowsPublicRanges(string ip) =>
        SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip)).ShouldBeFalse($"{ip} should be allowed");

    // ── IsBlockedAddress(allowPrivate: true) — the unconditional carve-out ────────

    [Theory]
    [InlineData("127.0.0.1")]            // loopback v4
    [InlineData("::1")]                  // loopback v6
    [InlineData("169.254.169.254")]      // link-local / cloud metadata
    [InlineData("169.254.1.1")]          // link-local range
    [InlineData("fe80::1")]              // v6 link-local
    [InlineData("0.0.0.0")]              // unspecified
    [InlineData("224.0.0.1")]            // multicast
    [InlineData("::ffff:127.0.0.1")]     // IPv4-mapped loopback (must unwrap)
    public void IsBlockedAddress_WithAllowPrivate_StillBlocksLoopbackAndLinkLocal(string ip) =>
        SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip), allowPrivate: true)
            .ShouldBeTrue($"{ip} must stay blocked even with allowPrivate");

    [Theory]
    [InlineData("10.0.0.1")]             // RFC1918 10/8
    [InlineData("172.16.0.1")]           // RFC1918 172.16/12 low
    [InlineData("172.31.255.254")]       // RFC1918 172.16/12 high
    [InlineData("192.168.1.1")]          // RFC1918 192.168/16
    [InlineData("fc00::1")]              // v6 unique-local
    [InlineData("::ffff:10.0.0.1")]      // IPv4-mapped RFC1918 (must unwrap)
    public void IsBlockedAddress_WithAllowPrivate_PermitsRfc1918(string ip) =>
        SsrfGuard.IsBlockedAddress(IPAddress.Parse(ip), allowPrivate: true)
            .ShouldBeFalse($"{ip} should be permitted with allowPrivate");

    // ── ValidateForRegistration — scheme + resolve-and-deny (controls 1-2) ───────

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com")]
    public async Task ValidateForRegistration_RejectsNonHttpScheme(string url)
    {
        var validate = () => SsrfGuard.ValidateForRegistrationAsync(url, allowPrivate: false, CancellationToken.None);
        await Should.ThrowAsync<WebhookValidationException>(validate);
    }

    [Fact]
    public async Task ValidateForRegistration_RejectsNonAbsoluteUrl()
    {
        var validate = () => SsrfGuard.ValidateForRegistrationAsync("not-a-url", allowPrivate: false, CancellationToken.None);
        await Should.ThrowAsync<WebhookValidationException>(validate);
    }

    [Fact]
    public async Task ValidateForRegistration_RejectsPrivateLiteral_WhenFlagOff()
    {
        var validate = () => SsrfGuard.ValidateForRegistrationAsync("http://127.0.0.1/hook", allowPrivate: false, CancellationToken.None);
        await Should.ThrowAsync<WebhookValidationException>(validate);
    }

    [Theory]
    [InlineData("10.0.0.1")]             // RFC1918 10/8
    [InlineData("172.16.0.1")]           // RFC1918 172.16/12
    [InlineData("192.168.1.1")]          // RFC1918 192.168/16
    public async Task ValidateForRegistration_AcceptsRfc1918_WhenFlagOn(string ip)
    {
        // allowPrivate re-permits the genuinely-private LAN ranges — the operator override for a
        // self-hosted target on a LAN/Tailscale address.
        var validate = () => SsrfGuard.ValidateForRegistrationAsync
        (
            $"http://{ip}/hook",
            allowPrivate: true,
            ResolvesTo(IPAddress.Parse(ip)),
            CancellationToken.None
        );

        await Should.NotThrowAsync(validate);
    }

    [Theory]
    [InlineData("127.0.0.1")]            // loopback — never re-opened by the flag
    [InlineData("169.254.169.254")]      // cloud-metadata endpoint — never re-opened by the flag
    public async Task ValidateForRegistration_StillRejectsLoopbackAndMetadata_WhenFlagOn(string ip)
    {
        // The carve-out: loopback and the link-local/cloud-metadata range stay blocked even with
        // allowPrivate set, so turning the flag on to reach a LAN host cannot expose them.
        var validate = () => SsrfGuard.ValidateForRegistrationAsync
        (
            $"http://{ip}/hook",
            allowPrivate: true,
            ResolvesTo(IPAddress.Parse(ip)),
            CancellationToken.None
        );

        await Should.ThrowAsync<WebhookValidationException>(validate);
    }

    [Fact]
    public async Task ValidateForRegistration_AcceptsPublicLiteral_WhenFlagOff()
    {
        var validate = () => SsrfGuard.ValidateForRegistrationAsync("https://8.8.8.8/hook", allowPrivate: false, CancellationToken.None);
        await Should.NotThrowAsync(validate);
    }

    [Fact]
    public async Task ValidateForRegistration_RejectsHostResolvingToPrivate_WhenFlagOff()
    {
        // A public-looking hostname that resolves to a private IP must be rejected (the resolve-
        // and-deny step), simulated with an injected resolver.
        var validate = () => SsrfGuard.ValidateForRegistrationAsync(
            "https://sneaky.example.com/hook",
            allowPrivate: false,
            ResolvesTo(IPAddress.Loopback),
            CancellationToken.None);

        await Should.ThrowAsync<WebhookValidationException>(validate);
    }

    // ── Connect-pin — the DNS-rebind / TOCTOU defense ────────────

    [Fact]
    public async Task ConnectPin_BlocksHostThatRebindsToLoopback()
    {
        // The rebind case: a host that resolves to loopback at connect time is blocked — the guard
        // throws WebhookSsrfBlockedException rather than dialing internal.
        var connect = () => SsrfGuard.ResolveAndValidateEndpointAsync(
            "rebind.example.com",
            443,
            allowPrivate: false,
            ResolvesTo(IPAddress.Loopback),
            CancellationToken.None).AsTask();

        await Should.ThrowAsync<WebhookSsrfBlockedException>(connect);
    }

    [Fact]
    public async Task ConnectPin_BlocksWhenAnyResolvedAddressIsInternal()
    {
        // Mixed resolution: a public address AND a loopback. ANY blocked address blocks the host —
        // a connect cannot pick the public one and ignore the trap.
        var connect = () => SsrfGuard.ResolveAndValidateEndpointAsync(
            "mixed.example.com",
            443,
            allowPrivate: false,
            ResolvesTo(IPAddress.Parse("93.184.216.34"), IPAddress.Loopback),
            CancellationToken.None).AsTask();

        await Should.ThrowAsync<WebhookSsrfBlockedException>(connect);
    }

    [Fact]
    public async Task ConnectPin_DialsTheValidatedIp_NeverReResolving()
    {
        // The endpoint returned is the IP we validated — connecting to it (not the hostname) is what
        // closes the rebind window.
        var publicIp = IPAddress.Parse("93.184.216.34");

        var endpoint = await SsrfGuard.ResolveAndValidateEndpointAsync(
            "good.example.com",
            8443,
            allowPrivate: false,
            ResolvesTo(publicIp),
            CancellationToken.None);

        endpoint.Address.ShouldBe(publicIp);
        endpoint.Port.ShouldBe(8443);
    }

    [Fact]
    public async Task ConnectPin_PermitsRfc1918_WhenFlagOn()
    {
        // The flag flips the gate for a genuinely-private LAN target: it is permitted at connect
        // (resumes delivery for a migrated private-URL subscription once the operator sets the flag).
        var privateIp = IPAddress.Parse("10.0.0.5");

        var endpoint = await SsrfGuard.ResolveAndValidateEndpointAsync(
            "internal.example.com",
            443,
            allowPrivate: true,
            ResolvesTo(privateIp),
            CancellationToken.None);

        endpoint.Address.ShouldBe(privateIp);
    }

    [Theory]
    [InlineData("127.0.0.1")]            // loopback — never re-opened by the flag
    [InlineData("169.254.169.254")]      // cloud-metadata endpoint — never re-opened by the flag
    public async Task ConnectPin_StillBlocksLoopbackAndMetadata_WhenFlagOn(string ip)
    {
        // The carve-out at connect: loopback and the link-local/cloud-metadata range stay blocked
        // even with allowPrivate set — a rebind to either cannot be reached via the flag.
        var connect = () => SsrfGuard.ResolveAndValidateEndpointAsync
        (
            "internal.example.com",
            443,
            allowPrivate: true,
            ResolvesTo(IPAddress.Parse(ip)),
            CancellationToken.None
        ).AsTask();

        await Should.ThrowAsync<WebhookSsrfBlockedException>(connect);
    }

    // ── End-to-end: a blocked connect surfaces as a Failed delivery attempt ─

    [Fact]
    public async Task BlockedConnect_SurfacesAsFailedDeliveryResult_NotAnUncaughtThrow()
    {
        // A real SocketsHttpHandler carrying the connect guard (allowPrivate:false), dialing a
        // loopback URL: the guard throws, SocketsHttpHandler wraps it in HttpRequestException, and
        // HttpWebhookSender's catch filter records it as a Failed result (no row written here — the
        // dispatcher writes the row; this proves the throw does not escape the sender).
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = SsrfGuard.CreateConnectCallback(allowPrivate: false),
        };
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sender = new HttpWebhookSender(httpClient);

        var boardEvent = SampleEvent();
        var target = new WebhookTarget("http://127.0.0.1:9/hook", Secret: null);

        var result = await sender.SendAsync(boardEvent, target, CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.StatusCode.ShouldBeNull();   // no response — blocked before any socket
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static Func<string, CancellationToken, ValueTask<IPAddress[]>> ResolvesTo(params IPAddress[] addresses) =>
        (_, _) => ValueTask.FromResult(addresses);

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
