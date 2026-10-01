namespace Collabot.Collattice.Api.Models;

public record CreateCommentRequest(string ContentMarkdown);

public record CreateUserRequest(string Name, UserRole Role);

public record CreateLabelRequest(string Name, string? Color);

public record CreateLaneRequest(string Name, int? Position);

public record CreateSizeRequest(string Name, int? Ordinal);

// Board requests
public record CreateBoardRequest(string? Name);

public record UpdateBoardRequest(string? Name);

// Card requests
public record CreateCardRequest(Guid LaneId, string? Name, string? DescriptionMarkdown, int? Position, Guid? SizeId, string? SizeName, Guid[]? LabelIds);

// ExpectedDescriptionRevision is the optional collision-awareness baseline: the descriptionHistoryCount
// the caller read before editing. When the description is changed and this is supplied, the update
// response carries an exact collision notice iff the description moved past that revision meanwhile.
// Awareness only — it never blocks or fails the save.
public record UpdateCardRequest(string? Name, string? DescriptionMarkdown, Guid? SizeId, Guid? LaneId, int? Position, Guid[]? LabelIds, int? ExpectedDescriptionRevision = null);

public record ReorderCardRequest(Guid? LaneId, int? Index);

// Comment requests
public record UpdateCommentRequest(string? ContentMarkdown);

// Label requests
public record UpdateLabelRequest(string? Name, string? Color);

public record AddCardLabelRequest(Guid LabelId);

// User requests
public record UpdateUserRequest(string? Name, UserRole? Role);

// Lane requests
public record UpdateLaneRequest(string? Name, int? Position);

public record ReorderLanesRequest(Guid[]? LaneIds);

// Size requests
public record UpdateSizeRequest(string? Name, int? Ordinal);

public record ReorderSizesRequest(Guid[]? SizeIds);

// Archive requests
public record RestoreCardRequest(Guid LaneId);

// Prune requests
public record PruneRequest(DateTimeOffset? OlderThan, Guid[]? LaneIds, Guid[]? LabelIds, string? Action, bool? IncludeArchived);

// Webhook subscription requests. Events is the event-selection (validated non-empty / known /
// wildcard in the shared store). Secret is write-only (set on create, replaced on PATCH only when
// present; ClearSecret clears it). Enabled defaults to true on create when omitted.
public record CreateWebhookSubscriptionRequest(string Url, string[]? Events, string? Secret, bool? Enabled, string? Name);

public record UpdateWebhookSubscriptionRequest(string? Url, string[]? Events, string? Secret, bool? ClearSecret, bool? Enabled, string? Name);
