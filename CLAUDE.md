# Collattice

Kanban board web application — .NET Minimal API backend + React SPA frontend. Designed for both human users and AI agent collaboration via MCP tooling.

## Repository Rules — Hard

These rules are non-negotiable. They apply to every agent, every dispatch, every commit.

1. **`.agents/*` and `.claude/*` are permanently gitignored. Local-only. No exceptions.** Agent workspaces, dispatch logs, specs, review artifacts, and harness state live here. **Never `git add --force` a path under `.agents/*` or `.claude/*`.** Not for "preserving review files." Not for "consistency with prior tracked files." Not for any reason. If a reviewer or planner produces an artifact (review doc, brief, spec) inside a worktree's `.agents/temp/`, copy it OUT to the main repo's `.agents/temp/` (also gitignored) before cleaning up the worktree. If you find tracked files under these paths, scrub via `git rm --cached -r .agents .claude` on a chore branch and PR to main.

   **Harness gotcha — editing `.claude/*` files:** the harness Edit/Write tools deny paths under `.claude/*` (e.g. a local skill file like `.claude/skills/release/SKILL.md`). This is a built-in Claude Code protection on the `.claude/` directory (same class as `.git/`), enforced above the settings allow/deny precedence chain — it is **not** a project rule and **cannot** be overridden by a `permissions.allow` glob. The filesystem allows the write; only the tools refuse. The canonical workaround is shell-level file I/O, which is outside the tool boundary: PowerShell `[System.IO.File]::WriteAllText($path, $content)` (Windows), or the platform equivalent. For surgical changes to an existing `.claude/*` file, the **Edit** tool is friendlier than **Write** — try it first. This applies to `.claude/*` only; `.agents/*` workspace files edit normally through the standard tools. (Investigated card #245.)

2. **Specs live under `.agents/specs/` and are NOT part of the published source.** Source code comments must not reference spec documents (e.g., `// per .agents/specs/multi-board.md §6.2`) — nor card numbers (`// Card #N`, bare `#N`), session or work-item shorthand, or bot-name attributions: none of these are resolvable by an external reader of the published source. Cross-reference internal design via **inline rationale** (the *why*, written into the comment) or a **full GitHub issue URL** (resolvable by anyone). Card numbers, spec paths, and dispatch shorthand belong to internal artifacts — cards, commit messages, dispatch logs — never to shipped source comments. *(Collattice publishes its source; this matches the suite-wide source-comment convention now in the `dotnet-dev` / `typescript-dev` skills.)*

3. **Persistence: project decisions live in tracked infra docs, not in auto-memory.** Auto-memory (`.claude/projects/.../memory/`) is ONLY for soft personal preferences. All project decisions, conventions, workflows, and hard rules go in `CLAUDE.md`, `COLLATTICE.md`, specs under `.agents/specs/`, or the `collattice` skill (board protocol). If it's about how the project works, update the relevant infra doc — not memory.

4. **Every bot loads `.agents/GLOSSARY.md` at session start, alongside this file and per-bot workspace files.** The glossary is the central hub for team terms, conventions, and operator-anchored rules — including the audience-determines-mode communication rule (human-facing plain English vs bot-internal dialect). Load it before doing work, not after needing it. The file is gitignored; bots read it from their own checkout. Concise by design — bots may extend or prune entries on their own terms.

## Tech Stack

**Backend**
- .NET 10 / C# — ASP.NET Minimal API (Program.cs + endpoint group classes in `Endpoints/`)
- Entity Framework Core — SQLite provider
- Auth: custom header-based (`X-User-Key` only), no ASP.NET auth middleware

**Frontend**
- React 18 + TypeScript
- Vite (dev server + build)
- React Router v6 — routes: `/`, `/boards/:slug`, `/boards/:slug/cards/:cardNumber`
- Tailwind CSS v4 + shadcn/ui
- TanStack Query (data fetching)
- dnd-kit (drag-and-drop)
- react-markdown (markdown rendering)
- Axios (HTTP client)

**Testing**
- xUnit + Shouldly — integration tests via WebApplicationFactory + in-memory SQLite
- Arrange-Act-Assert pattern
- Test file naming: `*.Tests.cs`

**Orchestration**
- Aspire 13.6, OpenTelemetry
- AppHost + ServiceDefaults
- Aspire Dashboard (dev-time observability)

## Build & Run

### Prerequisites
- .NET 10 SDK
- Node.js 22+
- Docker Desktop (for Aspire orchestration)
- Aspire CLI (optional): `irm https://aspire.dev/install.ps1 | iex`

### Recommended: Aspire

```powershell
aspire start
```

Starts the API, frontend Vite dev server, and Aspire dashboard with OpenTelemetry. Use `aspire describe` to check resource status. The dashboard URL is printed to the console on startup — it provides structured logs, traces, metrics, and resource management.

Equivalent without the CLI:

```powershell
dotnet run --project backend/Collabot.Collattice.AppHost
```

The API gets a dynamic port (no more hardcoded 58343). The frontend gets a dynamic port. Aspire handles service discovery between them.

Optionally configure `Admin:AuthKey` in `appsettings.Development.json` in `backend/Collabot.Collattice.Api/` — otherwise a random key is generated and logged on first run.

Aspire does NOT run workloads natively on Linux. Use standalone `dotnet run` for Linux testing.

### Tests
```powershell
cd backend
dotnet test
```

### Aspire Lifecycle

Use the Aspire skill and MCP tools to manage the Aspire lifecycle (start, stop, check resources, read logs/traces). Use `list_resources` and `doctor` to verify state before taking action.

**Hot reload:** Don't restart Aspire for frontend-only changes — the frontend dev server picks up changes automatically via hot reload. Only restart when backend code changes need to be picked up. Unnecessary restarts waste time and change the port.

**File lock gotcha:** If Aspire is running and you need to build or test, kill the Aspire process first. The running API locks DLLs (e.g., `Collabot.Collattice.ServiceDefaults.dll`) and causes MSB3027 file copy errors. Before `dotnet test` or `dotnet build`, check for and kill any running Aspire/Collabot.Collattice.Api processes if the build fails with file lock errors.

### Frontend Only (no Aspire)
```powershell
cd frontend
npm install
npm run dev
```
Vite dev server on port 5173 with proxy to localhost:58343 (requires API running separately).

## Auth Model

Header-based authentication — no ASP.NET auth middleware:
- `X-User-Key` — per-user ULID auth key (stored in `BoardUser` entity), sole auth header
- Roles: `Administrator`, `HumanUser`, `AgentUser`
- `IsActive` flag on `BoardUser` — deactivated users get 401
- Admin seed: uses `Admin:AuthKey` from config if set, else generates ULID and logs it
- **Use `Results.StatusCode(403)` not `Results.Forbid()`** (no auth middleware registered)
- `AgentUser` cannot delete cards; can delete own comments and attachments
- All users see all boards — no board-level membership
- **SSE endpoint (`/boards/{boardId}/events`) is intentionally unauthenticated** (decided 2026-05-29, card #217). The board GUID is the read-only stream's capability; the production-split added no exposure (it was reachable by anyone who could hit the URL before); browser-native `EventSource` can't carry `X-User-Key`. **Revisit before** the board model changes — multi-tenant, public/shared boards, or board-level membership replacing "all users see all boards."

## Configuration Precedence

Settings resolution precedence, highest to lowest:

```
env (Section__Key) > appsettings.json > hardcoded default
```

- **Stock .NET `Section__Key` env-var mapping is the override channel.** No named-env-var ladder, no per-section resolver, no whitespace-is-unset fallthrough. `Section:Key` binds from `Section__Key` (double underscore). This is the model — keep new settings on it.
- **Collabhost injects production overrides via these env vars.** The deployment contract is "all overrides via Collabhost configuration, no manual `appsettings` editing." `Program.cs` re-adds the env-var provider *after* `WebApplication.CreateBuilder` so env vars sit at the top of the provider chain even if a future JSON source is added later (#225 originally established this against `appsettings.Local.json`; #235 retired that overlay but kept the re-add as structural insurance). Do not remove the re-add — `ConfigPrecedenceTests` locks it.
- `appsettings.json` is operator-editable next to the executable. The installer (and a manual `--merge-appsettings` invocation) performs a smart three-way merge on upgrade — operator edits are preserved, untouched shipped defaults are refreshed, new shipped keys are added (#235). A baseline sidecar `appsettings.shipped.json` (gitignored, runtime artifact) records what was last shipped so the next merge can distinguish operator-edited keys from untouched defaults.
- `appsettings.Local.json` was retired by #235; do not reintroduce it. `Program.cs` no longer loads it and `ConfigPrecedenceTests.ProgramCs_DoesNotLoadAppsettingsLocalJson` guards the absence.
- "Hardcoded default" means a settings-POCO initializer or a `GetValue(..., literal)` fallback, not a configuration provider.

## Archive Model

Cards can be **archived** — hidden from normal views but preserved for reference:
- Each board has a hidden **archive lane** (`Lane.IsArchiveLane = true`, `Position = int.MaxValue`)
- Archive lane is auto-created with each board and excluded from all lane listings
- Archived cards are cards whose `LaneId` points to an archive lane
- Archived cards are **fully frozen** — no edits, comments, labels, or attachment mutations (400 response)
- Only **restore** and **delete** are allowed on archived cards
- `ArchiveGuard.IsCardArchivedAsync(db, cardId)` — shared helper for archive checks
- All card responses include `isArchived` (bool) field
- Prune defaults to archive action (not delete)
- Search excludes archived cards unless `archiveBoardId` is specified

## API Surface

All endpoints under `/api/v1/` — with one exception: the card-detail read also has `GET /api/v2/cards/{id}` (sparse versioning; see the Cards row under By-ID operations):

### Boards (admin-only for mutation)

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | /boards | All | List all boards |
| GET | /boards/{idOrSlug} | All | Get board by Guid or slug |
| POST | /boards | Admin | Create board (name required, slug auto-derived, immutable) |
| PATCH | /boards/{id} | Admin | Update name only (slug unchanged) |
| DELETE | /boards/{id} | Admin | Delete board (must have zero non-archive lanes). Returns `{ deleted, archivedCardsDeleted }` if archived cards exist |

### Board-scoped resources

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | /boards/{boardId}/board | All | Composite: lanes + cards + sizes for a board |
| GET | /boards/{boardId}/lanes | All | List lanes for a board |
| POST | /boards/{boardId}/lanes | Admin | Create lane in a board. Omitted `position` appends after the board's last non-archive lane; a taken position → 409, int.MaxValue → 400 (reserved). Position resolution is shared with MCP `create_lane` via `LanePositionHelper` |
| POST | /boards/{boardId}/lanes/reorder | Admin | Reorder all non-archive lanes. Body `{ laneIds }` — the complete desired left-to-right order; server assigns dense positions 0..n-1. All-or-nothing: must equal the board's current non-archive lane set, else 400 with no change. One `PublishBoardUpdated` broadcast (#277) |
| GET | /boards/{boardId}/sizes | All | List card sizes for a board (ordered by ordinal) |
| POST | /boards/{boardId}/sizes | Admin | Create size in a board. Omitted `ordinal` goes one past the board's highest; a taken ordinal or name → 409; 400 when omitted and the highest is int.MaxValue. Shared with MCP `create_size` via `SizeCreateHelper` |
| GET | /boards/{boardId}/cards | All | List cards (enriched: labels, sizeId, sizeName, commentCount, attachmentCount, isArchived). Returns `{ items, totalCount, offset, limit }` paged envelope. Optional query params: `since` (DateTimeOffset), `labelId` (Guid), `laneId` (Guid), `includeArchived` (bool, default false), `offset` (int, default 0), `limit` (int, optional, max 200 — omit for all results) |
| POST | /boards/{boardId}/cards | All | Create card in a board (accepts `sizeId` or `sizeName`, defaults to lowest-ordinal size). Card numbers are board-scoped (each board starts at 1 independently) |
| GET | /boards/{boardId}/labels | All | List labels for a board |
| POST | /boards/{boardId}/labels | Admin | Create label in a board |
| PATCH | /boards/{boardId}/labels/{id} | Admin | Update label name/color. A name another label on the board holds → 409 (also when a concurrent create or rename takes it between the check and the save), nothing saved. Shared with MCP `update_label` via `LabelUpdateHelper` |
| DELETE | /boards/{boardId}/labels/{id} | Admin | Delete label + cleanup card assignments |

### By-ID operations (flat, resource knows its board)

| Resource | Endpoints |
|----------|-----------|
| Lanes | `GET /lanes/{id}`, `PATCH /lanes/{id}` (400 if archive lane; rejects `int.MaxValue` position), `DELETE /lanes/{id}` (400 if archive lane) |
| Sizes | `GET /sizes/{id}`, `PATCH /sizes/{id}` (name/ordinal; a name or ordinal another size on the board holds → 409, also when a concurrent write takes it between the check and the save, nothing saved; shared with MCP `update_size` via `SizeUpdateHelper`), `DELETE /sizes/{id}` (blocked if in use by cards) |
| Cards | `GET /cards/{id}` (enriched: card, sizeName, user names, comments, labels, attachments, isArchived, `descriptionHistoryCount`. **Comments compat pivot (#389)**: v1 returns comments as a **plain array** — the whole thread, **oldest-activity-first** — restoring the v2.0.2 shape, plus the additive `includeDescription` projection (default true; `false` omits the description body — the card body is a dedicated response DTO, never the tracked entity, so a projected read cannot persist a blank), per-comment `createdAtUtc` (stamped once at posting, #384), and `descriptionHistoryCount`. `commentsOffset`/`commentsLimit` are **not** on v1 — they live only on v2. **v1 card detail is deprecated as a resource**: every response (incl. 404) carries a `Deprecation: @<epoch>` header + a `Link` `rel="successor-version"` → `/api/v2/cards/{id}`; no `Sunset` yet (removal at a future major, no date set). `descriptionHistoryCount` — recorded description revisions, equal to the history trail's `totalCount`; 0 or ≥2, never 1, since a first edit records both the pre-existing value and its replacement; present in every projection. Field-qualified on purpose: the store lights up further fields later and a bare `historyCount` would have to change meaning. #364), `GET /api/v2/cards/{id}` (the recommended read; **sparse versioning — v2 exists only for card detail, every other endpoint stays v1**. Field projection + comments as a `PagedResult` sub-envelope `{ items, totalCount, offset, limit }`, **newest-activity-first** by `LastUpdatedAtUtc`: omit `commentsLimit` → whole thread enveloped, clamp a given value `[1,200]`, `commentsLimit=0` = count-only; `includeDescription` + `descriptionHistoryCount` behave identically to v1. Shares `CardDetailBuilder` with v1 so the two cannot drift. No deprecation headers. #382, #389), `PATCH /cards/{id}` (accepts `sizeId`; returns the enriched `CardSummary` fields plus, on a description edit that overlapped another user's, an optional `collision` object `{ kind, field, actor: { userId, name } }`; 400 if archived. **Collision awareness (#369)** — additive and non-breaking: the response is the same flat summary with `collision` beside it when present, attached only here and at MCP `update_card`, never through the shared `CardSummaryBuilder`, so it is structurally incapable of appearing in list / search / webhook payloads. Pass `expectedDescriptionRevision` in the request body — the `descriptionHistoryCount` read before editing — for an **exact** answer (`kind: "exact"`, `field: "description"`): `collision` is present iff the description moved past that revision meanwhile, `actor` naming who was overwritten. Omit it and a best-effort **approximate** signal (`kind: "approximate"`, `field: null`) fires when someone else edited the card within a short window (`CardCollisionDetector.ApproximateWindow`, 10s) before the write. Field-general by construction — the detector is handed the field and reads that field's trail; description is the only field lit today. **Awareness, never blocking**: the save always succeeds, last-write-wins is unchanged, there is no conflict status), `DELETE /cards/{id}`, `POST /cards/{id}/reorder` (400 if archived or target is archive lane), `POST /cards/{id}/archive` (all roles; 400 if already archived), `POST /cards/{id}/restore` (accepts `{ laneId }`; all roles; 400 if not archived) |
| Card history | `GET /cards/{id}/history` — the card's description edit trail, newest-first, each revision attributed + timestamped. Query: `field` (default `description`, unknown → 400), `format` (`diff`\|`full`\|`both`, **REST default `both`**), `from`+`to` (both or neither; returns the single-object pair shape instead of the trail), `offset` (default 0) + `limit` (1–200, **omit for the whole trail** — REST's card-list convention). Response `{ cardId, field, entries, totalCount, offset, limit }`; `totalCount` is the whole trail regardless of paging. Pages come off the newest end and the entry at a page's oldest edge is still diffed against its real predecessor (the query fetches one row past the page for exactly this). `offset`/`limit` alongside `from`/`to` → 400. Any authenticated user — visibility follows the card's. History accrues from a card's first description edit after #357 shipped (no back-fill); the trail's oldest revision carries the pre-existing value with **null** author/timestamp, because nobody observed it being written — and, on that revision alone, an additive `inferredEditor` `{ userId, name, basis }` (`basis: "creator"` = the card's creator), resolved on read in `CardHistoryBuilder` and never written to the stored row, so an inferred author and an observed one stay distinguishable by shape (#397; no inferred timestamp — creation time is not necessarily when that text was written). Whole values are stored, diffs rendered on read (`UnifiedDiff`, git-style hunks, `\n` endings). Capture is staged by `CardHistoryHelper` from both the REST PATCH and MCP `update_card` paths so the two cannot drift; that helper's `SaveWithRevisionRetryAsync` **replaces the caller's `SaveChangesAsync`** on both paths and rebuilds-and-retries when a concurrent edit wins the revision ordinal — a rebuild, not a renumber, so the seed row is never duplicated, and the rebuild **re-decides whether there is anything to record**: if the winner set the description to the same text, the retry records nothing rather than appending a duplicate revision with an empty diff (an empty diff means "oldest revision", so a duplicate would forge that). 8 immediate attempts with no pause between them — the same shape as `CardNumberHelper`. Re-measured with writers released together on one card, 8–128-way, a fresh card per round, arms interleaved (9,600 edits per shape): no retry loses ~3 of every 4 edits (~7 of 8 at 8-way), 3 immediate retries ~5%, the earlier 5-with-a-2–14 ms-pause shape ~2.5%, 5 immediate ~0.2%, 8 immediate 0 (only at 128-way did an edit ever need all 8 attempts). The likely reason a pause hurts here: the SQLite provider already retries a held write lock after a fixed 150 ms sleep, on a head read before it slept, so a pausing loser gives the lock away and sleeps on a stale head. That explanation is not settled (the size-create retry measured a pause as helping), so each allocator's retry shape follows its own measurement (#406). The retry fires only on a revision collision — a unique-constraint failure elsewhere in the same save reaches the caller as itself. `EditedAtUtc` is stamped where the ordinal is derived, so revision order and timestamp order cannot disagree; **revision order is the authority** (stamps can tie at clock resolution). Two concurrent description edits both land as attributed revisions; there is no conflict response, and last-one-wins on the card's text is unchanged from before history existed. (#357, #364, #365) |

### Global resources (not board-scoped)

| Resource | Endpoints |
|----------|-----------|
| Users | `GET /users`, `GET /users/{id}`, `POST /users`, `PATCH /users/{id}`, `PATCH /users/{id}/deactivate`, `GET /auth/me` |
| Card Labels | `GET /cards/{id}/labels`, `POST /cards/{id}/labels` (validates label belongs to same board as card), `DELETE /cards/{id}/labels/{labelId}` |
| Comments | `GET /cards/{id}/comments`, `POST /cards/{id}/comments` (400 if archived), `PATCH /comments/{id}` (400 if archived), `DELETE /comments/{id}` (400 if archived) |
| Attachments | `GET /cards/{id}/attachments`, `POST /cards/{id}/attachments` (400 if archived), `GET /attachments/{id}` (auth required — downloads attachment content via `X-User-Key`; no browser-native `<img>` consumer), `DELETE /attachments/{id}` (400 if archived) |

### Webhooks (admin-level — `Administrator` / `AgentAdministrator`; D1 uniform — #326)

| Method | Path | Notes |
|--------|------|-------|
| GET | /webhooks/subscriptions | List subscriptions, each enriched with on-read metrics (success/failure counts, last-delivery status+time); **secret-free** (`signed: bool`, never the value) |
| GET | /webhooks/subscriptions/{id} | One subscription (secret-free) |
| POST | /webhooks/subscriptions | Create (`{ url, events[], secret?, enabled?=true, name? }`); SSRF registration check; `events` non-empty (or `"*"` wildcard); returns the created subscription (secret-free) |
| PATCH | /webhooks/subscriptions/{id} | Update; secret **set/keep/clear** (omit=keep, value=replace, `clearSecret`=clear); URL-changing edits re-validate SSRF; secret-free return |
| DELETE | /webhooks/subscriptions/{id} | Delete (`{ deleted: true }`); delivery-log history kept (`SubscriptionId` SetNull) |
| POST | /webhooks/subscriptions/{id}/test | Synchronous `webhook.ping` through the same SSRF-guarded + signing pipe; one attempt row; `{ success, statusCode, error? }` inline |
| GET | /webhooks/deliveries | Persisted delivery attempts (newest first); filters `boardId`, `subscriptionId`; `PagedResult`; item carries `subscriptionId` |
| GET | /webhooks/status | Global posture + counts: `{ enabled, allowPrivateNetworkTargets, subscriptionCount, enabledSubscriptionCount }` (booleans/counts only) |
| GET | /webhooks/event-types | The full selectable event catalog with display metadata — a list of family groups `[{ family, label, events: [{ type, label, description }] }]`. The single server-side source of truth the admin UI's subscription picker consumes (#336); driven off `WebhookEventCatalog`, pinned by test to `WebhookEventTypes.All`, so the picker can never drift from what the backend emits/accepts |

Delivery: per-subscription fan-out; uniform SSRF guard (4-control floor incl. connect-time DNS-rebind pin; **no grandfather — D3 deliberate breaking change**; `Webhooks:AllowPrivateNetworkTargets` override, default false; the webhook delivery client opts out of the standard resilience handler so the blocked-error isn't masked). Shared `WebhookSubscriptionStore` is the un-bypassable REST/MCP seam. Secret write-only at the API, plaintext at rest (symmetric HMAC key). Config-migration seeds v1 `Webhooks:Endpoint`/`Secret` into a subscription on first v2 boot (gated on an empty subscription table, v1-parity selection). Retention sweep ages out the delivery log (`Webhooks:DeliveryLogRetentionDays`, default 30, 0 = keep). M1 selectable events = `card.created`/`card.moved`; the full catalog landed in M2 (#329) at 22 events — 27 today, with `card.deleted` and the `size.*` family — and is exposed for the picker via `GET /webhooks/event-types` (#336). (#326)

### SSE Events

| Path | Notes |
|------|-------|
| /boards/{boardId}/events | Per-board stream; label mutations broadcast per-board, user mutations broadcast globally |

### MCP

| Path | Notes |
|------|-------|
| /mcp | Streamable HTTP transport — 46 tools across SystemTools, BoardTools, CardTools, DuplicateCardTools, ArchiveTools, CommentTools, AttachmentTools, LabelTools, LaneTools, SizeTools, PruneTools, BulkCardTools, SearchTools, WebhookTools, HistoryTools |

**Tools (46):**
- **SystemTools:** `get_api_info` (returns base URL and API prefix for direct REST calls)
- **BoardTools:** `get_boards`, `get_lanes` (boardId required, includes cardCount per lane; excludes archive lanes), `get_sizes` (boardId required, ordered by ordinal), `create_board` (admin-level; slug auto-derived, seeds archive lane + default sizes), `update_board` (admin-level; rename only)
- **CardTools:** `create_card` (supports labelIds, sizeId/sizeName — defaults to lowest-ordinal size; appends at the bottom of the lane; blocks archive lane), `move_card` (index optional; blocks to/from archive lane), `update_card` (supports laneId/index move, sizeId/sizeName, labelIds replace, no-op guard; blocks archived cards; returns enriched card summary with labels, sizeName, commentCount, attachmentCount, isArchived; a description change records history and two concurrent edits both land as attributed revisions — no conflict response to handle. **Collision awareness (#369)**: pass `expectedDescriptionRevision` = the `descriptionHistoryCount` from `get_card` before editing to learn whether a description edit overwrote another user's — the returned card carries an optional `collision` `{ kind: "exact", field: "description", actor: { userId, name } }` iff it did; omit the param and a best-effort `kind: "approximate"` signal (field null) fires when another user edited the card within a few seconds prior. `expectedDescriptionRevision` is not a change (passing only it → "No changes specified."). Same flat additive shape as REST PATCH and the same shared `CardCollisionDetector`; awareness only — the save always succeeds), `get_cards` (enriched: labels, sizeId, sizeName, commentCount, attachmentCount, isArchived; returns `{ items, totalCount, offset, limit }` paged envelope; `offset` param default 0, `limit` param default 200, max 500; `includeArchived` param default false), `get_card` (enriched: sizeName, attachments, user names, isArchived, `descriptionHistoryCount`; supports cardNumber lookup. **Comments compat pivot (#389)** — one tool, opt-in envelope, no `_v2` twin: `includeDescription` (default true; false omits the description body). **Always pass `commentsLimit`** (max 500) — with it, comments come back as a paged sub-envelope `{ items, totalCount, offset, limit }` newest-activity-first, `commentsLimit=0` = count-only; `commentsOffset` requires `commentsLimit` (fail-loud error otherwise). **Omitting `commentsLimit` is the DEPRECATED legacy path**: the whole comment thread as a plain array, oldest-activity-first, unbounded — byte-compatible with older clients, to be removed at a future major; the tool description leads with "always pass commentsLimit". Each comment carries `createdAtUtc` beside `lastUpdatedAtUtc`. Shares `CardDetailBuilder` with REST — `BuildLegacyAsync` for the array, `BuildAsync` for the envelope — so v1/v2/MCP cannot drift. #382, #389)
- **DuplicateCardTools:** `duplicate_card` (all roles; card ref + optional `laneId`, defaulting to the source's lane. Creates a new card carrying the source's name, description, labels and size — no comments, attachments, description history or position, and no link back to the source. Routed through the same `CardCreateHelper` → `CardNumberHelper` → `PublishCardCreatedAsync` path as `create_card`, so the copy gets the next board number, appends at the bottom of its lane and emits `card.created` by construction. An archived source requires `laneId` (fail-loud otherwise — the archive lane accepts no new card); the archive lane and other boards' lanes are rejected; temp cards are rejected. Optional overrides: `name`, `sizeName`, `labelIds` (replace; empty clears). No REST twin — duplication composes from `GET /cards/{id}` + `POST /boards/{boardId}/cards`. #395)
- **ArchiveTools:** `archive_card` (all roles; moves card to archive lane), `restore_card` (all roles; requires laneId; moves card from archive to target lane)
- **CommentTools:** `add_comment` (comment text via `contentMarkdown` — required, sole param; blocks archived cards), `update_comment` (edit comment text; own-or-admin-level; blocks archived cards; `contentMarkdown` required, sole body param), `delete_comment` (blocks archived cards; own-or-admin-level)
- **AttachmentTools:** `upload_attachment` (5MB base64 cap — larger files up to 50MB go via the REST endpoint `POST /api/v1/cards/{cardId}/attachments`; blocks archived cards), `download_attachment` (returns base64 content), `delete_attachment` (blocks archived cards; own-or-admin-level)
- **LabelTools:** `get_labels`, `add_label_to_card` (supports labelName; blocks archived cards), `remove_label_from_card` (supports labelName; blocks archived cards), `create_label` (admin-level), `update_label` (admin-level; name/color; rejects a taken name), `delete_label` (admin-level; cleans up CardLabel rows)
- **LaneTools:** `create_lane` (admin-level; `position` optional — omitted appends after the board's last lane; rejects a taken position and the reserved int.MaxValue position), `update_lane` (admin-level; name/position; rejects archive lane, position collision), `delete_lane` (admin-level; rejects archive lane or non-empty lane), `reorder_lanes` (admin-level; `orderedLaneIds` CSV = complete desired order of the board's non-archive lanes; server assigns dense 0..n-1 via a two-phase renumber under the unique (BoardId, Position) index; all-or-nothing set-equality pre-validation, archive lane rejected) (#277)
- **SizeTools:** `create_size` (admin-level; auto-ordinal if omitted; rejects a taken ordinal or name), `update_size` (admin-level; name/ordinal; a taken name or ordinal rejected), `delete_size` (admin-level; rejects size in use by cards), `reorder_sizes` (admin-level; `orderedSizeIds` CSV = complete desired order of the board's sizes; server assigns dense 0..n-1 via a two-phase renumber under the unique (BoardId, Ordinal) index; all-or-nothing set-equality pre-validation) (#308)
- **PruneTools:** `prune_preview` (admin-level; read-only; `{ matchCount, cards }`; filters: olderThan, laneIds, labelIds, includeArchived; excludes archived by default), `prune` (admin-level; **archive only** — no delete action and no prune_delete tool, by design per #243's exclusion list; `{ archivedCount }`)
- **BulkCardTools:** `bulk_archive_cards`, `bulk_restore_cards` (requires targetLaneId; all cards must share the target lane's board), `bulk_update_cards` (uniform laneId/index move, sizeId/sizeName, labelIds replace — folds in bulk-move; per-card name/description not offered). All three are **all-roles** (gate via `RequireUserAsync`, matching the per-card analogs they batch) and accept `cardIds` (CSV of GUIDs) XOR `cardNumbers` (CSV) + `boardId`/`boardSlug`. **Two-phase semantics:** Phase 1 pre-validation fails loud with a single `"Error: ..."` string and performs no mutations (ref-shape/parse, card existence, board-match and target premises); Phase 2 per-card execution is best-effort with a per-item envelope `{ totalRequested, succeeded, failed, results: [{ cardId, number, status, error? }] }` (results align 1:1 with input order), one `SaveChangesAsync` at the end, one broadcast per affected board (deduplicated). No `bulk_delete_cards` — delete is irreversible, excluded by design. (#196)
- **SearchTools:** `search_cards` (cross-board free-text search; query `q` — prefix `#` for exact card-number lookup; results grouped by board, each card carries enriched `CardSummary` shape; default limit 20, max 50; optional `boardId` ranks one board first without scoping; optional `archiveBoardId` includes archived cards from that board only; mirrors REST `GET /search/cards`)
- **WebhookTools:** `create_webhook` (admin-level; `url`, `events` CSV, `secret?`, `enabled?`, `name?`; SSRF registration check; secret-free `signed: bool` return), `list_webhooks` (admin-level; all subscriptions, global, secret-free + on-read metrics), `update_webhook` (admin-level; secret set/keep/clear via `clearSecret`; URL-changing edits re-validate SSRF), `delete_webhook` (admin-level; delivery-log history kept), `test_webhook` (admin-level; synchronous `webhook.ping` through the same SSRF-guarded + signing pipe). All delegate to the shared `WebhookSubscriptionStore` — the SSRF check is **un-bypassable** from MCP (no `get_webhook` single-get; `list_webhooks` returns everything at registry scale). (#326)
- **HistoryTools:** `get_card_history` (all roles, read-only; the card's description edit trail newest-first with per-revision author + timestamp. `format` defaults to **`diff`** here — the opposite of REST's `both` — because a bot asking "what changed?" should not pay for N full snapshots; `full`/`both` are available. `field` defaults to `description`. `from`+`to` returns the arbitrary-pair diff instead of the trail. `offset` (default 0) + `limit` (**default 200, max 500** — `get_cards`' convention, not REST's omit-for-all, because this is the surface that pays per token); `totalCount` always reports the whole trail so the cap is visible rather than silent. `offset`/`limit` with `from`/`to` → error. The oldest revision carries the same `inferredEditor` as REST. Mirrors REST `GET /cards/{id}/history` via the shared `CardHistoryBuilder`.) (#357, #365, #397)

**Admin-level tools** require the `Administrator` or `AgentAdministrator` role (gated via `McpAuthService.RequireAdminLevelAsync`). Strict-admin-only operations (delete board, prune-delete, user CRUD) are deliberately absent from the MCP surface entirely.

**Cross-cutting:** Card numbers are **board-scoped** (unique per board, not globally). All card-scoped tools accept `cardNumber` (long) as alternative to `cardId` (Guid), but **`cardNumber` requires `boardId` or `boardSlug`** — no fallback to global lookup. Label assignment tools accept `labelName` as alternative to `labelId`. Size tools accept `sizeName` as alternative to `sizeId`. Shared resolution via `McpCardResolver`.

## `.agents/` Workspace (gitignored)

Instance-local workspace. Run `/bootstrap` to create on a fresh clone. Layout:

```
.agents/
├── DISPATCH_LOG.md           # Project-scope dispatch log (Cora)
├── agents/<name>/            # Per-bot workspaces (LESSONS, JOURNAL, TODO, HANDOFF, archive/)
├── docs/                     # Internal team docs — TEAM.md, ONBOARDING.md
├── specs/                    # Architecture specs and feature specs (TEMPLATE.md included)
├── research/                 # Research outputs, grouped per effort
├── kb/                       # Knowledge bases
├── temp/                     # Scratch — design discussions, review artifacts, briefs
├── roadmap/INDEX.md          # Living backlog of ideas / decisions outside the board
└── archive/                  # Completed specs, milestone handoffs, post-mortems (append-only)
```

Workspace governance lives in the `agent-workspace` skill — not in this doc. Per-bot workspaces and how they're maintained are owned by each bot.

## Coding Conventions

### Skills agents must use

- **C# / .NET work:** invoke the `dotnet-dev` skill. Universal conventions live there.
- **TypeScript / React work:** invoke the `typescript-dev` skill. Universal conventions live there.

The skills carry universal patterns. The sections below name only Collattice-specific overrides, project structure, and conventions that don't fit cleanly in skill scope.

### .NET overrides

- **`.editorconfig` is the source of truth** for formatting and analyzer severity. Don't override it; configure your editor to respect it. Don't modify it to work around conflicts either — restructure the code or use `#pragma` instead.
- **Run `dotnet format` before committing.**
- **Use `Results.StatusCode(403)` not `Results.Forbid()`** — there's no auth middleware registered, so `Results.Forbid()` throws at runtime.

### Endpoint structure

- Endpoints live in static classes under `backend/Collabot.Collattice.Api/Endpoints/`, one file per resource (`BoardEndpoints.cs`, `UserEndpoints.cs`, etc.).
- Extension methods on `RouteGroupBuilder` map to `api.MapXxxEndpoints()`.
- `Program.cs` is a thin composition root (builder + services + middleware + endpoint registration).

### Frontend overrides

- **shadcn/ui primitives only** — components from `@/components/ui/`. Never raw `<button>`, `<input>`, or `<dialog>` elements.
- **TanStack Query for all API calls.** No bare `fetch` or `axios` calls in components / hooks — go through `@/lib/api` (Axios instance).
- **`cn()` from `@/lib/utils`** for conditional classes.
- **Design system lives in `src/styles.css`.** Use semantic tokens (`bg-primary`, `text-foreground`, etc.). Never hardcoded colors. See [Frontend Design System](#frontend-design-system) below for the full token set.
- **No silent mutation failures.** A mutation `onError` never has `console.error` as its only operator-facing behavior. Every mutation either surfaces through the global `MutationCache.onError` floor — a toast or inline error chosen by the attention-locus rule (operator looking at the thing that failed → inline; attention elsewhere → toast) — or handles the failure inline at the call site with `meta: { skipToast: true }`. `console.error` is fine as a developer log on top, never the only surface. Success is silent by default (toast only where the result isn't already visible on screen). Convention set by #203 (PR #250); the #240 review gate cites this — a new `onError` that only `console.error`s is a convention miss.

### Testing

- **Backend:** xUnit + Shouldly via `WebApplicationFactory` + in-memory SQLite. No mocking. Arrange-Act-Assert. Test classes per resource: `*EndpointTests.Tests.cs`. Shared infrastructure: `Infrastructure/CollatticeApiFactory.cs`, `TestAuthHelper.cs`.
- **Frontend:** TypeScript typecheck + Vite build + `npm run test` cover correctness; lint + format:check cover style.
- **Visual / browser testing is bot-discretionary via the `browser-verify` skill.** Reach for it when a behavioral question can only be answered in a real browser (e.g., transient drag-drop animation frames, SSE cross-context delivery). For routine correctness, tsc + Vite + backend tests still cover the bar.
- **CSS whose effect depends on build-time minification must be verified against the production build (`vite build` + `vite preview`), not the dev server.** Tailwind v4's Lightning CSS pass (which the dev server and jsdom both skip) folds `transform` + `translate` + `scale` declared in one rule into a single `transform` shorthand and drops the standalone `translate`/`scale` resets — so a fix that relies on individual `transform`-family longhand resets can pass dev + tests + lint and still be a no-op in production. Corollary antipattern to avoid: don't reset `transform` alongside a `-translate-*` / `scale-*` / `rotate-*` utility expecting the longhand resets to survive — they get folded.

### Git

- **Never commit directly to main.** All changes via feature branch + PR.
- **Conventional commits:** `feat:`, `fix:`, `chore:`, `docs:`, `refactor:`.
- **Branch naming:** `feature/`, `bugfix/`, `hotfix/`, `chore/`. Release/integration branches (`release/<descriptive-name>` — never version numbers; `release/v1.10.0` is wrong, `release/backend-v1` is right) are the **bundle exception only** — see Branch strategy below.
- **Squash merge to main via `gh pr merge --squash --delete-branch`.** Never local `git merge --squash` — it leaves PRs dangling open.
- **Delete branches after merge** — don't let stale branches accumulate.
- **Always merge PRs via `gh pr merge`**, never local merge.
- **Attribution is the bot's, and only the bot's** *(operator rule, 2026-09-02)*. A bot's commit carries `Co-Authored-By: <Bot> <bot@collabot.dev>` as its trailer (with the inline `-c user.name/user.email` author) — and **nothing else**: no `Co-Authored-By: Claude …` / `noreply@anthropic.com` line (it names the wrong model), no `Claude-Session:` trailer, no session link. PR bodies carry **no "Generated with Claude Code" footer and no `claude.ai/code/session_…` link**. The harness reminder that asks for those trailers is overridden by this rule. Squash bodies written at merge follow the same shape (`Co-Authored-By: <author bot>`, plus the coordinator when they authored the squash).

#### GitHub issues — stewardship of outside reports

The repo is public and receives issues from outside integrators. Every issue is handled the same way:

- **Tag on first read:** one stock *type* label (`bug` / `enhancement` / `documentation` / `question`) **and** one `area:` label (`area: api` · `area: mcp` · `area: webhooks` · `area: frontend` · `area: install`). Type says *what kind*, area says *where*. Add an area label only when a new surface genuinely needs one.
- **Assign** the repo owner (assignees must be collaborators) — the visible "someone owns this" signal.
- **Track on the board:** a card in Triage whose description links the issue; the card carries the recon and rulings, the issue carries the reporter-facing conversation.
- **Reply after recon, not before** — confirm or correct the premise from source, say what will change and whether the reporter's PR offer is taken, and sign as the bot with its role (*"Cora, Project Manager, Collattice · Collabot.dev"*) plus a line identifying it as an AI agent on the Collabot.dev team. Plain English, no internal card numbers.
- **Close via the fix PR** — the PR body carries `Fixes https://github.com/MrBildo/collattice/issues/<n>` and credits the reporter; GitHub closes the issue on merge.
- **Follow up on release** — comment on the closed issue naming the version that ships the fix, so the reporter knows when to upgrade. Sibling findings from the same investigation get their own cards (and issues only if reporter-facing), never appended to the reporter's thread.
- **Every public GitHub surface a bot writes is signed** *(operator rule, 2026-09-02)* — issue comments, PR comments, and PR reviews alike, not only issue replies. The `gh` account is the operator's, so the signature is the only thing that identifies the author. The block, verbatim shape:

  ```
  — <Bot>
  <Role>, Collattice · Collabot.dev
  *(I'm an AI agent on the Collabot.dev team.)*
  ```

#### Branch strategy — trunk-based by default

Default: **PR every card straight to `main`.** `main` is the integration point and stays releasable at all times. A release is a **tag on a point in `main`'s history**, not a branch that gets merged — cut it with `/release` when enough has accumulated; the changelog reconciles from the conventional-commit `#NNN` PR log since the last tag. The version (PATCH/MINOR/MAJOR) is decided at cut time from what accumulated, not planned up front.

This requires `main` stays releasable: **each PR must be independently shippable.** Work that isn't (a half-built feature, a risky or destructive change) goes behind a flag, or uses the integration-branch exception below — it does not land half-done on `main`.

**Integration-branch exception.** Use a single integration branch only when a set of PRs must land *atomically* and is *not* independently shippable, OR when you want one go/no-go gate over a named bundle. The discriminator:

> **Is there a named bundle with a go/no-go gate over the bundle?**
> - **Yes** (a milestone like the v1.12.0 production split, or a staged destructive change) → cut `release/<descriptive-name>` from `main` (or `feature/<epic>` for a feature epic — mechanically identical; the name is semantic). Sub-cards PR into it; the aggregate PR → `main` is the operator's one go/no-go gate on the bundle.
> - **No** (independent cards, ship when ready, cut a release when accumulated) → trunk default; PR straight to `main`.

**The discriminator is evaluated continuously, not once at first-card.** An investigation can crystallize into a shared-root-cause multi-PR correction mid-arc — a bundle that earns its gate late (e.g., a production-incident class that fans out into coordinated PRs). When the answer flips to "yes" mid-arc, cut the integration branch at that point and reparent the in-flight work. A discriminator asked only at first-card has no gate when an emergent bundle reveals itself.

The release/integration branch is the deliberate, named exception — not the default path. Single-PR releases never use it (PR → `main` → `/release`). When trunk is the mode, the operator's go/no-go gate is the changelog-diff review at release-cut time, not an aggregate PR.

#### Parallel work safety

Branches that touch overlapping files MUST serialize. Backend cards with disjoint files can run in parallel. Frontend cards on the same components must serialize. **If two agents could write to the same file, they must be the same agent.** The dependency check drives execution order — don't document it and ignore it. If in doubt, sequence; the time saved by parallelism is lost to conflict resolution.

## Agent Behavior Rules

**Safety over speed.** Optimize for safety, always. Move slow. Verify each step before moving to the next. Wait for user confirmation at natural checkpoints. Don't batch risky operations. The cost of a mistake far exceeds the time saved by going fast.

- **Do not auto-fix lint errors.** When any lint errors are encountered — GitHub CI, local eslint, dotnet format, or any other linter — do NOT automatically fix them. Stop, evaluate, summarize the issues to the user, and wait for instructions before making changes.
- **Ask, don't guess.** If stuck or unsure, report back rather than guessing. Max 3 follow-up rounds per task before escalating to user.

## Frontend Design System

### Colors

Use semantic Tailwind tokens, never hardcoded colors. The design system lives in `src/styles.css`.

```
bg-background    — page background
bg-card          — card/panel surfaces
bg-muted         — muted/recessed areas
bg-primary       — primary actions (cyan)
bg-accent        — highlights/badges (amber)
bg-destructive   — delete/error actions
text-foreground  — primary text
text-muted-foreground — secondary/helper text
border-border    — all borders
```

### Typography

- **Headings / UI labels:** Space Grotesk (font-sans via Tailwind)
- **Body / prose:** DM Sans (applied via prose contexts)
- Use Tailwind text utilities (`text-sm`, `text-base`, `text-lg`). No custom font sizes.

### Components

Always use shadcn/ui primitives from `@/components/ui/`. Never build raw HTML buttons, inputs, or dialogs.

- `<Button variant="default|outline|ghost|destructive" size="default|sm|lg">`
- `<Badge variant="default|secondary|outline">`
- `<Dialog>` / `<Sheet>` for modals and panels
- `<Input>`, `<Textarea>`, `<Select>` for form elements
- `<Tabs>`, `<Table>` for structured content
- `<Separator>` for visual dividers

### Cards

Kanban cards use: `rounded-lg shadow-xs border border-border bg-card p-3 hover:shadow-md transition-shadow`

### Icons

Use Lucide React (`lucide-react`). Import individual icons:
```tsx
import { Pencil, Trash2, Plus } from 'lucide-react';
```
Size: `className="w-4 h-4"` for inline, `"w-5 h-5"` for standalone.

### Dark Mode

Both light and dark themes are defined via CSS custom properties in `styles.css`. Dark mode activates via `data-theme="dark"` on the root element. Components should NOT use `dark:` Tailwind variants — the CSS vars handle everything automatically.

### Don'ts

- No inline styles except `image-rendering: pixelated` on the logo
- No `dark:` prefixed Tailwind classes — use the semantic tokens instead
- No new color values — if you need a color, it should come from the existing CSS vars
- No custom CSS classes — use Tailwind utilities composed via `cn()`
- No raw `<button>` or `<input>` elements — use shadcn components

## UI Design Process

When designing UI features, create self-contained HTML mockup files for user review before writing production code.

- Self-contained HTML with all CSS inline (no external dependencies)
- Match the project's exact CSS custom properties (copy from `styles.css` — dark/light theme vars, brand colors, border radius, etc.)
- Use phone frames (375x720px) for mobile mockups, desktop frames for desktop
- Show before/after or multiple states side-by-side (e.g., collapsed vs expanded)
- Include a "Design Notes" section at the bottom with implementation details
- Upload as card attachments when working on the board
- Save to `.agents/temp/` as working files

## Collattice (Kanban)

See [[COLLATTICE]] for board conventions, lanes, labels, sizes, and workflow.

Releases are cut from `main` (trunk-based — see Branch strategy). Use `/release` to cut a new version — it waits for CI to pass on `main`, creates a GitHub Release, monitors the publish workflow, and reports when artifacts are ready.

## Definition of Done

Before opening a PR or declaring work complete:

```powershell
# 1. Build — backend
cd backend
dotnet build

# 2. Build — frontend (typecheck + Vite build)
cd frontend
npm run build

# 3. Test — backend
cd backend
dotnet test

# 4. Test — frontend
cd frontend
npm run test

# 5. Lint — frontend
cd frontend
npm run lint
npm run format:check
```

**Runtime observation:** Feature must be observable in the running application. Launch full stack via `dotnet run --project backend/Collabot.Collattice.AppHost`. Backend changes must respond correctly via API. Frontend changes must render in the browser. MCP changes must be callable and return expected results. Aspire Dashboard provides structured logs, traces, and metrics.

**Capability work defines its "done" upfront — and "done" is the demonstration, not green CI.** *(Standing rule, 2026-06-27.)* When a card or milestone delivers a **user-facing capability** — the operator or an integrator can now *do* something new (not a pure refactor, internal cleanup, or mechanical fix) — its Definition of Done includes a concrete, demonstrable **user-facing outcome, written at scope time in the spec/card and ratified by the operator before the work is dispatched.** "Done" then means that outcome has been *demonstrated in the running app* — CI green is necessary but not sufficient. For work split across front and back (or across milestones), the DoD names **where the integration is demonstrated**, so the UI-meets-new-backend step cannot fall in the seam between the halves. Origin: webhooks M2 shipped its backend event catalog "complete" while the admin UI exposed 2 of 22 events — "selectable" was verified at the backend and never demonstrated end-to-end, and the UI-exposure task lived in no card because the front/back split never named it.

Format with `dotnet format` (backend) and `npm run format` (frontend) before committing.

## Dispatching Work

Dispatch is coordinator scope (Cora). Detailed dispatch protocol — Pre-Dispatch Gate, model selection, token estimation, worktree discipline — lives in `~/.agents/roles/project-manager.md` (the role file) and project-anchored calibration lives in `.agents/agents/cora/LESSONS.md`. Project-canonical rules and the team report format are below.

### Dispatch rules (project-canonical)

- **Spec first.** Write specs to `.agents/specs/` before dispatching substantive work. No spec → no dispatch.
- **Full non-reconstructable context in the prompt.** The child has no memory of the parent session — include the coordinator's synthesis (the thread across cards, the operator's framing, the boundary, judgment guidance) and the boot-up the child can't reconstruct. Point at durable artifacts by reference (card #, PR #, spec path) — the child reads them itself. **Do not transcribe** card bodies, comments, or sibling-card summaries; **do not prescribe** mechanisms a domain bot owns (state what must be true, not how to prove it). Shape: task + boundary + non-reconstructable synthesis, then pointers. The failure mode this guards against is over-transcription — it narrows the bot to the coordinator's possibly-incomplete reading.
- **Ask, don't guess.** Every dispatch prompt includes: *"If you get stuck or unsure, report back rather than guessing."* Max 3 follow-up rounds per task before escalating to the operator.
- **Sub-agents auto-load convention skills via frontmatter.** Don't restate skill invocations or verification commands in dispatch prompts — restating an incomplete list narrows the bot's work and creates regressions. Reference the canonical doc when needed (*"run the full verification suite per CLAUDE.md § Definition of Done"*).
- **Spec / architecture / review dispatches default to Opus.** Implementation defaults to Opus on meatier work; Sonnet for narrow refactors and pattern-following tasks. State the model explicitly on every dispatch.

### Standardized report format

Every dispatched coding or evaluation sub-agent returns findings in this format:

```
## Report: <card or task title>

### Summary
<1-2 sentence verdict>

### Deliverable Status
| Deliverable | Status | Notes |
|---|---|---|
| <item> | Done / Partial / Missing | <detail> |

### Verification
- Backend build: <pass/fail/not run>
- Backend tests: <pass/fail/not run — include count>
- Frontend typecheck: <pass/fail/not run>
- Frontend lint: <pass/fail/not run>
- Frontend tests: <pass/fail/not run — include count>

### Files Touched
- <path> — <created/modified/read> — <what changed>

### Gaps & Issues
1. <issue description>

### Convention Violations
<list or "None">

### Recommendation
<next steps; open PR, stays in In Progress with PR link, returns to Ready, etc.>
```

## Named Agents

| Agent | Role | Specialty |
|---|---|---|
| **Cora** | Operations coordinator | Board management, dispatch, design facilitation, release lifecycle, conventions stewardship |

Bots sign commits with `Co-Authored-By: <Name> <name@collabot.dev>` — and only that; see § Git for the attribution rule. Cora's first session was 2026-05-09. As the team grows, additions land here.

## Path Conventions

- **Relative paths in committed docs and specs.** Never hardcode absolute paths in tracked files.
- **Absolute paths in scripts only** when referencing the script's own location.
- Reference sibling projects as `../<name>` (relative to repo root).

## Relationship to Other Projects

| Project | Path | Relationship |
|---|---|---|
| **Collabhost** | `../collabhost` | Peer project. Hosts the production deployment of Collattice (and itself). Its own work is tracked on the `collabhost` Collattice board. |
| **Ecosystem** | `../ecosystem` | Cross-project tooling, shared skills/conventions, and suite-wide scripts. Its own work is tracked on the `ecosystem` Collattice board. |
| **Scout** | `../scout` | Peer Collabhost-hosted app — the suite's web-tooling MCP (`fetch_*` / `search_*`); Collattice bots use it for web research. Its own work is tracked on the `scout` Collattice board. |

> **Retired (2026-06):** Collabot, Collabot TUI, Knowledge Base, and Research Lab were sunset in the suite consolidation — their repos are archived under `collab/archived/`, so the `../collabot`, `../collabot-tui`, `../kb`, and `../lab` paths no longer resolve. (Collabot was Collattice's original external MCP consumer.)

Cross-project work that spans Collattice + a peer (e.g., Collabhost) coordinates between named operations coordinators on each side (Cora here, Nolan on Collabhost). Externally-gated cards stay in Backlog with an explicit gate-and-trigger comment; the `Blocked` label is reserved for in-Triage gating.

## Skills

**Skill wins.** Universal craft (`dotnet-dev`, `typescript-dev`, `agent-workspace`, `collattice`) lives in user-scope skills. Convention skills auto-load for craft bots via frontmatter. Project-specific overrides are documented above; if you find a pattern in this codebase that disagrees with the skill, the default assumption is "the skill is right, the codebase is behind" — not "the codebase reflects an intentional choice." Land the fix when you touch the surface; don't fix the world in one PR.

**Conventions are mandatory, and reviews enforce them.** *(Standing rule, 2026-05-31.)* Two non-negotiables: (1) every PR conforms to the convention skill (`dotnet-dev` / `typescript-dev`) regardless of the surrounding code — non-conforming existing code is debt, not license to match it; (2) every code review includes an explicit convention check, and the **reviewer is accountable for convention misses alongside the author** — a convention that ships wrong is two failures, not one. **A convention violation surfaced in review is a required fold before merge — never classified as "optional," "nit-to-waive," or "author's discretion"** *(operator ruling, 2026-06-27)*; a MERGE-WITH-NITS verdict is fine, but any nit that is a convention violation gets folded, not waived. Spend the review attention on the **judgment tier**: the conventions no formatter can catch (LINQ indent stepping, method-chain stepping, blank-line "breathing room" between statements, naming, comment / XML-doc policy — see `dotnet-dev` / `typescript-dev` § Formatting). The **mechanical tier** (Allman braces, intra-line spacing, indentation, using-order) is already enforced by `dotnet format` + the CI format gate, so re-checking it by eye is wasted motion; a mechanical-tier violation reaching review on green CI signals a tooling-coverage gap to flag, not just a reviewer lapse. Full detail in `.agents/docs/ONBOARDING.md` § Coding standards.

Available on demand at session start: `dotnet-dev`, `typescript-dev`, `do-dotnet-backend-architecture`, `agent-workspace`, `collattice`, `use-github`, `aspire`, `tmux`. Invoke when relevant; bots that load convention skills via frontmatter will see them auto-injected.
