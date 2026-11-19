using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// The request context the MCP server hands a CallToolFilter, for tests that drive the filter
// directly. The context cannot be built without a server, so one is made over a transport that
// reads and writes nothing; it is never run. Services is what the SDK sets to the call's request
// services, and is left null when none are given.
// sealed: a leaf test helper; no subtype hierarchy is intended.
public sealed class McpCallToolContext : IAsyncDisposable
{
    private readonly McpServer _server;

    public McpCallToolContext(IServiceProvider? services = null)
    {
        _server = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), new McpServerOptions());

        Context = new RequestContext<CallToolRequestParams>
        (
            _server,
            new JsonRpcRequest { Method = RequestMethods.ToolsCall },
            new CallToolRequestParams { Name = "test_tool" }
        )
        {
            Services = services,
        };
    }

    public RequestContext<CallToolRequestParams> Context { get; }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
