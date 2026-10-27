namespace Collabot.Collattice.Api.Auth;

// Single seam for the AuthKey == key && IsActive lookup — REST (RequireRoleFilter)
// and MCP (McpAuthService) share one implementation so a predicate change (soft-delete,
// tenancy scope, etc.) is made once and both surfaces pick it up.
public interface IUserResolver
{
    // Returns the active BoardUser for authKey, or null if the key is unknown or inactive.
    Task<BoardUser?> ResolveAsync(string authKey, CancellationToken ct = default);
}
