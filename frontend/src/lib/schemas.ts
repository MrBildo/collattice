import { z } from 'zod';

export const boardSchema = z.object({
  id: z.string(),
  name: z.string(),
  slug: z.string(),
  createdAtUtc: z.string(),
});

export const laneSchema = z.object({
  id: z.string(),
  boardId: z.string(),
  name: z.string(),
  position: z.number(),
});

export const cardSizeSchema = z.object({
  id: z.string(),
  boardId: z.string(),
  name: z.string(),
  ordinal: z.number(),
});

export const cardItemSchema = z.object({
  id: z.string(),
  number: z.number(),
  name: z.string(),
  descriptionMarkdown: z.string(),
  laneId: z.string(),
  position: z.number(),
  sizeId: z.string(),
  isArchived: z.boolean().optional().default(false),
  createdByUserId: z.string(),
  createdAtUtc: z.string(),
  lastUpdatedByUserId: z.string(),
  lastUpdatedAtUtc: z.string(),
});

export const labelSchema = z.object({
  id: z.string(),
  boardId: z.string(),
  name: z.string(),
  color: z.string().nullable().optional(),
});

export const cardCommentSchema = z.object({
  id: z.string(),
  cardId: z.string(),
  userId: z.string(),
  contentMarkdown: z.string(),
  createdAtUtc: z.string(),
  lastUpdatedAtUtc: z.string(),
});

export const boardUserSchema = z.object({
  id: z.string(),
  name: z.string(),
  role: z.number(),
  authKey: z.string(),
  isActive: z.boolean(),
});

export const attachmentMetaSchema = z.object({
  id: z.string(),
  fileName: z.string(),
  contentType: z.string(),
  fileSize: z.number(),
  addedByUserId: z.string(),
  addedAtUtc: z.string(),
});

export const cardLabelSummarySchema = z.object({
  id: z.string(),
  name: z.string(),
  color: z.string().nullable().optional(),
});

export const cardSummarySchema = z.object({
  id: z.string(),
  number: z.number(),
  name: z.string(),
  descriptionMarkdown: z.string(),
  sizeId: z.string(),
  sizeName: z.string(),
  laneId: z.string(),
  position: z.number(),
  isArchived: z.boolean(),
  createdByUserId: z.string(),
  createdAtUtc: z.string(),
  lastUpdatedByUserId: z.string(),
  lastUpdatedAtUtc: z.string(),
  labels: z.array(cardLabelSummarySchema),
  commentCount: z.number(),
  attachmentCount: z.number(),
});

export const boardDataSchema = z.object({
  lanes: z.array(laneSchema),
  cards: z.array(cardSummarySchema),
  sizes: z.array(cardSizeSchema),
});

export const pagedCardSummarySchema = z.object({
  items: z.array(cardSummarySchema),
  totalCount: z.number(),
  offset: z.number(),
  limit: z.number().nullable(),
});

// The best available attribution for a revision nobody observed being written.
// `basis` says how it was inferred ('creator' = the card's creator). It is a
// plain string rather than an enum so that a basis added later on the server
// degrades to a generic label here instead of failing the whole trail's parse.
export const cardHistoryInferredEditorSchema = z.object({
  userId: z.string(),
  name: z.string(),
  basis: z.string(),
});

// One revision in a card field's history trail (GET /cards/{id}/history).
// The attribution fields are null on the trail's oldest revision only: history
// is not back-filled, so nobody observed that value being written, and the
// backend never stores a guess for it. That revision alone carries
// `inferredEditor` instead — a separate field, so an inferred name can't be
// mistaken for an observed one. `value`/`diff`/`inferredEditor` are absent from
// the wire entirely (not null) when they don't apply.
export const cardHistoryEntrySchema = z.object({
  revision: z.number(),
  editedByUserId: z.string().nullable(),
  editedByName: z.string().nullable(),
  editedAtUtc: z.string().nullable(),
  inferredEditor: cardHistoryInferredEditorSchema.optional(),
  value: z.string().optional(),
  diff: z.string().optional(),
});

// The trail envelope. `entries` is newest-first; `totalCount` is the whole
// trail's length regardless of paging, so `entries.length < totalCount` means
// there is more. `limit` echoes what was applied — null when none was passed.
export const cardHistoryTrailSchema = z.object({
  cardId: z.string(),
  field: z.string(),
  entries: z.array(cardHistoryEntrySchema),
  totalCount: z.number(),
  offset: z.number(),
  limit: z.number().nullable(),
});

export const userDirectoryEntrySchema = z.object({
  id: z.string(),
  name: z.string(),
});

export const authMeSchema = z.object({
  id: z.string(),
  name: z.string(),
  role: z.number(),
});

export const versionSchema = z.object({
  version: z.string(),
});

// Current-vs-latest update status from GET /version/status. latest/releaseUrl are null
// until the backend's first successful poll (offline/air-gap/disabled stays null forever, the
// honest degraded state). lastChecked is the timestamp of the last successful poll.
export const versionStatusSchema = z.object({
  current: z.string(),
  latest: z.string().nullable(),
  updateAvailable: z.boolean(),
  releaseUrl: z.string().nullable(),
  lastChecked: z.string().nullable(),
});

export const reorderResponseSchema = z.object({
  lanes: z.array(laneSchema),
  cards: z.array(cardItemSchema),
});

export const uploadAttachmentResponseSchema = z.object({
  id: z.string(),
  fileName: z.string(),
});

export const prunePreviewCardSchema = z.object({
  id: z.string(),
  number: z.number(),
  name: z.string(),
  laneName: z.string(),
  lastUpdatedAtUtc: z.string(),
});

export const prunePreviewResponseSchema = z.object({
  matchCount: z.number(),
  cards: z.array(prunePreviewCardSchema),
});

export const pruneResponseSchema = z.object({
  deletedCount: z.number().optional(),
  archivedCount: z.number().optional(),
});

export const searchResultSchema = z.object({
  boardId: z.string(),
  boardName: z.string(),
  boardSlug: z.string(),
  cards: z.array(cardSummarySchema),
});

export const createTempCardResponseSchema = z.object({
  id: z.string(),
});

export const finalizeCardResponseSchema = z.object({
  id: z.string(),
  number: z.number(),
});

// Runtime config — fetched from /config.json at app boot (not a build-time env var).
// Default object behavior strips unknown keys, so a deployed config.json with
// future fields an older Portal doesn't recognize will not fail validation.
// (Do not add .strict().)
export const runtimeConfigSchema = z.object({
  apiBaseUrl: z.string().min(1),
});

// Webhooks — subscription registry + delivery observability.
// The signing secret is WRITE-ONLY: it never appears in any read shape, only as
// the `signed` boolean. `events` carries the exact event-type strings or the
// single `"*"` wildcard. The metric fields are computed on-read from the
// delivery log (a brand-new subscription reports zeros and nulls).
export const webhookSubscriptionSchema = z.object({
  id: z.string(),
  name: z.string().nullable(),
  url: z.string(),
  enabled: z.boolean(),
  events: z.array(z.string()),
  signed: z.boolean(),
  successCount: z.number(),
  failureCount: z.number(),
  lastDeliveryStatus: z.string().nullable(),
  lastDeliveryAtUtc: z.string().nullable(),
});

// One persisted delivery attempt (GET /webhooks/deliveries). `status` is the
// enum NAME ("Succeeded" | "Failed"); `httpStatusCode` and `error` populate on
// a failed attempt. An SSRF-blocked delivery surfaces here as Failed with a
// null `httpStatusCode` (the request never left) and a blocked-address `error`.
export const webhookDeliverySchema = z.object({
  id: z.string(),
  subscriptionId: z.string().nullable(),
  eventId: z.string(),
  eventType: z.string(),
  boardId: z.string(),
  attempt: z.number(),
  status: z.string(),
  httpStatusCode: z.number().nullable(),
  error: z.string().nullable(),
  attemptedAtUtc: z.string(),
});

export const webhookDeliveriesPageSchema = z.object({
  items: z.array(webhookDeliverySchema),
  totalCount: z.number(),
  offset: z.number(),
  limit: z.number().nullable(),
});

// Global delivery posture (GET /webhooks/status). Booleans + counts only — no
// secret, no URL. `enabled` is the master kill-switch; `allowPrivateNetworkTargets`
// is the SSRF override that decides whether private/internal targets deliver.
export const webhookStatusSchema = z.object({
  enabled: z.boolean(),
  allowPrivateNetworkTargets: z.boolean(),
  subscriptionCount: z.number(),
  enabledSubscriptionCount: z.number(),
});

// The synchronous outcome of POST /webhooks/subscriptions/{id}/test. A blocked
// or unreachable target returns success:false with the reason in `error` — the
// HTTP call itself still succeeds (200), so this is a normal result, not an error.
export const webhookTestResultSchema = z.object({
  success: z.boolean(),
  statusCode: z.number().nullable(),
  error: z.string().nullable(),
});

// The selectable event catalog (GET /webhooks/event-types) — the single
// server-side source of truth the subscription picker renders. A list of
// family groups, each carrying its display `label` and ordered events. An event's
// `type` is the value a subscription selects; `label` is the display token (the
// machine event-type string — what an integrator picks by), with the prose in
// `description`. The server owns all display naming, so the picker renders these
// groups generically and a new backend event appears automatically — no
// frontend-hardcoded copy to drift.
export const webhookEventOptionSchema = z.object({
  type: z.string(),
  label: z.string(),
  description: z.string(),
});

export const webhookEventGroupSchema = z.object({
  family: z.string(),
  label: z.string(),
  events: z.array(webhookEventOptionSchema),
});
