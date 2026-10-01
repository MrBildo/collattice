using System.Text.Json;
using System.Text.Json.Serialization;

namespace Collabot.Collattice.Api.Models;

// The response for a card update — the REST PATCH /cards/{id} handler and the MCP update_card tool —
// and the only place a collision notice is attached. Both write sites build this directly. The shared
// CardSummaryBuilder that feeds card lists, cross-board search and webhook payloads never produces it,
// so a collision is structurally incapable of appearing on any of those surfaces: it lives on a type
// that only the two write responses ever construct.
//
// On the wire this is the enriched card's own fields at the top level with an optional "collision"
// beside them — additive, so a consumer already reading the update response keeps working unchanged
// and simply gains one field. That flattening is why the type carries a converter instead of nesting
// the card under a property: nesting would relocate every existing field and break every existing
// reader, which is the breaking shape (B) that was rejected in favour of this one.
[JsonConverter(typeof(CardUpdateResultConverter))]
public sealed record CardUpdateResult(CardSummary Card, CardCollision? Collision);

// A card write landed on top of another user's edit. Awareness only — the save is never blocked and
// last-write-wins is unchanged; this reports what happened, it does not prevent it.
//
// Kind records how the overlap was established. "exact" means the caller passed the revision it had
// read and the field has since moved past it — a definite overwrite, whatever the wall-clock gap.
// "approximate" means the caller passed no baseline and the card was edited by someone else within a
// short window before this write — a best-effort "someone was working this card at the same time as
// you". Field is the field an exact overwrite was measured on ("description" today) and is null for
// the approximate signal, which is a card-level observation that cannot honestly name a field. Actor
// is who the caller's write landed on top of.
public sealed record CardCollision(string Kind, string? Field, CardCollisionActor Actor);

public sealed record CardCollisionActor(Guid UserId, string Name);

// Writes the card's own fields inlined at the top level, then "collision" when present. The card is
// serialized with the same options so its naming policy and its LaneId-when-default omission are
// preserved, then its members are lifted one level up — which is what keeps the shape additive rather
// than a new envelope.
internal sealed class CardUpdateResultConverter : JsonConverter<CardUpdateResult>
{
    public override void Write(Utf8JsonWriter writer, CardUpdateResult value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        var card = JsonSerializer.SerializeToElement(value.Card, options);
        foreach (var property in card.EnumerateObject())
        {
            property.WriteTo(writer);
        }

        if (value.Collision is not null)
        {
            var name = options.PropertyNamingPolicy?.ConvertName(nameof(CardUpdateResult.Collision))
                ?? nameof(CardUpdateResult.Collision);
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, value.Collision, options);
        }

        writer.WriteEndObject();
    }

    // Response-only: the server writes this shape but never reads it back.
    public override CardUpdateResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("CardUpdateResult is a response type and is not deserialized.");
}
