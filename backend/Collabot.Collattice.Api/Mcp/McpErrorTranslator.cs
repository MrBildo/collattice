using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Mcp;

// Translates exceptions thrown by MCP tool methods (or by the SDK's
// AIFunction parameter-binding layer above them) into McpException so the
// SDK renders the underlying message back to the caller.
//
// Default SDK behaviour: any non-McpException becomes the body-less
// "An error occurred invoking '<tool>'." — useful for the server log, useless
// for the bot trying to fix a typo.
// McpException messages flow through as
// "An error occurred invoking '<tool>': <Message>".
//
// Security posture: only known operator-input shapes surface their messages.
// Stack traces never leak. Server-internal failures (DB, downstream timeouts,
// EF-translation crashes) keep the body-less wrapper response, with one
// exception: a write that lost to a concurrent delete is answered with a fixed
// message (see ConcurrentDeleteConflict), never the exception's own text.
internal static class McpErrorTranslator
{
    // Surface message for input-validation / serialization shapes. Anything
    // that originates server-internal (DbUpdateException, SqliteException,
    // HttpRequestException, …) deliberately falls through so we don't leak
    // infrastructure detail in error text.
    public static bool ShouldSurface(Exception ex) => ex switch
    {
        ArgumentException => true,            // also covers ArgumentNullException
        JsonException => true,                // bad JSON in tool args
        FormatException => true,              // malformed Guid / DateTime parse
        OverflowException => true,            // numeric range violation in args
        NotSupportedException => true,        // AIFunction binder uses this for unsupported types
        _ => false,
    };

    public static string Format(Exception ex)
    {
        // Type name + Message is operator-actionable; matches the shape the
        // server log already carries. Stack trace is deliberately omitted.
        var typeName = ex.GetType().Name;
        var message = ex.Message ?? string.Empty;
        return string.IsNullOrWhiteSpace(message) ? typeName : $"{typeName} — {message}";
    }

    public static McpException ToMcpException(Exception ex) => new(Format(ex), ex);

    // CallToolFilter the MCP server pipeline wires. Catches allowlisted
    // exceptions from the inner handler and rethrows as McpException so the
    // SDK wrapper renders "An error occurred invoking '<tool>': <Type — Message>".
    // Anything outside the allowlist propagates untouched, preserving the
    // SDK's body-less wrapper for server-internal failures. Exposed as a
    // static method (not an inline lambda in Program.cs) so tests can drive
    // the exact same filter without standing up the DI pipeline.
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> WrapForCallTool
    (
        McpRequestHandler<CallToolRequestParams, CallToolResult> next
    ) =>
        async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldSurface(ex))
            {
                throw ToMcpException(ex);
            }
            catch (Exception ex) when (ConcurrentDeleteConflict.Matches(ex))
            {
                // A write that lost to a concurrent delete answers the way a tool answers anything
                // it refuses, with an "Error: " string: the MCP form of the 409 REST gives. The
                // message is fixed and names no table or constraint, so nothing internal leaks.
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Error: {ConcurrentDeleteConflict.Message}" }],
                };
            }
        };
}

