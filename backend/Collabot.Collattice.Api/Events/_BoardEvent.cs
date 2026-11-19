using System.Text.Json.Serialization;

namespace Collabot.Collattice.Api.Events;

// The structured internal event a converted mutation call-site raises. One enriched
// event fans out to two projections: the SSE bus downsamples it to the thin
// `board-updated` bell (browser unchanged — the safety property), the webhook sink
// consumes it whole.
//
// Everything the wire envelope needs is resolved AT EMIT-TIME and carried here
// (fidelity-required): a domain event reports state as it was at occurrence, so the
// dispatcher must not re-hydrate names/slug at drain time (a rename between emit and
// drain would make the event lie). Carrying the fully-resolved envelope also keeps the
// dispatcher DB-free — a self-contained queued POJO — which is what lets the in-memory
// sink → durable-outbox swap stay clean.
//
// The [JsonPropertyName] attributes ARE the wire contract — the dispatcher
// just serializes this record with the project's camelCase options and POSTs it, so the
// envelope field names live here, not in delivery code. The dotted past-tense `event`
// name is the GitHub/Stripe idiom an integrator recognizes cold.
public sealed record BoardEvent
(
    [property: JsonPropertyName("event")] string EventType,
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("boardId")] Guid BoardId,
    [property: JsonPropertyName("boardSlug")] string BoardSlug,
    [property: JsonPropertyName("actor")] BoardEventActor Actor,
    [property: JsonPropertyName("data")] object Data
)
{
    // True when the event is about a card that is still an unsaved draft. It still rings the board
    // bell, but it is never a webhook event: a subscriber hears of a card first through card.created
    // when the draft is saved, so an event about the draft before then would reference a card it has
    // never been told exists (and may never be, if the draft is cancelled). The webhook queue refuses
    // these; on save, card.created is followed by an attachment.created / comment.created for each
    // attachment and comment the draft still has. Not part of the wire envelope.
    [JsonIgnore]
    public bool AboutDraft { get; init; }
}

// Who caused the event — resolved at the call-site from the authenticated BoardUser,
// never dropped. Role rides the wire as the enum NAME ("AgentUser"), not its integer
// ordinal (2): a deliberate divergence from the numeric REST `role` shape, so a
// consumer can filter the recursion-guard allowlist on a self-describing value. The
// string form is a payload-contract property (set via actor.Role.ToString() at emit),
// not a serializer setting — the webhook serializer stays REST-identical otherwise.
public sealed record BoardEventActor(Guid UserId, string Name, string Role);
