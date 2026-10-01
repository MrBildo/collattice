namespace Collabot.Collattice.Api.Events;

// The single server-side source of truth for webhook event DISPLAY metadata: the
// human-readable label, the description, and the family grouping for every selectable event type.
// The machine event-type strings come from WebhookEventTypes (the deliver/select SoT); this class is
// the presentation layer over that set and is exposed verbatim by GET /api/v1/webhooks/event-types,
// so the admin UI's subscription picker consumes ONE catalog instead of keeping its own
// hand-maintained copy. That second copy is exactly what drifted — the backend emitted and accepted
// all 22 events while the picker still offered only the original two. WebhookEventCatalogTests pins this
// catalog's flattened type set to WebhookEventTypes.All, so adding an event to the deliver/select SoT
// without display metadata here fails the build — the catalog and what the backend actually delivers
// can never desync again.
//
// `Label` is the machine token itself (e.g. "card.created"), not a prose name: a webhook integrator
// picks events by their exact type string, so the token IS the right display label for this surface
// (the prose lives in `Description`). The field is carried explicitly anyway — the server owns it, so
// a future friendlier label is a one-line backend edit the UI picks up with no frontend change.
public static class WebhookEventCatalog
{
    public static IReadOnlyList<WebhookEventGroup> Groups { get; } =
    [
        new WebhookEventGroup
        (
            "card",
            "Cards",
            [
                new WebhookEventDescriptor(WebhookEventTypes.CardCreated, "A card is created."),
                new WebhookEventDescriptor(WebhookEventTypes.CardMoved, "A card moves to a different lane or position."),
                new WebhookEventDescriptor(WebhookEventTypes.CardUpdated, "A card's name, description, or size changes."),
                new WebhookEventDescriptor(WebhookEventTypes.CardArchived, "A card is archived."),
                new WebhookEventDescriptor(WebhookEventTypes.CardRestored, "A card is restored from the archive."),
                new WebhookEventDescriptor(WebhookEventTypes.CardDeleted, "A card is deleted."),
                new WebhookEventDescriptor(WebhookEventTypes.CardLabeled, "A label is added to a card."),
                new WebhookEventDescriptor(WebhookEventTypes.CardUnlabeled, "A label is removed from a card."),
            ]
        ),
        new WebhookEventGroup
        (
            "comment",
            "Comments",
            [
                new WebhookEventDescriptor(WebhookEventTypes.CommentCreated, "A comment is added to a card."),
                new WebhookEventDescriptor(WebhookEventTypes.CommentUpdated, "A comment is edited."),
                new WebhookEventDescriptor(WebhookEventTypes.CommentDeleted, "A comment is deleted."),
            ]
        ),
        new WebhookEventGroup
        (
            "label",
            "Labels",
            [
                new WebhookEventDescriptor(WebhookEventTypes.LabelCreated, "A label is created on a board."),
                new WebhookEventDescriptor(WebhookEventTypes.LabelUpdated, "A label is renamed or recolored."),
                new WebhookEventDescriptor(WebhookEventTypes.LabelDeleted, "A label is deleted from a board."),
            ]
        ),
        new WebhookEventGroup
        (
            "attachment",
            "Attachments",
            [
                new WebhookEventDescriptor(WebhookEventTypes.AttachmentCreated, "An attachment is added to a card."),
                new WebhookEventDescriptor(WebhookEventTypes.AttachmentDeleted, "An attachment is removed from a card."),
            ]
        ),
        new WebhookEventGroup
        (
            "lane",
            "Lanes",
            [
                new WebhookEventDescriptor(WebhookEventTypes.LaneCreated, "A lane is created on a board."),
                new WebhookEventDescriptor(WebhookEventTypes.LaneRenamed, "A lane is renamed."),
                new WebhookEventDescriptor(WebhookEventTypes.LaneReordered, "A board's lanes are reordered."),
                new WebhookEventDescriptor(WebhookEventTypes.LaneDeleted, "A lane is deleted from a board."),
            ]
        ),
        new WebhookEventGroup
        (
            "size",
            "Sizes",
            [
                new WebhookEventDescriptor(WebhookEventTypes.SizeCreated, "A card size is created on a board."),
                new WebhookEventDescriptor(WebhookEventTypes.SizeRenamed, "A card size is renamed."),
                new WebhookEventDescriptor(WebhookEventTypes.SizeReordered, "A board's card sizes are reordered."),
                new WebhookEventDescriptor(WebhookEventTypes.SizeDeleted, "A card size is deleted from a board."),
            ]
        ),
        new WebhookEventGroup
        (
            "board",
            "Boards",
            [
                new WebhookEventDescriptor(WebhookEventTypes.BoardCreated, "A board is created."),
                new WebhookEventDescriptor(WebhookEventTypes.BoardRenamed, "A board is renamed."),
                new WebhookEventDescriptor(WebhookEventTypes.BoardDeleted, "A board is deleted."),
            ]
        ),
    ];

    // The flattened set of every type the catalog presents — the in-backend drift guard compares this
    // to WebhookEventTypes.All. Ordinal because event-type identifiers are exact ASCII tokens.
    public static IReadOnlySet<string> Types { get; } = Groups
        .SelectMany(group => group.Events)
            .Select(descriptor => descriptor.Type)
                .ToHashSet(StringComparer.Ordinal);
}

// One family of related event types, with its display label (the section heading the picker renders)
// and the events it contains. `Family` is the stable machine key (e.g. "card"); `Label` is the
// human-readable heading (e.g. "Cards").
public sealed record WebhookEventGroup
(
    string Family,
    string Label,
    IReadOnlyList<WebhookEventDescriptor> Events
);

// One selectable event type plus its display metadata. `Type` is the machine string a subscription
// selects (from WebhookEventTypes); `Label` is the display token; `Description` is the prose shown
// beneath it.
public sealed record WebhookEventDescriptor
(
    string Type,
    string Label,
    string Description
)
{
    public WebhookEventDescriptor(string type, string description)
        : this(type, type, description)
    {
    }
}
