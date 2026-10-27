using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Mcp;

[McpServerToolType]
public class SystemTools(McpAuthService auth, IHttpContextAccessor httpContextAccessor)
{
    [McpServerTool(Name = "get_api_info", ReadOnly = true, Destructive = false)]
    [Description("Returns the API's base URL and version. Use this to discover the API address for direct REST calls (e.g. downloading large attachments).")]
    public async Task<string> GetApiInfoAsync
    (
        [Description("Your auth key")] string authKey,
        CancellationToken ct = default
    )
    {
        var (_, error) = await auth.RequireUserAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var request = httpContextAccessor.HttpContext?.Request;
        var baseUrl = request is not null
            ? $"{request.Scheme}://{request.Host}"
            : "unknown";

        return JsonSerializer.Serialize(new
        {
            BaseUrl = baseUrl,
            ApiPrefix = "/api/v1",
        }, JsonSerializerOptions.Web);
    }
}
