namespace Collabot.Collattice.Api.Auth;

public static class AuthExtensions
{
    public const string UserKeyHeader = "X-User-Key";

    public static BoardUser CurrentUser(this HttpContext context)
        => context.Items[nameof(BoardUser)] as BoardUser
           ?? throw new InvalidOperationException("User not resolved. Ensure RequireRoleFilter is applied.");

    public static RouteHandlerBuilder RequireRole(this RouteHandlerBuilder builder, params UserRole[] roles)
        => builder.AddEndpointFilter(new RequireRoleFilter(roles));

    public static RouteHandlerBuilder RequireAuth(this RouteHandlerBuilder builder)
        => builder.RequireRole(UserRole.Administrator, UserRole.AgentAdministrator, UserRole.HumanUser, UserRole.AgentUser);

    public static RouteHandlerBuilder RequireAdmin(this RouteHandlerBuilder builder)
        => builder.RequireRole(UserRole.Administrator);

    public static RouteHandlerBuilder RequireAdminOrAgentAdmin(this RouteHandlerBuilder builder)
        => builder.RequireRole(UserRole.Administrator, UserRole.AgentAdministrator);
}
