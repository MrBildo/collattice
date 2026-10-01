using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

internal static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/users", async (BoardDbContext db, CreateUserRequest request, BoardEventBroadcaster broadcaster) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest("Name is required.");
            }

            if (!Enum.IsDefined(request.Role))
            {
                return Results.BadRequest("Invalid role.");
            }

            var user = new BoardUser
            {
                Id = Guid.NewGuid(),
                Name = request.Name,
                Role = request.Role,
                AuthKey = Ulid.NewUlid().ToString(),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            broadcaster.PublishGlobal("board-updated");
            return Results.Created($"/api/v1/users/{user.Id}", user);
        }).RequireAdmin();

        group.MapGet("/users/{id:guid}", async (BoardDbContext db, Guid id) =>
        {
            var user = await db.Users.FindAsync(id);
            return user is null ? Results.NotFound() : Results.Ok(user);
        }).RequireAdmin();

        group.MapGet("/users", async (BoardDbContext db) =>
            Results.Ok(await db.Users.OrderBy(x => x.Name).ToListAsync()))
            .RequireAdmin();

        group.MapGet("/users/directory", async (BoardDbContext db) =>
        {
            var users = await db.Users
                .Where(x => x.IsActive)
                .OrderBy(x => x.Name)
                    .Select(x => new { x.Id, x.Name })
                        .ToListAsync();
            return Results.Ok(users);
        }).RequireAuth();

        group.MapPatch("/users/{id:guid}", async (BoardDbContext db, Guid id, UpdateUserRequest request, BoardEventBroadcaster broadcaster) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (request.Name is not null)
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest("Name cannot be empty.");
                }

                user.Name = request.Name;
            }

            if (request.Role is not null)
            {
                if (!Enum.IsDefined(request.Role.Value))
                {
                    return Results.BadRequest("Invalid role.");
                }

                user.Role = request.Role.Value;
            }

            await db.SaveChangesAsync();
            broadcaster.PublishGlobal("board-updated");
            return Results.Ok(user);
        }).RequireAdmin();

        group.MapPatch("/users/{id:guid}/deactivate", async (BoardDbContext db, Guid id, BoardEventBroadcaster broadcaster) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null)
            {
                return Results.NotFound();
            }

            user.IsActive = false;
            await db.SaveChangesAsync();
            broadcaster.PublishGlobal("board-updated");
            return Results.NoContent();
        }).RequireAdmin();

        group.MapGet("/auth/me", (HttpContext http) =>
        {
            var user = http.CurrentUser();
            return Results.Ok(new { user.Id, user.Name, user.Role });
        }).RequireAuth();

        return group;
    }
}
