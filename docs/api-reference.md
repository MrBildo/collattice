# API Reference

All endpoints are under `/api/v1/`, with one exception: the card-detail read also has a second version at `GET /api/v2/cards/{id}` — see [Reading a card](#reading-a-card). Authentication is via the `X-User-Key` header.

## Boards

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | /boards | All | List all boards |
| GET | /boards/{idOrSlug} | All | Get board by ID or slug |
| POST | /boards | Admin | Create board |
| PATCH | /boards/{id} | Admin | Update board name |
| DELETE | /boards/{id} | Admin | Delete board (must have zero non-archive lanes) |

## Board-Scoped Resources

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | /boards/{boardId}/board | All | Composite: lanes + cards + sizes |
| GET | /boards/{boardId}/lanes | All | List lanes |
| POST | /boards/{boardId}/lanes | Admin | Create lane. Body `{ name, position? }` — omit `position` to append after the board's last lane; a taken position returns 409; 400 when `position` is the reserved `int.MaxValue`, or when it is omitted and no position is free after the last lane |
| POST | /boards/{boardId}/lanes/reorder | Admin | Reorder all non-archive lanes. Body `{ laneIds }` — complete desired order; all-or-nothing |
| GET | /boards/{boardId}/cards | All | List cards (enriched: labels, sizes, comment/attachment counts). Returns a `{ items, totalCount, offset, limit }` paged envelope. Query params: `since`, `labelId`, `laneId`, `search`, `includeArchived` (default `false`), `offset` (default `0`), `limit` |
| POST | /boards/{boardId}/cards | All | Create card. When many cards are being created on one board at the same moment, a create that cannot get a card number returns 409 with a "try again" message and saves nothing; retry it |
| GET | /boards/{boardId}/sizes | All | List card sizes (ordered by ordinal) |
| POST | /boards/{boardId}/sizes | Admin | Create size. Body `{ name, ordinal? }` — omit `ordinal` to place it one past the board's highest; a taken ordinal or name returns 409; 400 when `ordinal` is omitted and no ordinal is free after the highest |
| GET | /boards/{boardId}/labels | All | List labels for a board |
| POST | /boards/{boardId}/labels | Admin | Create label |
| PATCH | /boards/{boardId}/labels/{id} | Admin | Update label name/color. A name another label on the board holds → 409, nothing saved |
| DELETE | /boards/{boardId}/labels/{id} | Admin | Delete label + cleanup card assignments |

## By-ID Operations

| Resource | Endpoints |
|----------|-----------|
| Lanes | `GET /lanes/{id}`, `PATCH /lanes/{id}` (name/position; a position another lane on the board holds → 409, nothing saved), `DELETE /lanes/{id}` |
| Sizes | `GET /sizes/{id}`, `PATCH /sizes/{id}` (name/ordinal; a name or ordinal another size on the board holds → 409, nothing saved), `DELETE /sizes/{id}` (blocked if in use) |
| Cards | `GET /cards/{id}` (enriched detail; comments as a plain array — **deprecated** in favour of `GET /api/v2/cards/{id}`; see [Reading a card](#reading-a-card); includes `descriptionHistoryCount`, see [Card History](#card-history)), `GET /api/v2/cards/{id}` (the recommended read — field projection + paged comments; see [Reading a card](#reading-a-card)), `PATCH /cards/{id}` (a description edit can carry a [collision notice](#collision-awareness)), `DELETE /cards/{id}`, `POST /cards/{id}/reorder`, `POST /cards/{id}/archive`, `POST /cards/{id}/restore` |
| Card history | `GET /cards/{id}/history` — the card's description edit trail; see [Card History](#card-history) |

### Reading a card

There are two versions of the card-detail read. Both return the enriched card detail — the card's own fields, its `sizeName`, the creator and last-editor display names, its labels and attachment metadata, `isArchived`, `descriptionHistoryCount`, and per-comment `createdAtUtc` (its stamped-once posting time) beside `lastUpdatedAtUtc` (bumped on every edit) — and both accept the `includeDescription` projection. They differ only in how the comment thread is shaped, and which one is recommended.

**Sparse versioning:** `v2` exists **only** for this one endpoint. Every other endpoint in this reference stays `v1`; there is no full-surface `v2` alias.

#### `GET /api/v2/cards/{id}` — the recommended read

The card detail with field projection and the comment thread as a **paged sub-envelope**. Reach for this in any new integration.

| Param | Values | Default | Notes |
|---|---|---|---|
| `includeDescription` | `true` \| `false` | `true` | Pass `false` to omit the description body — the single largest field on a heavy card — when you only need metadata or comments. Every other field, including `descriptionHistoryCount`, is unaffected. |
| `commentsOffset` | integer | `0` | Comments to skip, counting back from the newest. Negative values clamp to `0`. |
| `commentsLimit` | integer | *(all)* | Page size for comments, newest activity first. **Omitted returns the whole thread, still enveloped** — a browser client is not paying an agent's per-token cost. A given value clamps to `1..200`; `0` omits comment bodies and returns only the count. |

`comments` comes back as `{ items, totalCount, offset, limit }`, **newest activity first** (page 0 is the freshest). `totalCount` is the whole thread regardless of the page, so a capped read is never mistaken for the whole. Offset paging runs over `lastUpdatedAtUtc`, which is bumped when a comment is edited, so a comment edited concurrently with a paged walk can shift between pages — the usual offset-paging caveat when the sort key is mutable; comment id breaks ties on the key, so a page is otherwise stable.

#### `GET /cards/{id}` — deprecated

The `v1` read returns `comments` as a **plain array** — the whole thread, **oldest activity first** — the shape prior releases served, so a client written against an earlier release deserializes it unchanged. It carries the same additive `createdAtUtc`, `descriptionHistoryCount`, and `includeDescription` projection as `v2`, and it takes **no** comment-paging parameters — `commentsOffset` and `commentsLimit` live only on `v2`.

This resource is **deprecated** in favour of `GET /api/v2/cards/{id}`. Every response — success **and** `404` — carries a `Deprecation` header and a `Link` header with `rel="successor-version"` pointing at the `v2` URL. There is no `Sunset` header yet: removal is planned for a future major release, and no date is set. Until then the endpoint keeps working exactly as it does now. Nothing else in `v1` is deprecated.

To read comments on their own — untouched by this deprecation — use `GET /cards/{id}/comments` (below); that endpoint was not changed.

## Users

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | /users | All | List users |
| GET | /users/{id} | All | Get user |
| POST | /users | Admin | Create user |
| PATCH | /users/{id} | Admin | Update user |
| PATCH | /users/{id}/deactivate | Admin | Deactivate user |
| GET | /auth/me | All | Current user info |

## Comments, Attachments, Card Labels

| Resource | Endpoints |
|----------|-----------|
| Card Labels | `GET /cards/{id}/labels`, `POST /cards/{id}/labels` (validates same board; 409 if the label is already on the card, also when another caller added it at the same moment), `DELETE /cards/{id}/labels/{labelId}` (404 if the label is not on the card, also when another caller removed it at the same moment) |
| Comments | `GET /cards/{id}/comments`, `POST /cards/{id}/comments`, `PATCH /comments/{id}`, `DELETE /comments/{id}` |
| Attachments | `GET /cards/{id}/attachments`, `POST /cards/{id}/attachments` (5 MB via MCP / 50 MB via REST), `GET /attachments/{id}`, `DELETE /attachments/{id}` |

## Card History

Editing a card's description is recorded. `PATCH /cards/{id}` — and the MCP `update_card` tool, which shares the same capture — preserve the value they replace, so every version of a description stays recoverable along with who replaced it and when. The card mutation and its history entry commit together: a description can never replace an unrecorded one.

Only the **description** is recorded today. The store is field-general (the `field` parameter exists for it), but no other field is captured yet.

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | /cards/{id}/history | All | The card's description version trail, newest first. `404` if the card does not exist. |

Reading history needs no permission beyond reading the card — any authenticated user who can read a card can read its history.

### Knowing whether there is any history, without asking for it

`GET /cards/{id}` carries **`descriptionHistoryCount`**: how many recorded revisions this card's description has. It is the same number the trail reports as its `totalCount`, so a client can decide whether a history affordance is worth offering without spending a call to find out the trail is empty — which it is for every card that has not been description-edited since recording began.

It is `0` or at least `2`, never `1`: a card's first edit records two revisions, the value that was already there and the value that replaced it. It counts the **description** specifically rather than history in general, because the store records other fields as soon as one is lit up and a count that would have to change meaning then would be misleading now.

### Query parameters

| Param | Values | Default | Notes |
|---|---|---|---|
| `field` | `description` | `description` | Which field's trail to return; case-insensitive. An unrecognized field is a `400`, not an empty trail — on an audit surface a typo must not read as "this card has no history". |
| `format` | `diff` \| `full` \| `both` | `both` | Whether each entry carries the unified diff of what that edit changed, the full value at that revision, or both. Case-insensitive; an unrecognized value is a `400`. (The MCP tool defaults to `diff` instead — see the [MCP skill](collattice/SKILL.md).) |
| `from`, `to` | revision numbers | — | Supply **both** to compare two arbitrary revisions instead of walking the trail; the response is a different shape (below). One without the other is a `400`, as is a revision this card's trail does not have. |
| `offset` | integer | `0` | Revisions to skip, counting back from the newest. Negative values clamp to `0`. |
| `limit` | integer | *(none)* | Maximum revisions to return, `1`–`200`; values outside that range clamp into it. **Omit it to get the whole trail** — that is what a caller written before paging existed sees. |

`offset` and `limit` apply to the trail only. Sending either alongside `from`/`to` is a `400`: a pair comparison answers with a single object, and there is no page to take of it.

### Trail response

```json
{
  "cardId": "a1b2c3d4-5e6f-7a8b-9c0d-1e2f3a4b5c6d",
  "field": "description",
  "entries": [
    {
      "revision": 3,
      "editedByUserId": "52df8c11-2c9a-4d1e-8b3f-7a6e5d4c3b2a",
      "editedByName": "Bill Wheelock",
      "editedAtUtc": "2026-07-23T20:41:12.7731840+00:00",
      "value": "the full description text at revision 3",
      "diff": "@@ -1,2 +1,3 @@\n alpha\n gamma\n+delta\n"
    },
    {
      "revision": 2,
      "editedByUserId": "b4261f19-f9dd-4231-9b3f-1b01e725cb96",
      "editedByName": "Agent Bot",
      "editedAtUtc": "2026-07-23T20:39:03.1120040+00:00",
      "value": "the full description text at revision 2",
      "diff": "@@ -1,2 +1,2 @@\n alpha\n-beta\n+gamma\n"
    },
    {
      "revision": 1,
      "editedByUserId": null,
      "editedByName": null,
      "editedAtUtc": null,
      "inferredEditor": {
        "userId": "52df8c11-2c9a-4d1e-8b3f-7a6e5d4c3b2a",
        "name": "Bill Wheelock",
        "basis": "creator"
      },
      "value": "the description as it stood when recording began",
      "diff": ""
    }
  ],
  "totalCount": 3,
  "offset": 0,
  "limit": null
}
```

- `entries` is ordered **newest first**. Each entry is a *version* of the text, not an edit delta — so the newest entry's `value` is the card's current description, and `diff` answers "what did this edit change?".
- `revision` is a monotonic integer starting at 1, unique within a card and field. It is the addressing scheme `from`/`to` uses.
- **The oldest revision carries a `null` author and timestamp**, and only the oldest. History is not back-filled, so revision 1 holds whatever the description said when recording began — nobody observed it being written, and an audit trail should not attribute a value to someone who may not have written it. Every later revision is fully attributed. Render this case explicitly, not as an empty name or an invalid date.
- **The oldest revision also carries `inferredEditor`** — `{ userId, name, basis }`, the best available attribution for that unobserved text. `basis` says how it was inferred: `"creator"` means the card's creator. It is an inference, not an observation: a card created before history was recorded may have been edited by someone else before recording began. Keep the two apart when you render it — the history panel shows *"original version — Bill Wheelock (card creator)"*. `inferredEditor` is absent (not `null`) on every observed revision, so its presence is itself the signal that the name is inferred. There is no inferred timestamp: the card's creation time is on the card, but it is not necessarily when this text was written. Treat an unfamiliar `basis` value as a generic inference rather than an error; more may be added.
- The oldest revision's `diff` is `""` — an empty string, never `null`. There is nothing older to compare it against. **Only the oldest revision has an empty diff**, so an empty diff is a reliable test for "this is the start of the record": no revision is ever recorded holding the same text as the one before it, including when two people save the same wording at the same moment.
- `editedAtUtc` is stamped when the revision is recorded, not when the request arrived, so **timestamps never decrease as `revision` increases**. Where the two could disagree — two stamps can land on the same clock tick — **`revision` is the authority**; sort by it, not by time.
- `value` and `diff` are **omitted from the JSON entirely** (not serialized as `null`) when the requested `format` does not include them, so `format=diff` carries no wasted padding.
- `totalCount` is the length of the **whole** trail regardless of paging, so `entries.length < totalCount` is how you tell there is more to fetch. `offset` and `limit` echo what was applied; `limit` is `null` when none was.
- **Pages are taken from the newest end**, so `offset=0` is the most recent revisions and walking the trail means increasing `offset`. A revision's `diff` is the same whether it arrives on a page or in the whole trail — the entry at a page's oldest edge is still diffed against the revision before it, even though that revision is not on the page.
- The trail comes back whole when no `limit` is given, and the REST default `format=both` carries every full version *and* every diff. On a heavily edited card that is the largest response this API produces; pass a `limit`, or ask for `format=diff` (or `full`) when you do not need both halves.

### Pair response

Supplying both `from` and `to` returns a single object rather than a list:

```json
{
  "cardId": "a1b2c3d4-5e6f-7a8b-9c0d-1e2f3a4b5c6d",
  "field": "description",
  "from": 1,
  "to": 3,
  "diff": "@@ -1,1 +1,1 @@\n-one\n+three\n",
  "fromValue": "one",
  "toValue": "three"
}
```

`diff`, `fromValue` and `toValue` are gated by `format` the same way the trail's `diff` and `value` are. The revisions are compared in the order given, so `from=3&to=1` returns the diff that would undo the change rather than an error.

### The diff format

Diffs are computed on read and rendered as a **git-style unified diff**: hunks only, no `---`/`+++` file headers (the envelope already names what is being compared), and `\n` line endings on every host.

```
@@ -1,2 +1,3 @@
 alpha
 gamma
+delta
```

- A line starting `@@ ` is a hunk header; `+` marks an addition, `-` a removal, and a single leading space unchanged context. The prefix is one character and is not part of the line's text.
- Three lines of context surround each change. Nearby changes merge into one hunk; distant ones produce several `@@` hunks in the same string.
- Empty ranges follow git's convention — a description that started empty diffs as `@@ -0,0 +1,4 @@`.
- Ranges always carry an explicit count, including single-line ranges that git abbreviates: this renderer emits `@@ -1,1 +1,1 @@` where git would write `@@ -1 +1 @@`. Both forms are valid unified diff and every reader accepts them.

### What does and does not accrue

- **No back-fill.** A trail begins at a card's first description edit after this feature shipped; the value in place at that moment is preserved as revision 1. A card whose description has never been edited returns `entries: []`, and its current text remains available from `GET /cards/{id}`.
- **No-op edits record nothing.** Saving a description identical to the current one adds no revision — including a `PATCH` that changes the lane or labels while carrying an unchanged description.
- **Simultaneous edits both land, and leave the trail two people editing one after the other would have left.** Two saves racing each other are recorded as two attributed revisions in the order they committed; neither request is rejected and no conflict response exists to handle. The one exception is heavy contention: an edit that loses the race for its revision on every attempt (many edits to one description at the same instant) returns 409 with a "try again" message and saves nothing; retry it. If they happen to set the *same* text, the second one records nothing, exactly as it would have if it had arrived a minute later. Note this is not lost-update protection: the card's text is still last-one-wins, as it was before history existed — a write can, however, tell you *after the fact* when it overwrote someone; see [Collision awareness](#collision-awareness).
- **Archived cards are frozen.** Their descriptions cannot be edited (`400`), so no history accrues; whatever they already have stays readable.
- **Retention is unbounded.** History is never pruned — that is the point of an audit trail. It is deleted only with the card.
- A description edit fires the existing `card.updated` webhook event; recording history adds no new event type.

## Collision awareness

Last write wins on a card, so two people editing the same one can still overwrite each other — the behaviour described under [Card History](#card-history). What a write can now tell you is whether it landed on top of someone else's edit, so a caller — an automated one especially — knows it overwrote a change instead of discovering it later. It never changes the outcome: the save always succeeds and last-write-wins is unchanged. There is no conflict status and nothing to retry.

`PATCH /cards/{id}` — and the MCP `update_card` tool, which answers identically — carries an optional **`collision`** object beside the updated card when a description edit overlapped another user's:

```json
{
  "kind": "exact",
  "field": "description",
  "actor": { "userId": "52df8c11-2c9a-4d1e-8b3f-7a6e5d4c3b2a", "name": "Bill Wheelock" }
}
```

When the write overlapped nothing, the `collision` field is **absent** entirely (not `null`).

### Exact — pass back the revision you read

Read `descriptionHistoryCount` from `GET /cards/{id}` before you edit, then pass it back as **`expectedDescriptionRevision`** in the `PATCH` body. If the description moved past that revision between your read and your write, someone edited it in the meantime — a definite overwrite — and `collision` comes back with `kind: "exact"`, `field: "description"`, and `actor` naming whoever you landed on top of. If nothing changed in between, there is no `collision`. Because it compares the exact revision you read, an intervening edit is reported however long ago the read was; the exact answer carries no time window.

### Approximate — the best-effort fallback

Omit `expectedDescriptionRevision` and you still get a best-effort signal. If someone **else** edited the card within a deliberately short window — ten seconds — just before your write, `collision` comes back with `kind: "approximate"` and `field: null`. Without a baseline there is no way to say which field the other editor touched or whether your write truly replaced theirs, only that another editor was active on this card at about the same time — so the approximate signal is card-level and names no field. The window is short on purpose: it is meant to catch "someone was working this card at the same time as you," not ordinary sequential editing where one person picks up minutes after another left off.

### Scope and confinement

Only the **description** is checked today, because it is the only field with a recorded history to compare a baseline against (see [Card History](#card-history)). The mechanism is field-general — as other fields gain history the same `collision` shape extends to them with no change to its form.

`collision` is **additive and non-breaking**: it appears only on the two write responses (this `PATCH` and MCP `update_card`), beside the card's usual fields, so a consumer already reading the update response keeps working unchanged and simply gains a field when one is present. It is deliberately built into those two responses alone and never into the shared card summary that feeds card lists, [search](#search), and [webhook](#webhooks) payloads, so it cannot appear on any of those surfaces.

## Writes that race a delete

A write checks that the card, lane, label or size it refers to exists before it saves. If that thing is deleted in the moment between the check and the save — a comment posted as its card is deleted, a card created into a lane as the lane is deleted — the write answers **`409 Conflict`** with the message `"Something this change refers to was changed or deleted at the same moment. Reload and try again."` The failed save is not applied. Retrying gets the usual precise answer, normally a `404` because the thing is now gone. MCP tools answer the same case with that message prefixed `Error: `. This applies to every write, REST and MCP alike.

The three deletes that are refused while something depends on the row — a lane that still has cards, a size a card uses, a board that still has lanes — make that check part of the delete itself. A card or lane that lands at the same moment is never removed along with its parent; the delete answers as it would have if the card or lane had been there first (`409 "Lane must be empty."`, `409 "Size is in use by cards."`, `400 "Board must have no lanes before it can be deleted."`).

## Search

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | /search/cards?q=&limit= | All | Global cross-board search. Returns results grouped by board. Default limit 20, max 50. |

Search supports:
- Free text — matches card name and description (case-insensitive)
- Card number — prefix with `#` (e.g. `#37`) for exact match
- Plain number — matches card number OR text content

## SSE Events

| Path | Notes |
|------|-------|
| /boards/{boardId}/events | Per-board stream; mutations broadcast to all connected clients |

## MCP Endpoint

| Path | Notes |
|------|-------|
| /mcp | Streamable HTTP transport — 46 tools (boards, cards, card history, lanes, sizes, labels, comments, attachments, archive, bulk operations, search, prune, webhooks) |

For the full agent-facing tool reference — connecting a client, every tool, the board model, and the identifier rules — see the [MCP skill](collattice/SKILL.md), a drop-in `SKILL.md` you can add to an agent harness rather than writing your own from the tool schemas.

## Webhooks

Collattice can POST a structured event to a URL of your choice whenever something happens on a board — a card created, moved, or labeled; a comment posted; a lane reordered; and more, across the [board-event catalog](#event-types) — so an external consumer (a workflow tool, a script, an agent) can react to board activity without polling. Delivery targets are managed as **subscriptions** — you can register more than one, each with its own URL, an optional signing secret, an enabled state, and a selection of which events it wants. For a guided walkthrough — creating a subscription, sending a test delivery, and the recursion guard you need before pointing one at anything that creates cards — see the [Webhooks Integration Guide](integrating-webhooks.md). For the global delivery settings (master switch, timeout, retries, and the private-network security control), see [Host Configuration](../README.md#webhooks).

Every webhook endpoint below requires an administrator-level key — either the **Administrator** or the **AgentAdministrator** role. A request from any other role receives `403`.

### Managing subscriptions

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | /webhooks/subscriptions | Administrator / AgentAdministrator | List all subscriptions, each with on-read delivery metrics. Never returns secrets. |
| GET | /webhooks/subscriptions/{id} | Administrator / AgentAdministrator | One subscription. `404` if it doesn't exist. |
| POST | /webhooks/subscriptions | Administrator / AgentAdministrator | Create a subscription (body below). Returns the created subscription (`201`). |
| PATCH | /webhooks/subscriptions/{id} | Administrator / AgentAdministrator | Update a subscription; any omitted field is left unchanged (body below). |
| DELETE | /webhooks/subscriptions/{id} | Administrator / AgentAdministrator | Delete a subscription. Returns `{ "deleted": true }`. Its delivery-log history is kept. |
| POST | /webhooks/subscriptions/{id}/test | Administrator / AgentAdministrator | Send a synchronous test delivery (a `webhook.ping`) to this subscription and return the outcome. |

**Create body** (`POST /webhooks/subscriptions`):

```json
{
  "url": "https://automation.example.com/collattice-hook",
  "events": ["card.created", "card.moved"],
  "secret": "a-long-random-shared-secret",
  "enabled": true,
  "name": "automation prod"
}
```

- `url` (required) — the dial-out target. Must be `http`/`https`, and must not resolve to a blocked private or internal address unless `Webhooks:AllowPrivateNetworkTargets` is set (see [Host Configuration](../README.md#webhooks)).
- `events` (required, non-empty) — the event types this subscription receives. Use the exact event-type strings (`card.created`, `card.moved`) or the single wildcard `"*"` to receive every event type, including ones added in future versions. An empty list is rejected; an unknown event type is rejected with the list of valid ones.
- `secret` (optional) — the HMAC signing key. **Write-only**: accepted here, never returned by any read. When set, this subscription's deliveries are signed (see [Signing](#signing)).
- `enabled` (optional, default `true`) — whether the subscription delivers.
- `name` (optional) — a label for your own reference.

**Update body** (`PATCH /webhooks/subscriptions/{id}`) — the same fields, all optional; an omitted field is unchanged. The secret follows a set / keep / clear rule:

- omit `secret` → the secret is **kept** unchanged (so you can edit the URL without re-sending the secret);
- `"secret": "new-value"` → the secret is **replaced**;
- `"clearSecret": true` → the secret is **removed**, and the subscription goes unsigned.

**Subscription shape** (returned by the list, get, create, and update endpoints):

```json
{
  "id": "8f1c…",
  "name": "automation prod",
  "url": "https://automation.example.com/collattice-hook",
  "enabled": true,
  "events": ["card.created", "card.moved"],
  "signed": true,
  "successCount": 142,
  "failureCount": 3,
  "lastDeliveryStatus": "Succeeded",
  "lastDeliveryAtUtc": "2026-06-25T16:42:25.770Z"
}
```

The secret never appears — `signed` is `true` when a secret is set, `false` otherwise. The metric fields (`successCount`, `failureCount`, `lastDeliveryStatus`, `lastDeliveryAtUtc`) are computed from the delivery log at read time; a brand-new subscription reports zeros and nulls.

**Test delivery** (`POST /webhooks/subscriptions/{id}/test`) sends one `webhook.ping` event to the subscription through the exact same delivery path as a real event — same private-network guard, same signing — and returns the outcome directly:

```json
{ "success": true, "statusCode": 200, "error": null }
```

It is synchronous (you get the result of the one attempt, with no retry) and it records a row in the delivery log like any other delivery. A blocked or unreachable target comes back with `"success": false` and the reason in `error`.

### Observability endpoints

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | /webhooks/deliveries | Administrator / AgentAdministrator | Persisted delivery attempts, newest first. Query params: `boardId` (filter to one board), `subscriptionId` (filter to one subscription), `offset` (default 0), `limit` (default 50, max 200). Returns a `PagedResult`. |
| GET | /webhooks/status | Administrator / AgentAdministrator | Global delivery posture and subscription counts — booleans and numbers only, never a secret or a URL. Answers "is delivery on, are private targets allowed, how many subscriptions exist?" when the delivery log is empty. |
| GET | /webhooks/event-types | Administrator / AgentAdministrator | The full selectable [event catalog](#event-types) with display metadata, grouped by family: `[{ family, label, events: [{ type, label, description }] }]`. The server-side source of truth a selection UI consumes, so it can never drift from what the backend emits and accepts. |

Each `deliveries` item:

```json
{
  "id": "3a2b…",
  "subscriptionId": "8f1c…",
  "eventId": "01J9ZQK8H6F4N3M2P7R5T8V0XW",
  "eventType": "card.created",
  "boardId": "f6fa6794-4bed-44d0-9656-de8080791302",
  "attempt": 1,
  "status": "Succeeded",
  "httpStatusCode": 200,
  "error": null,
  "attemptedAtUtc": "2026-06-25T16:42:25.770Z"
}
```

`subscriptionId` identifies which subscription the attempt was for; it is `null` for an attempt whose subscription was later deleted (the log outlives the subscription). `status` is `Succeeded` or `Failed`; `httpStatusCode` and `error` are populated on a failed attempt where there was a response or an error to record.

`GET /webhooks/status` response:

```json
{
  "enabled": true,
  "allowPrivateNetworkTargets": false,
  "subscriptionCount": 3,
  "enabledSubscriptionCount": 2
}
```

- `enabled` — the `Webhooks:Enabled` global master switch. When `false`, no subscription delivers.
- `allowPrivateNetworkTargets` — whether the `Webhooks:AllowPrivateNetworkTargets` override is on (see [Host Configuration](../README.md#webhooks)).
- `subscriptionCount` — how many subscriptions are registered.
- `enabledSubscriptionCount` — how many of those are individually enabled.

### Event types

A subscription receives an event only when its `events` selection includes that event type (or the wildcard `"*"`). Collattice emits an event catalog covering the board-scoped lifecycle, grouped into families. The same catalog — with display labels and descriptions for a selection UI — is served by `GET /webhooks/event-types`.

| Family | Event | Fires when |
|--------|-------|------------|
| Cards | `card.created` | A card first comes into existence — via REST `POST /boards/{boardId}/cards`, MCP `create_card` or `duplicate_card` (a copy of an existing card is a new card), or when an interactive draft card is finalized. A draft (temp) card does **not** fire until it is finalized. |
| | `card.moved` | A card moves to a different lane **or position** through any successful non-archive mutation — the dedicated reorder/move paths, a `PATCH /cards/{id}` that sets `laneId` or `position`, an `update_card` that sets `laneId`, or `bulk_update_cards` with `laneId` (one event per card in the batch whose saved lane or position differs from where it sat before the call — the batch saves once, so `from` is the card's placement before the call and `to` its placement after; a card outside the batch does not report even when the batch renumbered it, and a batch that leaves every card where it was fires none). A within-lane move carries equal `from`/`to` lane ids. For `PATCH /cards/{id}` and `update_card` the rule is where the card actually lands, however the request expressed it: re-sending the card's current lane still fires `card.moved` when that re-positions the card (a `PATCH` with the current `laneId` and no `position` re-appends the card to the end of its lane), and a request that leaves the card exactly where it was fires none. Archiving and restoring do **not** fire `card.moved` (they fire `card.archived` / `card.restored`). |
| | `card.updated` | A card's name, description, or size changes. |
| | `card.archived` | A card is archived. |
| | `card.restored` | A card is restored from the archive. |
| | `card.deleted` | A card is hard-deleted (irreversible) — via REST `DELETE /cards/{id}` (an archived card can be deleted too) or the admin prune with `action: "delete"` (one event per deleted card). Distinct from `card.archived`, which is the reversible "left the board" transition. Deleting a whole board does not fire per-card `card.deleted` — its archived cards are covered by `board.deleted`. |
| | `card.labeled` | A label is added to a card (one event per label). |
| | `card.unlabeled` | A label is removed from a card (one event per label). |
| Comments | `comment.created` | A comment is added to a card. |
| | `comment.updated` | A comment is edited. |
| | `comment.deleted` | A comment is deleted. |
| Labels | `label.created` | A label is created on a board. |
| | `label.updated` | A label is renamed or recolored. |
| | `label.deleted` | A label is deleted from a board. |
| Attachments | `attachment.created` | An attachment is added to a card. |
| | `attachment.deleted` | An attachment is removed from a card. |
| Lanes | `lane.created` | A lane is created on a board. |
| | `lane.renamed` | A lane is renamed. |
| | `lane.reordered` | A board's lanes are reordered. |
| | `lane.deleted` | A lane is deleted from a board. |
| Sizes | `size.created` | A card size is created on a board. |
| | `size.renamed` | A card size is renamed. |
| | `size.reordered` | A board's card sizes are reordered. |
| | `size.deleted` | A card size is deleted from a board. |
| Boards | `board.created` | A board is created. |
| | `board.renamed` | A board is renamed. |
| | `board.deleted` | A board is deleted. |

Two coverage rules are worth knowing:

- **Multi-axis edits co-fire.** A single `PATCH /cards/{id}` / `update_card` / `bulk_update_cards` that changes more than one thing emits one event per axis — a call that renames a card **and** moves it **and** changes its labels emits `card.updated` **+** `card.moved` **+** `card.labeled` / `card.unlabeled`. An unchanged axis emits nothing.
- **One `lane.reordered` / `size.reordered` per reorder.** Reordering a board's lanes emits a single `lane.reordered` carrying the board's full new order — never one event per lane. Both the bulk reorder and a single-lane position move emit this same shape. Card sizes behave identically: a reorder emits one `size.reordered` with the board's full new size order.

`webhook.ping` is a separate event type that exists only for the [test-delivery endpoint](#managing-subscriptions): board activity never produces it, and a subscription can't select it.

### The envelope

Every event is a JSON object with a shared envelope and a per-event `data` block. The body is camelCase, byte-identical in field naming to the REST API. A full `card.created` body:

```json
{
  "event": "card.created",
  "eventId": "01J9ZQK8H6F4N3M2P7R5T8V0XW",
  "occurredAt": "2026-06-18T16:42:25.770Z",
  "version": "1",
  "boardId": "f6fa6794-4bed-44d0-9656-de8080791302",
  "boardSlug": "collattice",
  "actor": { "userId": "52df8c11-2c9a-4d1e-8b3f-7a6e5d4c3b2a", "name": "Bill Wheelock", "role": "Administrator" },
  "data": {
    "card": {
      "id": "a1b2c3d4-5e6f-7a8b-9c0d-1e2f3a4b5c6d",
      "number": 321,
      "name": "Investigate flaky test",
      "descriptionMarkdown": "Repro steps...",
      "sizeId": "e5f6a7b8-1234-5678-9abc-def012345678",
      "sizeName": "M",
      "laneId": "b7c8d9e0-1111-2222-3333-444455556666",
      "position": 0,
      "createdByUserId": "52df8c11-2c9a-4d1e-8b3f-7a6e5d4c3b2a",
      "createdAtUtc": "2026-06-18T16:42:25.770Z",
      "lastUpdatedByUserId": "52df8c11-2c9a-4d1e-8b3f-7a6e5d4c3b2a",
      "lastUpdatedAtUtc": "2026-06-18T16:42:25.770Z",
      "labels": [],
      "commentCount": 0,
      "attachmentCount": 0,
      "isArchived": false,
      "latestComment": null
    },
    "laneName": "Inbox"
  }
}
```

| Field | Type | Notes |
|-------|------|-------|
| `event` | string | The event type — one of the catalog values above (e.g. `card.created`), or `webhook.ping` for a test delivery. |
| `eventId` | string (ULID) | Unique per event. Use it to deduplicate: delivery is at-least-once, so the same `eventId` may arrive more than once (on a retry). |
| `occurredAt` | string (ISO-8601 UTC) | When the fact happened, server-side. Sort on this for ordering — events may arrive out of order. |
| `version` | string | Contract version, currently `"1"`. New fields may be added within a version; a consumer must ignore unknown fields rather than reject the payload. |
| `boardId` | string (GUID) | The board the card belongs to. |
| `boardSlug` | string | The board's slug (e.g. `collattice`) — the human-readable key to filter on without a lookup. |
| `actor` | object | Who caused the event: `{ userId, name, role }`. `role` is the role **name** (`Administrator`, `HumanUser`, `AgentUser`, `AgentAdministrator`), not a number. This is the field a consumer filters on to avoid an automation loop — see the [integration guide](integrating-webhooks.md). |
| `data` | object | The per-event payload — see below. |

The `data` block differs per family. The envelope above is identical for every event; only `data` changes. Each shape below is what rides under `data`.

#### Card events

The card family embeds the full card (the same `CardSummary` shape the REST card endpoints return) under `card`, plus the resolved `laneName` — `CardSummary` is GUID-keyed and carries no lane name, so the event resolves it for you.

- **`card.created`, `card.updated`, `card.archived`, `card.restored`, `card.deleted`** — `{ card, laneName }`. The card reflects state *at the moment of the event*: a freshly created card has `commentCount: 0`, `attachmentCount: 0`, and `latestComment: null` — correct, not a missing value. `card.archived` / `card.restored` carry the card as it sits in the archive lane (or its restored target lane). `card.deleted` carries the card as it was just before removal (fully enriched — its labels and counts are the state at deletion); if the deleted card was archived, its `laneId` is omitted (the internal archive-lane GUID), exactly as `card.archived` does, with `isArchived: true` and `laneName` carrying the state.
- **`card.moved`** — `{ card, laneName, from, to }`, with the transition:

  ```json
  "data": {
    "card": { /* the full card, now in the target lane */ },
    "laneName": "Ready",
    "from": { "laneId": "...", "laneName": "Inbox", "position": 3 },
    "to":   { "laneId": "...", "laneName": "Ready", "position": 0 }
  }
  ```

  `card.moved` covers both a cross-lane move and a within-lane position change. `from`/`to` carry both the lane (id + name) and the position the card left and landed at — for a within-lane move the two `laneId`s are equal and only the position differs.
- **`card.labeled`, `card.unlabeled`** — `{ card, laneName, label }`, where `label` is `{ id, name, color }` (color is nullable). The label that was added or removed is embedded so a consumer knows *which* one without a second fetch. One event per label.

#### Comment events

**`comment.created`, `comment.updated`, `comment.deleted`** — `{ comment, card }`:

```json
"data": {
  "comment": {
    "id": "…", "cardId": "…", "cardNumber": 321,
    "contentMarkdown": "Looks good, shipping.",
    "authorUserId": "…", "authorName": "Bill Wheelock",
    "lastUpdatedAtUtc": "2026-06-18T16:42:25.770Z"
  },
  "card": { "id": "…", "number": 321 }
}
```

The comment is the changed resource; the card rides as a thin `{ id, number }` ref. `authorUserId` / `authorName` are the comment's **own** author — when an admin edits or deletes someone else's comment, the editor is the envelope `actor` while the author stays the comment's author. `comment.deleted` carries the comment's state at occurrence (the row is gone after).

#### Label events

**`label.created`, `label.updated`, `label.deleted`** — `{ label }`, where `label` is `{ id, boardId, name, color }` (color is nullable). This is the label-*resource* lifecycle on a board — distinct from `card.labeled` / `card.unlabeled`, which report a *card's* label-set changing.

#### Attachment events

**`attachment.created`, `attachment.deleted`** — `{ attachment, card }`:

```json
"data": {
  "attachment": {
    "id": "…", "cardId": "…",
    "fileName": "screenshot.png", "contentType": "image/png",
    "sizeBytes": 51234,
    "addedByUserId": "…", "addedAtUtc": "2026-06-18T16:42:25.770Z"
  },
  "card": { "id": "…", "number": 321 }
}
```

**Metadata only — the file bytes never ride the wire.** `sizeBytes` is the stored payload length. `attachment.deleted` carries the metadata at occurrence (the row is gone after).

#### Lane events

- **`lane.created`, `lane.renamed`, `lane.deleted`** — `{ lane }`, where `lane` is `{ id, boardId, name, position }`. `lane.deleted` carries the lane's state at occurrence.
- **`lane.reordered`** — `{ lanes }`, the board's **full new left-to-right order**, each entry `{ id, name, position }`:

  ```json
  "data": {
    "lanes": [
      { "id": "…", "name": "Backlog", "position": 0 },
      { "id": "…", "name": "In Progress", "position": 1 },
      { "id": "…", "name": "Done", "position": 2 }
    ]
  }
  ```

  Both the bulk reorder and a single-lane position move emit this same full-order shape, so a consumer gets the resulting order directly with no reconstruction.

#### Size events

- **`size.created`, `size.renamed`, `size.deleted`** — `{ size }`, where `size` is `{ id, boardId, name, ordinal }`. `size.deleted` carries the size's state at occurrence.
- **`size.reordered`** — `{ sizes }`, the board's **full new order**, each entry `{ id, name, ordinal }`:

  ```json
  "data": {
    "sizes": [
      { "id": "…", "name": "S", "ordinal": 0 },
      { "id": "…", "name": "M", "ordinal": 1 },
      { "id": "…", "name": "L", "ordinal": 2 }
    ]
  }
  ```

  Both the bulk reorder and a single-size ordinal move emit this same full-order shape, so a consumer gets the resulting size order directly with no reconstruction.

#### Board events

**`board.created`, `board.renamed`, `board.deleted`** — `{ board }`, where `board` is `{ id, slug, name }`. The envelope's `boardId` / `boardSlug` already identify the board; the embedded resource adds the `name` (and `slug` for completeness). `board.deleted` references a now-deleted board (state at occurrence).

#### Ping

**`webhook.ping`** (test deliveries only) — `{ subscriptionId, message }`, a minimal body so a receiver can confirm it is reachable, verifies the signature, and parses. A ping is not board-scoped, so its envelope carries an empty `boardId` / `boardSlug`.

### Delivery headers

Every POST carries these headers (signed or not):

| Header | Value |
|--------|-------|
| `Content-Type` | `application/json` |
| `User-Agent` | `Collattice-Webhooks` |
| `X-Collattice-Event` | The event type (`card.created`) — lets a consumer route without parsing the body. |
| `X-Collattice-Delivery-Id` | The `eventId`. The delivery id is the event id, so every retry of one event carries the same value (which is what you want for dedup). |
| `X-Collattice-Signature` | Present only when the subscription has a secret configured. |

### Signing

When a subscription has a secret set, every delivery to it carries:

```
X-Collattice-Signature: sha256=<hex-lowercase-digest>
```

The digest is HMAC-SHA256 over the **exact raw bytes of the request body**, keyed by the shared secret. To verify, compute HMAC-SHA256 over the body you received (as received, before any re-parsing or re-serialization) with the same secret, and compare against the header value using a constant-time comparison. The `sha256=` prefix names the algorithm so a future scheme can change it without changing the header name.
