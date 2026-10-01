using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Auth;

public class UserResolver(BoardDbContext db) : IUserResolver
{
    public Task<BoardUser?> ResolveAsync(string authKey, CancellationToken ct = default)
        => db.Users.SingleOrDefaultAsync(x => x.AuthKey == authKey && x.IsActive, ct);
}
