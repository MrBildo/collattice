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
);

// Who caused the event — resolved at the call-site from the authenticated BoardUser,
// never dropped. Role rides the wire as the enum NAME ("AgentUser"), not its integer
// ordinal (2): a deliberate divergence from the numeric REST `role` shape, so a
// consumer can filter the recursion-guard allowlist on a self-describing value. The
// string form is a payload-contract property (set via actor.Role.ToString() at emit),
// not a serializer setting — the webhook serializer stays REST-identical otherwise.
public sealed record BoardEventActor(Guid UserId, string Name, string Role);
