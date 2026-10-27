namespace Collabot.Collattice.Api.Auth;

public class RequireRoleFilter(params UserRole[] allowedRoles) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var resolver = httpContext.RequestServices.GetRequiredService<IUserResolver>();

        var userKey = httpContext.Request.Headers[AuthExtensions.UserKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(userKey))
        {
            return Results.Unauthorized();
        }

        var user = await resolver.ResolveAsync(userKey, httpContext.RequestAborted);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        httpContext.Items[nameof(BoardUser)] = user;

        return allowedRoles.Length > 0 && !allowedRoles.Contains(user.Role)
            ? Results.StatusCode(StatusCodes.Status403Forbidden)
            : await next(context);
    }
}
