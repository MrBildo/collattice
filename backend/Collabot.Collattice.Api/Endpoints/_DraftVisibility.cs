using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api.Endpoints;

// A draft is a card started in the create dialog and not yet saved. Until it is saved it belongs to
// the person creating it: anyone else, an administrator included, is answered exactly as if the card
// did not exist, by every REST route and MCP tool that names it, or a comment or attachment on it, by id.
// A draft is not a card yet, so there is nothing for anyone else to read or change, and answering
// "not found" rather than "forbidden" keeps one person's unsaved work from being discoverable at all.
internal static class DraftVisibility
{
    public static bool IsHiddenFrom(CardItem card, Guid userId) => card.IsTemp && card.CreatedByUserId != userId;

    public static async Task<bool> IsCardHiddenFromAsync(BoardDbContext db, Guid cardId, Guid userId, CancellationToken ct)
    {
        var card = await db.Cards.FindAsync([cardId], ct);
        return card is not null && IsHiddenFrom(card, userId);
    }

    // Applied after the auth filter, so the caller is known. The card is loaded into the request's own
    // context, which the handler shares, so the handler's own lookup of it costs nothing more.
    public static RouteHandlerBuilder HidesOthersDrafts(this RouteHandlerBuilder builder, RouteIdentifies identifies = RouteIdentifies.Card)
        => builder.AddEndpointFilter(new HideOthersDraftsFilter(identifies));
}

// What the {id} in a route names, so the filter can find the card it belongs to.
internal enum RouteIdentifies
{
    Card,
    Comment,
    Attachment,
}

internal class HideOthersDraftsFilter(RouteIdentifies identifies) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!Guid.TryParse(http.GetRouteValue("id")?.ToString(), out var id))
        {
            return await next(context);
        }

        var db = http.RequestServices.GetRequiredService<BoardDbContext>();
        var ct = http.RequestAborted;

        var cardId = identifies switch
        {
            RouteIdentifies.Comment => await db.Comments
                .Where(comment => comment.Id == id)
                    .Select(comment => (Guid?)comment.CardId)
                        .SingleOrDefaultAsync(ct),
            RouteIdentifies.Attachment => await db.Attachments
                .Where(attachment => attachment.Id == id)
                    .Select(attachment => (Guid?)attachment.CardId)
                        .SingleOrDefaultAsync(ct),
            _ => id,
        };

        return cardId is { } value && await DraftVisibility.IsCardHiddenFromAsync(db, value, http.CurrentUser().Id, ct)
            ? Results.NotFound()
            : await next(context);
    }
}
