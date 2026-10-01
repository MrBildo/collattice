using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Collabot.Collattice.Api.Models;

public enum UserRole
{
    Administrator,
    HumanUser,
    AgentUser,
    AgentAdministrator,
}

public class Board
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }

    public static string GenerateSlug(string name) =>
        Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
}

public class BoardUser
{
    public Guid Id { get; set; }
    public string AuthKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Lane
{
    public Guid Id { get; set; }
    public Guid BoardId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Position { get; set; }
    public bool IsArchiveLane { get; set; }
}

public class CardSize
{
    public Guid Id { get; set; }
    public Guid BoardId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Ordinal { get; set; }
}

public class CardItem
{
    public Guid Id { get; set; }
    public long Number { get; set; }
    public Guid BoardId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DescriptionMarkdown { get; set; } = string.Empty;
    public Guid SizeId { get; set; }
    public Guid LaneId { get; set; }
    public int Position { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid LastUpdatedByUserId { get; set; }
    public DateTimeOffset LastUpdatedAtUtc { get; set; }
    public bool IsTemp { get; set; }
}

// One recorded version of one card field's value. The store is deliberately field-general — the
// Field discriminator lets title, size or lane history be added later without a schema change —
// but only the description is captured today.
//
// A row is a VERSION, not an edit delta: Value is what the field said at that revision, so any two
// revisions can be diffed against each other and no reconstruction chain is needed. The oldest row
// of a trail is the value that was already in place when recording began; its author and time are
// genuinely unknown (the trail is not back-filled), so both are null rather than attributed to the
// card's creator or to the editor who happened to trigger the capture. An audit trail that guesses
// at provenance is worse than one that admits the gap.
public class CardFieldHistory
{
    public Guid Id { get; set; }
    public Guid CardId { get; set; }
    public string Field { get; set; } = string.Empty;
    public int Revision { get; set; }
    public string Value { get; set; } = string.Empty;
    public Guid? EditedByUserId { get; set; }
    public DateTimeOffset? EditedAtUtc { get; set; }
}

public class CardComment
{
    public Guid Id { get; set; }
    public Guid CardId { get; set; }
    public Guid UserId { get; set; }
    public string ContentMarkdown { get; set; } = string.Empty;

    // Set once at posting and never touched again — the comment's provenance on a board whose
    // comments are a decision ledger. Distinct from LastUpdatedAtUtc, which is bumped on every edit
    // and is what the UI and triage sort by, so an edited comment resurfaces as the latest activity.
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastUpdatedAtUtc { get; set; }
}

public class Label
{
    public Guid Id { get; set; }
    public Guid BoardId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
}

public class CardLabel
{
    public Guid CardId { get; set; }
    public Guid LabelId { get; set; }
}

public class CardAttachment
{
    public Guid Id { get; set; }
    public Guid CardId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Payload { get; set; } = [];
    public Guid AddedByUserId { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
}

public record CardLabelSummary(Guid Id, string Name, string? Color);

// Lane-scan projection of a card's most-recent comment, so a consumer can spot
// an unaddressed operator ruling without a second per-card fetch.
// LastUpdatedAtUtc is the comment's only timestamp — set on create, bumped on edit —
// so an edited comment correctly surfaces as the latest activity.
public record LatestCommentSummary
(
    string? Author,
    bool IsFromAdmin,
    DateTimeOffset LastUpdatedAtUtc,
    string Preview
);

public record CardSummary
(
    Guid Id,
    long Number,
    string Name,
    string DescriptionMarkdown,
    Guid SizeId,
    string SizeName,
    // An archived card's lane is the board's hidden internal archive lane, whose GUID is an
    // implementation detail of no use to an external consumer — the card.archived webhook payload
    // omits it (laneName + isArchived carry the relevant state) by emitting a default lane id, which
    // this drops from the wire. A real card always carries its (non-default) lane id, so REST and the
    // other events are unaffected.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    Guid LaneId,
    int Position,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    Guid LastUpdatedByUserId,
    DateTimeOffset LastUpdatedAtUtc,
    List<CardLabelSummary> Labels,
    int CommentCount,
    int AttachmentCount,
    bool IsArchived,
    LatestCommentSummary? LatestComment
);

public record SearchResult(Guid BoardId, string BoardName, string BoardSlug, List<CardSummary> Cards);

public record PagedResult<T>(List<T> Items, int TotalCount, int Offset, int? Limit);
