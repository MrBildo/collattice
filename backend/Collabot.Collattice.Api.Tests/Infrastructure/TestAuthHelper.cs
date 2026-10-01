using System.Net.Http.Json;
using System.Text.Json;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

public static class TestAuthHelper
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void SetAuth(HttpClient client, string userKey)
    {
        client.DefaultRequestHeaders.Remove("X-User-Key");
        client.DefaultRequestHeaders.Add("X-User-Key", userKey);
    }

    public static void SetAdminAuth(HttpClient client, CollatticeApiFactory factory) =>
        SetAuth(client, factory.AdminAuthKey);

    public static async Task<BoardUser> CreateUserAsync
    (
        HttpClient client,
        CollatticeApiFactory factory,
        string name,
        UserRole role
    )
    {
        SetAdminAuth(client, factory);
        var response = await client.PostAsJsonAsync("/api/v1/users", new { name, role });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BoardUser>())!;
    }

    public static async Task<Guid> GetDefaultBoardIdAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/boards");
        response.EnsureSuccessStatusCode();
        var boards = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        return boards![0].GetProperty("id").GetGuid();
    }
}
