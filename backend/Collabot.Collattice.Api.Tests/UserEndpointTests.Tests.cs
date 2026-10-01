using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class UserEndpointTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private static JsonSerializerOptions JsonOptions => TestAuthHelper.JsonOptions;

    [Fact]
    public async Task PostUser_AsAdmin_Returns201WithUserDetails()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var payload = new { name = "New Human", role = (int)UserRole.HumanUser };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/users", payload);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        json.TryGetProperty("id", out var idProp).ShouldBeTrue();
        idProp.GetGuid().ShouldNotBe(Guid.Empty);
        json.TryGetProperty("authKey", out var authKeyProp).ShouldBeTrue();
        authKeyProp.GetString().ShouldNotBeNullOrWhiteSpace();
        json.GetProperty("name").GetString().ShouldBe("New Human");
        json.GetProperty("role").GetInt32().ShouldBe((int)UserRole.HumanUser);
    }

    [Fact]
    public async Task PostUser_AsHumanUser_Returns403()
    {
        // Arrange
        var human = await TestAuthHelper.CreateUserAsync(_client, _factory, "Human Forbidden Post", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, human.AuthKey);
        var payload = new { name = "Should Not Create", role = (int)UserRole.HumanUser };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/users", payload);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PostUser_AsAgentUser_Returns403()
    {
        // Arrange
        var agent = await TestAuthHelper.CreateUserAsync(_client, _factory, "Agent Forbidden Post", UserRole.AgentUser);
        TestAuthHelper.SetAuth(_client, agent.AuthKey);
        var payload = new { name = "Should Not Create", role = (int)UserRole.HumanUser };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/users", payload);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetUsers_AsAdmin_ReturnsUsersList()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync("/api/v1/users");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var users = await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        users.ShouldNotBeNull();
        users.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetUsers_AsHumanUser_Returns403()
    {
        // Arrange
        var human = await TestAuthHelper.CreateUserAsync(_client, _factory, "Human Forbidden Get", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, human.AuthKey);

        // Act
        var response = await _client.GetAsync("/api/v1/users");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Request_MissingUserKey_Returns401()
    {
        // Arrange
        _client.DefaultRequestHeaders.Remove("X-User-Key");

        // Act
        var response = await _client.GetAsync("/api/v1/users");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Request_InvalidUserKey_Returns401()
    {
        // Arrange
        TestAuthHelper.SetAuth(_client, "nonexistent-user-key");

        // Act
        var response = await _client.GetAsync("/api/v1/users");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetUserById_AsAdmin_Returns200()
    {
        // Arrange
        var user = await TestAuthHelper.CreateUserAsync(_client, _factory, "GetById Target", UserRole.HumanUser);
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync($"/api/v1/users/{user.Id}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        json.GetProperty("id").GetGuid().ShouldBe(user.Id);
        json.GetProperty("name").GetString().ShouldBe("GetById Target");
    }

    [Fact]
    public async Task GetUserById_NonexistentUser_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var bogusId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/v1/users/{bogusId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchUser_UpdatesName_Returns200()
    {
        // Arrange
        var user = await TestAuthHelper.CreateUserAsync(_client, _factory, "PatchNameBefore", UserRole.HumanUser);
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/users/{user.Id}", new { name = "PatchNameAfter" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        json.GetProperty("name").GetString().ShouldBe("PatchNameAfter");
    }

    [Fact]
    public async Task PatchUser_UpdatesRole_Returns200()
    {
        // Arrange
        var user = await TestAuthHelper.CreateUserAsync(_client, _factory, "PatchRoleTarget", UserRole.HumanUser);
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/users/{user.Id}", new { role = (int)UserRole.AgentUser });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        json.GetProperty("role").GetInt32().ShouldBe((int)UserRole.AgentUser);
    }

    [Fact]
    public async Task PatchUser_NonexistentUser_Returns404()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);
        var bogusId = Guid.NewGuid();

        // Act
        var response = await _client.PatchAsJsonAsync($"/api/v1/users/{bogusId}", new { name = "Ghost" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeactivateUser_AsAdmin_Returns204()
    {
        // Arrange
        var user = await TestAuthHelper.CreateUserAsync(_client, _factory, "ToDeactivate", UserRole.HumanUser);
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.PatchAsync($"/api/v1/users/{user.Id}/deactivate", null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeactivateUser_AsNonAdmin_Returns403()
    {
        // Arrange
        var human = await TestAuthHelper.CreateUserAsync(_client, _factory, "NonAdminDeactivator", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, human.AuthKey);

        // Act
        var response = await _client.PatchAsync($"/api/v1/users/{human.Id}/deactivate", null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeactivateUser_DeactivatedUser_CannotAuthenticate()
    {
        // Arrange
        var user = await TestAuthHelper.CreateUserAsync(_client, _factory, "WillBeDeactivated", UserRole.HumanUser);
        TestAuthHelper.SetAdminAuth(_client, _factory);
        await _client.PatchAsync($"/api/v1/users/{user.Id}/deactivate", null);

        // Act — try to use the deactivated user's auth key
        TestAuthHelper.SetAuth(_client, user.AuthKey);
        var response = await _client.GetAsync($"/api/v1/boards/{_factory.DefaultBoardId}/board");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAuthMe_AsAdmin_ReturnsCurrentUser()
    {
        // Arrange
        TestAuthHelper.SetAdminAuth(_client, _factory);

        // Act
        var response = await _client.GetAsync("/api/v1/auth/me");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        json.GetProperty("id").GetGuid().ShouldNotBe(Guid.Empty);
        json.GetProperty("name").GetString().ShouldNotBeNullOrWhiteSpace();
        json.GetProperty("role").GetInt32().ShouldBe((int)UserRole.Administrator);
    }

    [Fact]
    public async Task GetAuthMe_AsHumanUser_ReturnsOwnDetails()
    {
        // Arrange
        var human = await TestAuthHelper.CreateUserAsync(_client, _factory, "Me Human", UserRole.HumanUser);
        TestAuthHelper.SetAuth(_client, human.AuthKey);

        // Act
        var response = await _client.GetAsync("/api/v1/auth/me");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        json.GetProperty("id").GetGuid().ShouldBe(human.Id);
        json.GetProperty("name").GetString().ShouldBe("Me Human");
        json.GetProperty("role").GetInt32().ShouldBe((int)UserRole.HumanUser);
    }

    [Fact]
    public async Task GetAuthMe_Unauthenticated_Returns401()
    {
        // Arrange
        _client.DefaultRequestHeaders.Remove("X-User-Key");

        // Act
        var response = await _client.GetAsync("/api/v1/auth/me");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
