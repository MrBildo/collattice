# Collattice Board

Collattice development is tracked on its own production instance — Collattice runs on Collabhost, the board the team uses to track Collattice work IS a Collattice board. Self-hosted dogfooding.

> **The board slug still reads `collaboard`.** Board slugs are fixed when a board is
> created, so this board keeps its original slug even though the project is now
> Collattice. The MCP skill and the auth-key variable have been renamed: the skill is
> invoked as `collattice` and the key is `COLLATTICE_AUTH_KEY`. Use the values as
> written below.

| Field | Value |
|---|---|
| Slug | `collaboard` |
| Board UUID | `f6fa6794-4bed-44d0-9656-de8080791302` |
| Auth key | `~/.agents/bots/<bot>/.env` → `COLLATTICE_AUTH_KEY` (per-bot, gitignored) |

See the `collattice` skill for the full auth contract and MCP usage. The conventions below are project-specific overrides on top of that skill.

## Lanes

This project deviates from the org-default lane set defined in the `collattice` skill. Two changes (adopted 2026-05-12):

- **Added: `On Deck`** — sits between `Triage` and `Ready`. Acts as the bench / depth chart for work that's been decided-on but isn't the immediate next pickup.
- **Removed: `Review`** — review happens on PRs, not on a board lane. The lane was unused.

**Lane order:** `Backlog` → `Triage` → `On Deck` → `Ready` → `In Progress` → `Done` → `Archived`.

| Lane | Purpose |
|---|---|
| **Backlog** | Someday/maybe. No commitment to ship. Long-running `Blocked` cards parked here are acceptable — Backlog is a fine parking lot for hard problems and externally-gated work. |
| **Triage** | Items awaiting disposition — *needs scope, recon, or discussion before its home can be decided.* Not only new items: a mature card can be sent **back** to Triage when it needs recon to settle (e.g. "is this still an issue?", "does the #234 value converter moot this workaround?"). Sized + labeled cards with no surfaceable triggering question get promoted out by the coordinator without per-card ask. **Default exit is On Deck** (or Backlog if long-tail, or Archived if rejected). |
| **On Deck** | Decided to do, queued for the cycle, not the immediate next pickup. The bench — coordinator-curated; operator pulls forward to Ready. |
| **Ready** | Picked up next session (or right now). Curated by the operator; coordinator proposes promotions from On Deck. Target depth: 3-5 cards. |
| **In Progress** | Actively being worked on. PR-open cards stay here — the PR is the review surface, not a board lane. |
| **Done** | Merged to main, awaiting archive sweep. |
| **Archived** | Closed. Archived cards are **frozen** — no edits, comments, labels, or attachment changes (400 response). Only `restore_card` and delete are allowed. |

### Coordinator implications

- Triage walks sub-batch dispositions as **`On Deck / Backlog / Archive`**, not `Ready / Backlog / Archive`.
- **`HANDOFF.md` "what's next session" is a snapshot of the Ready lane** — no mental subsetting required.
- **All newly-created cards go to Triage** — including ones whose disposition already looks settled. The coordinator does not place a new card straight into On Deck on its own judgment; only the operator can direct otherwise. (Standing order, 2026-06-28, suite-wide. This line previously allowed coordinator discretion and predates that order.)
- At session-end, glance at On Deck depth — if it's growing past ~10 cards without churn, surface it (it shouldn't quietly become a second Backlog).

This pattern is borrowed from Collabhost. If it proves out across multiple [Collabot.dev](https://collabot.dev)-org projects, promote the lane definition into the `collattice` skill at that point.

## Labels

Labels are board-scoped and align with conventional commit prefixes.

### Type

| Label | Color | Meaning |
|-------|-------|---------|
| `Feature` | green | `feat:` commits |
| `Bug` | orange-red | `fix:` commits |
| `Improvement` | blue | `refactor:` / minor enhancements |
| `Chore` | gray | CI, deps, tooling |
| `Docs` | teal | Documentation |
| `Infrastructure` | dark gray | Build, deploy, CI infrastructure |
| `Investigation` | yellow | Research-driven work |
| `Discussion` | purple | Needs conversation before action |

### Status (transient)

| Label | Color | Meaning |
|-------|-------|---------|
| `Blocked` | red | Can't proceed, external dependency |

## Sizes

| Size | Ordinal | Effort + Risk |
|------|---------|---------------|
| S | 0 | Trivial — single surface, no ambiguity |
| M | 1 | Moderate — one or two surfaces, straightforward |
| L | 2 | Significant — multiple surfaces or non-trivial logic |
| XL | 3 | Complex — cross-cutting, high risk, or unknown scope |

Sizes represent effort and risk, not urgency. The **scope of work** — which surfaces are touched (backend, frontend, MCP, tests) — is the primary triage signal for sizing. During triage, evaluate the scope and set the size accordingly; don't ask the user to reconsider.

## Workflow

1. New items → **Triage** with a type label (`Bug`, `Feature`, etc.).
2. Size (S/M/L/XL), discuss if needed → **On Deck** (or Backlog if long-tail; coordinator promotes without per-card ask when sizing is clean and no discussion is open).
3. Operator pulls forward from On Deck → **Ready**. Agents pick up cards only from Ready. Coordinator proposes promotions; operator curates Ready depth (~3-5).
4. Pick up → **In Progress**, comment with the plan, create a feature branch.
5. PR open → **stays in In Progress** with a PR link in the comments. The PR is the review surface; the board doesn't mirror PR state.
6. PR merged → **Done**.
7. Coordinator sweeps Done → **Archived** as part of session-close hygiene. No external trigger; coordinator's judgment, executed when the lane has accumulated enough to be worth a sweep.
8. Cards needing a spec get a comment linking to `.agents/specs/`.

## Card Conventions

### Titles
Action-oriented for features (e.g., "Add archive endpoint for cards"). Bug-report style for bugs. Keep under 80 characters.

### Descriptions
Include goal, background (if needed), and specific deliverables. Reference specs by name (`.agents/specs/<file>.md`).

### Comments
Session journals — write for a reader with no prior context. Include what was done, what changed, what's next, and PR links when relevant.

### When to defer card disposition

When a board walk with the operator is imminent, individual card-state questions (move, archive, verify-and-close) wait for the walk rather than getting fragmented into per-card rulings. The walk is the right surface for batch dispositions.

## Session Workflow

When the operator signals board work:

1. **Pull live state** — `get_lanes` + `get_cards` for recent activity. The board is ground truth; HANDOFF/TODO can drift across sessions and must be reconciled against the board, not the other way around.
2. **Read the comments before briefing.** `get_cards` returns comment *counts*, not bodies — so before briefing or asking the operator about any card with `commentCount > 0`, open it (`get_card`) and read the latest comment. Operator comments are often rulings ("Approved for 3a," "hold this"); briefing a card whose answer is already in a comment reads as not having looked. (`get_cards` also returns `latestComment` where the running instance supports it — that surfaces the freshest comment in the scan itself.)
3. **Brief the operator** — short summary of board state (what's ready, what's in progress, what's blocking, what's stale), incorporating any rulings found in those comments. Tables over prose.
4. **Wait for direction** — don't auto-start work or grab cards.

During a session:

- Move cards as state changes (Triage → On Deck → Ready → In Progress → Done; coordinator promotes Triage → On Deck or Backlog; operator pulls forward On Deck → Ready).
- Comment on cards as work progresses — write for a reader with no prior context.
- Create new cards when gaps or ideas surface — put them in Triage with minimal ceremony. Triage is the destination even when the disposition already seems obvious; promoting out of it is a separate, later step.

### Cross-project externally-gated cards

When a Collattice card is gated on a sibling project's deliverable (e.g., something Collabhost ships first), keep the card in Backlog and add an explicit gate-and-trigger comment naming what's blocking and what unblocks it. Do **not** apply the `Blocked` label — that label is reserved for in-Triage gating; Backlog is the right home for externally-gated cards by design.

### Card addressing

Use `cardNumber` + `boardSlug` (e.g., card #15 on `collaboard`). When referencing cards from another project's board (e.g., Collabhost #220), always name the board — card numbers are board-scoped and easy to garble silently.

## Archive

- Use `archive_card` to archive (not `move_card` — the archive lane refuses `move_card` with a 400).
- Archived cards are frozen: no edits, comments, labels, or attachment mutations (400).
- Only `restore_card` (requires target `laneId`) and delete are allowed.
- `get_cards` excludes archived by default; pass `includeArchived: true` to include.
- Card responses include `isArchived` (bool).

## MCP Tips

- **Pass `labelIds` at creation.** Use the `labelIds` param on `create_card` instead of a separate `add_label_to_card` call. One call instead of two.
- **Schema-load before first use of an unfamiliar tool.** Don't infer parameter names from sibling-tool conventions — the wrapper hides parameter mismatches as opaque `An error occurred invoking '<tool>'.` messages. Card #202 (Ready) addresses the wrapper-side; the caller-side discipline is to load the schema first.
