namespace Collabot.Collattice.Api.Events;

// The per-event `data` payloads. Both embed the existing CardSummary DIRECTLY
// (no parallel webhook-only card DTO to drift; the `version` envelope field is the
// release valve if CardSummary's shape ever changes). CardSummary is GUID-keyed and
// carries no board/lane names, so the payload enriches it with the resolved laneName.
//
// Both events nest the card under a `card` key (rather than flattening card fields to
// the data root) so the embed-CardSummary-directly rule holds without forking and the
// two event shapes stay structurally consistent — card.moved must nest (it carries
// from/to alongside the card), and card.created follows the same shape. The card is
// "state at occurrence", not "current state": a card.created therefore carries
// commentCount 0 / attachmentCount 0 / latestComment null (freshly created), which is
// correct, not a bug.

public sealed record WebhookCardCreatedData(CardSummary Card, string LaneName);

public sealed record WebhookCardMovedData
(
    CardSummary Card,
    string LaneName,
    WebhookLaneRef From,
    WebhookLaneRef To
);

// The from/to transition on card.moved. A cross-lane move changes the lane (an automation wired to
// "card entered Ready → assign"); a within-lane move changes only the position and carries equal
// from/to lane ids — the position is the axis that moved. `from` is captured BEFORE the move mutates
// the card.
public sealed record WebhookLaneRef(Guid LaneId, string LaneName, int Position);

// The card-family payloads. Each embeds the fat CardSummary directly (no
// parallel webhook-only card DTO to drift) plus the resolved laneName, mirroring the v1
// card.created shape. card.updated fires on a content change to name, description or size.
// card.archived and card.restored carry state at occurrence — the card sits in the archive
// lane, or in its restored target lane, respectively.
public sealed record WebhookCardUpdatedData(CardSummary Card, string LaneName);

public sealed record WebhookCardArchivedData(CardSummary Card, string LaneName);

public sealed record WebhookCardRestoredData(CardSummary Card, string LaneName);

// card.deleted carries the card's state at occurrence — built BEFORE the row is removed, so the
// fat summary is fully enriched (labels, counts) rather than blank. A card can be hard-deleted
// while archived, so when the deleted card was archived its lane is the hidden archive lane and its
// GUID is blanked exactly as card.archived does; a normal delete keeps its real laneId.
public sealed record WebhookCardDeletedData(CardSummary Card, string LaneName);

// card.labeled / card.unlabeled embed the label resource the card's label-set changed by,
// so a consumer knows WHICH label without a second fetch. One event per label add/remove.
public sealed record WebhookCardLabeledData(CardSummary Card, string LaneName, WebhookLabelRef Label);

public sealed record WebhookCardUnlabeledData(CardSummary Card, string LaneName, WebhookLabelRef Label);

// The label resource embedded in card.labeled / card.unlabeled. Color is nullable on the
// Label entity, so it rides the wire as nullable too.
public sealed record WebhookLabelRef(Guid Id, string Name, string? Color);

// The minimal card reference embedded in comment.* and attachment.* events — the affected card's
// id and board-scoped number. The comment/attachment IS the changed resource; the card is context,
// so it rides as a thin ref, not the fat CardSummary the card.* events carry.
public sealed record WebhookCardRef(Guid Id, long Number);

// The comment-family payloads. Each embeds the comment resource plus a minimal card ref.
// AuthorUserId / AuthorName are the comment's OWN author — an admin editing or deleting another
// user's comment is the envelope `actor`, while the author stays the comment's author.
// comment.deleted carries the comment's state at occurrence (the row is gone after the delete).
public sealed record WebhookCommentData
(
    Guid Id,
    Guid CardId,
    long CardNumber,
    string ContentMarkdown,
    Guid AuthorUserId,
    string AuthorName,
    DateTimeOffset LastUpdatedAtUtc
);

public sealed record WebhookCommentCreatedData(WebhookCommentData Comment, WebhookCardRef Card);

public sealed record WebhookCommentUpdatedData(WebhookCommentData Comment, WebhookCardRef Card);

public sealed record WebhookCommentDeletedData(WebhookCommentData Comment, WebhookCardRef Card);

// The label-resource-family payloads. The label resource itself — created / renamed or
// recolored / deleted — distinct from card.labeled / card.unlabeled (which report a card's
// label-SET changing). Color is nullable on the Label entity, so it rides the wire as nullable too.
// label.deleted carries the label's state at occurrence.
public sealed record WebhookLabelData(Guid Id, Guid BoardId, string Name, string? Color);

public sealed record WebhookLabelCreatedData(WebhookLabelData Label);

public sealed record WebhookLabelUpdatedData(WebhookLabelData Label);

public sealed record WebhookLabelDeletedData(WebhookLabelData Label);

// The attachment-family payloads. Metadata ONLY — the file bytes never ride the wire.
// SizeBytes is the stored payload length (the bytes themselves stay at rest). attachment.deleted
// carries the metadata at occurrence (the row is gone after the delete).
public sealed record WebhookAttachmentData
(
    Guid Id,
    Guid CardId,
    string FileName,
    string ContentType,
    long SizeBytes,
    Guid AddedByUserId,
    DateTimeOffset AddedAtUtc
);

public sealed record WebhookAttachmentCreatedData(WebhookAttachmentData Attachment, WebhookCardRef Card);

public sealed record WebhookAttachmentDeletedData(WebhookAttachmentData Attachment, WebhookCardRef Card);

// The lane-family payloads. lane.created / lane.renamed / lane.deleted carry the single
// lane resource (the envelope's boardId/boardSlug identify the board). lane.deleted carries the
// lane's state at occurrence (the row is gone after the delete).
public sealed record WebhookLaneData(Guid Id, Guid BoardId, string Name, int Position);

public sealed record WebhookLaneCreatedData(WebhookLaneData Lane);

public sealed record WebhookLaneRenamedData(WebhookLaneData Lane);

public sealed record WebhookLaneDeletedData(WebhookLaneData Lane);

// lane.reordered carries the board's FULL new left-to-right order — both the bulk
// reorder_lanes path and a single-lane update_lane position move emit this same shape, so a consumer
// gets the resulting lane order directly with no reconstruction. Each entry is {id, name, position};
// the boardId is on the envelope (all entries share it), so it is not repeated per lane.
public sealed record WebhookLaneOrderEntry(Guid Id, string Name, int Position);

public sealed record WebhookLaneReorderedData(IReadOnlyList<WebhookLaneOrderEntry> Lanes);

// The size-family payloads — structurally a twin of the lane family. size.created / size.renamed /
// size.deleted carry the single card-size resource (the envelope's boardId/boardSlug identify the
// board). size.deleted carries the size's state at occurrence (the row is gone after the delete).
public sealed record WebhookSizeData(Guid Id, Guid BoardId, string Name, int Ordinal);

public sealed record WebhookSizeCreatedData(WebhookSizeData Size);

public sealed record WebhookSizeRenamedData(WebhookSizeData Size);

public sealed record WebhookSizeDeletedData(WebhookSizeData Size);

// size.reordered carries the board's FULL new order — both the bulk reorder_sizes path and a
// single-size update_size ordinal move emit this same shape, so a consumer gets the resulting size
// order directly with no reconstruction. Each entry is {id, name, ordinal}; the boardId is on the
// envelope (all entries share it), so it is not repeated per size.
public sealed record WebhookSizeOrderEntry(Guid Id, string Name, int Ordinal);

public sealed record WebhookSizeReorderedData(IReadOnlyList<WebhookSizeOrderEntry> Sizes);

// The board-family payloads. board.created / board.renamed / board.deleted carry the board
// resource. The envelope's boardId/boardSlug ARE this board; the embedded resource adds the name (and
// slug for completeness). board.deleted references a now-deleted board (state at occurrence).
public sealed record WebhookBoardData(Guid Id, string Slug, string Name);

public sealed record WebhookBoardCreatedData(WebhookBoardData Board);

public sealed record WebhookBoardRenamedData(WebhookBoardData Board);

public sealed record WebhookBoardDeletedData(WebhookBoardData Board);

// The webhook.ping test-delivery payload. A minimal body so an integrator can confirm the
// endpoint is reachable, signs, and parses — carries the subscription id and a human-readable
// message, nothing board-scoped (a ping is not a board mutation).
public sealed record WebhookPingData(Guid SubscriptionId, string Message);
