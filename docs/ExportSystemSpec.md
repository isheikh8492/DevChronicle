# DevChronicle Export System v1 (Diary + Archive + Update Existing Diary)

## Goal
Provide a product-grade export workflow that is session-based and supports:
- Creating new exports from selected sessions
- Updating an existing developer diary safely and deterministically
- Exporting an evidence-rich archive (commits + file stats)

## Non-Goals (v1)
- Resume export
- Editing unmanaged user Markdown files in place
- Fancy diff UI beyond counts (+new / ~updated / =unchanged)
- ZIP bundles (optional later)

---

## Concepts

### Session-Based Exports
- A **session** is the unit of data ownership (repo + filters + options + optional date range).
- All exports operate over one or more session IDs.

### Outputs
- **Developer Diary (Markdown)**: summary-driven narrative output.
- **Session Archive (JSON)**: evidence-rich structured data export containing commits and per-file stats.

### Evidence
Evidence is stored per commit in the DB:
- commit metadata + churn
- `files_json` containing per-file additions/deletions

Evidence is:
- Included in **Archive**
- Not required in **Diary** (Diary is summary-first)

---

## UX (Information Architecture)

### Page 1: Export Hub (existing Export page evolves into this)
Primary responsibilities:
- Show session list with checkboxes (used for *new exports*)
- Provide clear entry points for:
  1. **Create New Export...**
  2. **Update Existing Diary...**
  3. (Optional) **Export Archive...** if not included in Create New Export

Rules:
- Selecting sessions affects **Create New Export**.
- Session selection does **not** affect **Update Existing Diary** (the diary file binds its own session set).

UI elements:
- Sessions list:
  - Checkbox per session
  - Session details (name, repo name + date range, repo path, created timestamp)
  - Secondary per-session quick actions: `Diary`, `Archive`
- Primary actions:
  - `Create New Export...` (navigates to Target page)
  - `Update Existing Diary...` (navigates to Update Diary page)

### Page 2: New Export Target (Target page)
Purpose:
- Configure destination and outputs for **new exports** from currently selected sessions.

Inputs:
- Selected session IDs (from Hub)
- Outputs:
  - Diary (on/off)
  - Archive (on/off)
- Format:
  - Combined dropdown:
    - Markdown + JSON
    - Markdown only
    - JSON only
- Destination folder chooser
- File naming preview

Defaults:
- Diary + Archive enabled
- Hide repo paths in Markdown enabled
- Missing summaries => placeholder enabled
- Evidence included in Archive always

Actions:
- `Export` (writes files atomically)
- `Back`

### Page 3: Update Existing Diary
Purpose:
- Update a *managed* diary file safely.
- Auto-binds session IDs from diary metadata; user does not re-select sessions.

Step flow:
1. User selects an existing diary Markdown file.
2. App checks if the file is **managed** (contains a manifest).
3. If managed:
   - Load manifest session IDs
   - Show bound sessions list (read-only)
   - Show preview counts: `+new / ~updated / =unchanged`
   - Allow `Apply Update`
4. If unmanaged (no manifest):
   - Offer `Convert to managed...`
   - Conversion creates a *new* managed diary file (does not modify original)
   - After conversion, proceed with update flow

Actions:
- `Browse...` diary file
- `Convert to managed...` (only for unmanaged)
- `Apply Update`
- `Back`

---

## Safety & Protection Rules

### Strict Session Binding for Updates
- Update Existing Diary uses the diary's manifest session IDs.
- The user's session checkbox selection must not alter update behavior.

### Managed vs Unmanaged Diaries
- **Managed diary**: produced by DevChronicle and contains an embedded manifest header.
- **Unmanaged diary**: any Markdown without a manifest.
- v1 policy:
  - Never update unmanaged diaries in place.
  - Only allow:
    - Convert to a new managed diary file
    - Or export a new diary file elsewhere

### Atomic Writes + Backup
All file writes must be safe:
- Write to `*.tmp` in destination
- If successful, rename/replace the final file
- Before replacing an existing file, create a `*.bak` (timestamp optional)
- On cancellation/failure:
  - Delete temp files
  - Leave original intact

### Filename Safety
- Sanitize session names / repo names for filenames.
- Avoid invalid Windows characters.
- Avoid collisions by adding timestamp suffix.

---

## Diary (Markdown) Content Rules

### Data Source
- Diary is summary-driven:
  - Use latest `day_summaries.bullets_text` for each day
- If no summary:
  - Emit placeholder text (v1 default), e.g. `(No summary yet)`

### Multi-Session Diary Structure
- Group by calendar date.
- Under each date, emit an entry per session (repo), ordered by:
  1. repo name (from repo path basename)
  2. session name

### Privacy Default (Markdown)
- Do not include full local `RepoPath` in Markdown by default.
- Use repo name only.

---

## Diary Update (In-Place) Format Requirements

This section defines the exact managed-diary file format that enables safe, deterministic updates.

### Managed Diary Manifest (HTML Comment JSON)

A managed diary MUST start with a single-line manifest comment:

`<!-- DC:MANIFEST {...json...} -->`

Required manifest fields (v1):
- `schemaVersion`: string (fixed `"1.0"`)
- `diaryType`: string (`"single"` | `"multi"`)
- `sessionIds`: int[] (sorted ascending)
- `options`: object
- `options.hideRepoPaths`: bool
- `options.includePlaceholders`: bool
- `options.summaryPolicy`: string (fixed `"latestCreatedAt"`)
- `createdAt`: string (ISO-8601 UTC)
- `lastSyncedAt`: string (ISO-8601 UTC)

Example:

`<!-- DC:MANIFEST {"schemaVersion":"1.0","diaryType":"multi","sessionIds":[12,34,56],"options":{"hideRepoPaths":true,"includePlaceholders":true,"summaryPolicy":"latestCreatedAt"},"createdAt":"2026-02-06T03:21:00Z","lastSyncedAt":"2026-02-06T03:21:00Z"} -->`

### Stable Markers

Managed content is updated via sentinel markers. The exporter MUST only rewrite content inside these markers.

Day section markers (used for insertion and ordering):
- `<!-- DC:DAY day=YYYY-MM-DD -->`
- `## YYYY-MM-DD`
- `<!-- /DC:DAY -->`

Entry markers (granularity is per `(day, sessionId)`):
- `<!-- DC:ENTRY day=YYYY-MM-DD session=123 summaryCreatedAt=2026-02-05T18:00:00Z -->`
- entry content
- `<!-- /DC:ENTRY -->`

Notes:
- `summaryCreatedAt` MUST be `"none"` when no summary exists and a placeholder is emitted.
- The exporter MUST preserve any user-authored text outside DC markers.

### Insertion and Ordering Rules

The diary is ordered deterministically:
- Days: chronological ascending (oldest to newest) by `YYYY-MM-DD`.
- Within a day: entries ordered by `repoName` (ascending), then `sessionName` (ascending), then `sessionId` (ascending).

Update algorithm:
1. Parse the manifest; derive the bound `sessionIds`.
2. Query DB for `"ideal"` entries for each bound `sessionId` and day (`summaryPolicy = latestCreatedAt`).
3. For each ideal entry key `(day, sessionId)`:
   - If `DC:ENTRY` exists: replace the block contents between `DC:ENTRY` and `/DC:ENTRY`, and update `summaryCreatedAt`.
   - If `DC:ENTRY` does not exist:
     - If `DC:DAY` exists for that day: insert the entry into that day in the correct order.
     - If `DC:DAY` does not exist: create a new day section (`DC:DAY` + heading) and insert it in date order, then add the entry.
4. For any existing `DC:ENTRY` blocks that are not present in the ideal set:
   - v1 default: leave them as-is (no deletion), but they may be reported as `"extra"` in a warning.

### Staleness and Diff Computation

The app reports whether a managed diary is `"Up to date"` or `"Out of date"` relative to the DB.

Definitions:
- Let `lastSyncedAt` be from the manifest.
- Let `latestSummaryCreatedAt(sessionIds)` be the max `day_summaries.created_at` across bound sessions.

Stale rule:
- A diary is stale if `latestSummaryCreatedAt(sessionIds) > lastSyncedAt`.

Diff counts shown to user (based on entry keys):
- `new`: entry key exists in ideal set but no matching `DC:ENTRY` exists in file.
- `updated`: entry key exists in both, but marker `summaryCreatedAt` differs from DB latest summary `created_at` for that `(day, sessionId)`.
- `unchanged`: entry key exists in both and `summaryCreatedAt` matches.
- `extra` (warning): `DC:ENTRY` exists in file but entry key not present in ideal set.

After a successful update:
- `lastSyncedAt` MUST be set to `exportedAt` (UTC time) and the manifest rewritten in-place as a single-line replacement.

---

## Archive (JSON) Rules

### Combined Multi-Session Archive (v1 default)
- One JSON file containing:
  - `schemaVersion`
  - `exportedAt`
  - `sessions[]`:
    - session metadata
    - days[]:
      - day stats
      - summary (latest)
      - commits[]:
        - commit fields
        - files[] parsed from `files_json`

### Deterministic Ordering
- sessions ordered by repo name then createdAt
- days ordered ascending
- commits ordered by author_date then sha
- files ordered by path

---

## Database Access (Performance Requirements)
Avoid N+1 query patterns:
- Add batch queries in `DatabaseService` for:
  - days in range for many sessions
  - commits in range for many sessions
  - latest summaries for many sessions (latest-per-day per session)

Effective session range for exports:
- If session has `RangeStart/RangeEnd`, use them.
- Else derive from existing `days` (min/max day) for that session.

---

## Operational Requirements (Performance, Atomicity, Cancellation)

This section defines best practices required for a production-grade implementation.

### Streaming Output (Do Not Build Giant Strings)
- Markdown diary export SHOULD be written using a `StreamWriter` to a temp file:
  - write header first, then write day sections and entries incrementally.
- JSON archive export SHOULD be written using `Utf8JsonWriter` over a `FileStream`:
  - write objects/arrays incrementally (no in-memory JSON blob).
- Preview/diff computation MUST NOT require full text comparison:
  - use `summaryCreatedAt` in `DC:ENTRY` markers to decide `updated` vs `unchanged` in O(1).

### Atomic Writes + Backups
- All exports MUST write to `*.tmp` first, then replace the final path on success.
- Updating an existing managed diary MUST create a backup before replacement:
  - recommended: `File.Replace(tmpPath, finalPath, bakPath)` (Windows-friendly atomic replace).
- On failure or cancellation:
  - temp files MUST be deleted
  - the original file MUST remain unchanged

### Cancellation and User Abort
- All export operations MUST accept a `CancellationToken`.
- The implementation MUST check for cancellation frequently:
  - between DB batches (per session / per day)
  - before writing large sections
  - before final replace step
- If canceled:
  - return a "Canceled" result state
  - clean up temp files and do not touch the original output paths

### Large Exports (Scaling Guardrails)
- Batch DB reads and stream output to keep memory bounded.
- Avoid per-day/per-commit DB calls in loops.
- Log export counts and timings (sessions/days/entries) for diagnosing performance regressions.

---

## Acceptance Criteria (v1)
- User can create a new diary+archive export for selected sessions.
- User can update an existing managed diary without re-selecting sessions.
- Updating a diary never corrupts or partially overwrites it (atomic + backup).
- Unmanaged diaries are never edited; conversion creates a new managed file.
- Multi-session diary is grouped by date, ordered by repo/session deterministically.
- Archive contains commits + file evidence; diary remains summary-first.
