using System.Security.Cryptography;
using System.Text;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Unit tests for the HMAC signer. WebhookSigner is a pure function (no DbContext, no
// DB) — the project's pure-function carve-out lets it be tested directly. These prove the wire
// contract: the sha256= prefix, lowercase hex, and that a consumer recomputing HMAC-SHA256 over
// the SAME bytes with the SAME secret matches byte-for-byte.
public sealed class WebhookSignerTests
{
    [Fact]
    public void Sign_ProducesSha256PrefixedLowercaseHexDigest()
    {
        var body = "{\"event\":\"card.created\"}"u8.ToArray();

        var signature = WebhookSigner.Sign(body, "test-secret");

        signature.ShouldStartWith("sha256=");
        var hex = signature["sha256=".Length..];
        hex.ShouldBe(hex.ToLowerInvariant());          // lowercase
        hex.Length.ShouldBe(64);                       // 32-byte SHA-256 => 64 hex chars
    }

    [Fact]
    public void Sign_MatchesIndependentConsumerRecomputation()
    {
        var body = "the exact bytes we sent"u8.ToArray();
        const string secret = "shared-secret";

        var signature = WebhookSigner.Sign(body, secret);

        // A consumer recomputes HMAC-SHA256 over the received raw body with the shared secret.
        var expected = "sha256=" + Convert.ToHexStringLower
        (
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)
        );

        signature.ShouldBe(expected);
    }

    [Fact]
    public void Sign_DiffersByBody_AndBySecret()
    {
        var bodyA = "body-a"u8.ToArray();
        var bodyB = "body-b"u8.ToArray();

        WebhookSigner.Sign(bodyA, "k").ShouldNotBe(WebhookSigner.Sign(bodyB, "k"));
        WebhookSigner.Sign(bodyA, "k1").ShouldNotBe(WebhookSigner.Sign(bodyA, "k2"));
    }
}
