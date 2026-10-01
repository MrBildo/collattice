using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// End-to-end wiring proof for the CallToolFilter that translates
// allowlisted exceptions into McpException so the SDK wrapper surfaces the
// underlying detail instead of the body-less "An error occurred invoking
// '<tool>'.". The McpErrorTranslator allowlist matrix is owned by
// McpErrorTranslatorTests; this file proves the filter is registered against
// the real DI pipeline AND that the filter delegate produces the expected
// translation when wrapped around a throwing inner handler.
public class McpServerErrorWrapperTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;

    [Fact]
    public void Program_RegistersCallToolFilter()
    {
        // Guard: future cleanup must not silently drop the registration on the
        // configured McpServerOptions. The exact filter count is intentionally
        // not asserted — the SDK or the ASP.NET integration may add its own
        // filters in future versions. We only assert OUR filter is present.
        var options = _factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;

        options.Filters.Request.CallToolFilters.ShouldContain(McpErrorTranslator.WrapForCallTool);
    }

    [Fact]
    public async Task WrapForCallTool_AllowlistedException_Rethrows_AsMcpException()
    {
        // Arrange — wrap an inner handler that throws the kind of
        // ArgumentException the SDK's AIFunction binder produces on missing
        // required parameters (as happens when an integrator mistypes a parameter name).
        var pipeline = McpErrorTranslator.WrapForCallTool(static (_, _) =>
            throw new ArgumentException
            (
                "The arguments dictionary is missing a value for the required parameter 'content'.",
                "arguments"
            ));

        // Act — context is unused by the filter; pass null to keep the test
        // independent of RequestContext's non-trivial ctor.
        var ex = await Should.ThrowAsync<McpException>(
            async () => await pipeline(null!, CancellationToken.None).ConfigureAwait(false));

        // Assert — the SDK's outer catch will render this Message as
        // "An error occurred invoking '<tool>': <msg>".
        ex.Message.ShouldContain("ArgumentException");
        ex.Message.ShouldContain("missing a value for the required parameter");
        ex.Message.ShouldContain("'content'");
        ex.InnerException.ShouldBeOfType<ArgumentException>();
    }

    [Fact]
    public async Task WrapForCallTool_ServerInternalException_PassesThrough()
    {
        // Arrange — a non-allowlisted exception (DB / EF / downstream) must
        // NOT be re-thrown as McpException; the SDK then renders its body-less
        // wrapper response. This preserves the "don't leak infrastructure
        // detail" half of the card's security posture.
        var inner = new InvalidOperationException("ef translation imploded");
        var pipeline = McpErrorTranslator.WrapForCallTool((_, _) => throw inner);

        // Act
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            async () => await pipeline(null!, CancellationToken.None).ConfigureAwait(false));

        // Assert — exact same exception instance bubbled through.
        ex.ShouldBeSameAs(inner);
    }

    [Fact]
    public async Task WrapForCallTool_WriteLostToConcurrentDelete_AnswersWithAToolErrorString()
    {
        // Arrange — the failure a write gets when a row it refers to was deleted under it
        var pipeline = McpErrorTranslator.WrapForCallTool(static (_, _) =>
            throw new DbUpdateException("save failed", new SqliteException("FOREIGN KEY constraint failed", 19, 787)));

        // Act
        var result = await pipeline(null!, CancellationToken.None);

        // Assert — the same shape a tool's own refusal takes, naming nothing internal
        var text = result.Content.OfType<TextContentBlock>().Single().Text;

        text.ShouldBe("Error: Something this change refers to was changed or deleted at the same moment. Reload and try again.");
    }

    [Fact]
    public async Task WrapForCallTool_UniqueCollision_PassesThrough()
    {
        // Arrange — same primary SQLite code (19), a different failure: not a concurrent delete
        var inner = new DbUpdateException("save failed", new SqliteException("UNIQUE constraint failed", 19, 2067));
        var pipeline = McpErrorTranslator.WrapForCallTool((_, _) => throw inner);

        // Act
        var ex = await Should.ThrowAsync<DbUpdateException>(
            async () => await pipeline(null!, CancellationToken.None).ConfigureAwait(false));

        // Assert
        ex.ShouldBeSameAs(inner);
    }

    [Fact]
    public async Task WrapForCallTool_McpException_PassesThrough()
    {
        // Arrange — an McpException thrown by the tool body itself is already
        // operator-actionable. The filter must not double-wrap it (which would
        // produce a nonsensical "McpException — McpException: msg" surface);
        // the SDK's outer catch already handles propagation correctly.
        var inner = new McpException("user-facing message from the tool itself");
        var pipeline = McpErrorTranslator.WrapForCallTool((_, _) => throw inner);

        // Act
        var ex = await Should.ThrowAsync<McpException>(
            async () => await pipeline(null!, CancellationToken.None).ConfigureAwait(false));

        // Assert — the original instance, untouched. McpException is not in
        // the allowlist (ShouldSurface returns false for it), so it falls
        // through the catch filter and propagates.
        ex.ShouldBeSameAs(inner);
    }
}
