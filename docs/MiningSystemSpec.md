# DevChronicle Mining System (Technical Spec)

## 1. Goal And Product Promise

DevChronicle mining is **commit-first** and **branch-agnostic**:

- Every commit that exists in the repository history at mining time should be captured and stored with evidence (subject + file list + churn).
- Mining is scoped by a **session** (repo + author filters + options + optional date range).
- Summaries are generated from mined evidence; the model must not invent facts.
- Branches are treated as **labels**, not the source of truth, but the UI should still be able to answer:
  - "Which branches were touched on this day?"
  - "What was the commit chain per branch for this day?"

From the user's perspective:

- Pick a local clone of a GitHub repo.
- Create a session (optionally with a date range).
- DevChronicle mines commits across the repo (not just the current checkout), groups them by day, and makes them available for summarization.

## 2. Definitions

- **Repo**: A local working directory containing a Git repository.
- **Session**: Immutable mining rules for a repo + author filters + options + date range.
- **Ref Scope**: Which refs are included when enumerating commits:
  - `LocalBranchesOnly`: commits reachable from `refs/heads/*`
  - `LocalPlusRemotes`: commits reachable from `refs/heads/*` and `refs/remotes/*`
  - `AllRefs`: commits reachable from all refs (includes tags)
- **Scope**: The effective time bounds of a mining operation:
  - `RangeStart`+`RangeEnd` set -> mine only that range.
  - No range -> mine "all history".
- **Evidence**: The commit set stored in SQLite (`commits`) plus per-commit file stats (`files_json`).
- **Day**: A calendar date derived from each commit's author timestamp.
- **Author Identity**: The set of author email(s)/name(s) used to decide "my commits".

## 3. Current Implementation Snapshot (As Of Today)

### 3.1 Git Commands Used

Mining uses `git.exe` via `GitService`:

- Fetch:
  - `git fetch --all --prune --tags`
- Commit enumeration:
  - `git log --all --pretty=format:%H|%aI|%an|%ae|%s`
  - Optional date filters:
    - `--since="YYYY-MM-DD"`
    - `--until="YYYY-MM-DD"`
  - Optional author filters:
    - `--author="email-or-name"`
  - Optional merge exclusion:
    - `--no-merges` (default when `IncludeMerges == false`)
- Per-commit enrichment (evidence):
  - `git show --numstat --pretty=format: <sha>`

Notes:

- Current mining enumerates commits using `--all` (all reachable commits from all refs: local branches, remote-tracking branches, tags).
- Merge commits may be included/excluded by the `--no-merges` switch, but merge commits are not currently annotated as merges in the DB (see "Planned Improvements").
- Branch attribution ("branches touched that day") is not yet computed/stored.

### 3.2 Persistence (SQLite Tables)

Mining writes:

- `commits` (dedup by `(session_id, sha)`):
  - `sha`, `author_date`, `author_name`, `author_email`, `subject`
  - `additions`, `deletions`, `files_json`
  - `is_merge` exists but is not reliably populated yet
- `days` (upsert by `(session_id, day)`):
  - `commit_count`, `additions`, `deletions`, `status`

Summaries are separate:

- `day_summaries` (versioned by `prompt_version`)

### 3.3 Idempotency

- Commits are inserted with `INSERT OR IGNORE`.
- Days are upserted with `ON CONFLICT ... DO UPDATE`.

### 3.4 UX Entry Points

- New session with no mined days auto-starts mining on load (`SessionDetailViewModel.LoadSessionAsync`).
- Session detail context menu:
  - `Re-Mine`: delete commits/days/day_summaries in scope and mine scope again.
  - `Re-Mine (Keep Summaries & Evidence)`: re-mine scope without deleting commits/summaries; if a summarized/approved day's aggregates changed, downgrade that day's status to `Mined`.

## 4. Mining Pipeline (Step-By-Step)

### 4.1 Inputs (From Session)

- `RepoPath`
- `AuthorFiltersJson` -> `[AuthorFilter]`
- `OptionsJson` -> `SessionOptions`:
  - `IncludeMerges`
  - `WindowSizeDays` / `OverlapDays` (used by the view model for windowing)
- Optional `RangeStart` / `RangeEnd` (nullable)

### 4.2 Pipeline

1. Fetch remote refs:
   - `git fetch --all --prune --tags`
2. Enumerate commits in scope:
   - `git log --all ...` with date + author + merge filters
3. For each commit:
   - Read numstat file deltas:
     - Parse additions/deletions and file paths
   - Compute per-commit totals:
     - `Commit.Additions` / `Commit.Deletions`
   - Store `files_json` (JSON array of `{ path, additions, deletions }`)
4. Persist commits in one DB transaction:
   - `BatchInsertCommitsAsync`
5. Aggregate commits by day (author date):
   - Group by `commit.AuthorDate.Date`
   - Upsert `days` for those dates

### 4.3 Scope Semantics

- If `RangeStart/RangeEnd` are provided, mining is bounded to `[RangeStart, RangeEnd]` inclusive (day precision).
- If no range is provided, mining runs without `--since/--until`.

Correctness note (planned improvement):

- Git date flags (`--since`, `--until`) operate on timestamps.
- When the UI provides a date-only range, the miner should normalize to an explicit time window:
  - `start = RangeStart at 00:00:00` (session timezone)
  - `end_exclusive = (RangeEnd + 1 day) at 00:00:00` (session timezone)
  - pass `--since=<start>` and `--until=<end_exclusive_minus_epsilon>` to avoid off-by-one behavior.

### 4.4 Ref Scope Semantics (Planned Improvement)

Current implementation uses `AllRefs` (`git log --all`).

To stay honest about what "all branches" means and avoid surprises, the session should store an explicit `RefScope`:

- `LocalBranchesOnly` -> `git log --branches`
- `LocalPlusRemotes` -> `git log --branches --remotes`
- `AllRefs` -> `git log --all`

Recommended default for MVP:

- `LocalBranchesOnly`, assuming local branches are not deleted (no reflog dependency).

### 4.5 Author Identity Matching Semantics

Current mining applies author filters via `git log --author=...`, which matches the **author**, not the committer.

Spec semantics:

- A session can contain multiple `AuthorFilter` entries (email/name).
- Mining includes a commit if its author matches any configured filter (logical OR).
- Storage is currently author-only (`author_name`, `author_email`, `author_date`).

Planned improvement:

- Store committer name/email/date as separate fields to distinguish "work I authored" vs "merge I committed" (e.g., bots).

### 4.6 Branch Attribution And Daily Branch Chains (Best-Effort, Deterministic)

User-visible outcome:

- For any day, the UI can show "branches touched that day" and an ordered commit chain for each branch.

Important constraints:

- Branches are labels and can be ambiguous (a commit may be reachable from multiple branches).
- The system must be deterministic and explainable.
- Performance should avoid N subprocesses per commit for common cases.

Proposed approach:

1. Capture a branch tip snapshot at mining time:
   - `git for-each-ref refs/heads --format="%(refname:short)|%(objectname)"`
2. Assign a *primary* branch label to each commit (for grouping):
   - Preferred: batch `git name-rev --name-only --refs=refs/heads/* <sha...>` in chunks.
   - Fallback: `git branch --contains <sha>` for a selected commit (on-demand UI), not for the full day in the hot path.
3. Persist branch labels:
   - `commit_branch_labels(session_id, sha, branch_name, is_primary, label_method, captured_at)`
4. Daily branch chains:
   - Group commits by `branch_name` (primary label).
   - Sort by `author_date` (MVP).
   - Tie-breaker: SHA ascending to stabilize output.

Notes:

- The UI may optionally show "also contained in:" secondary branches, but that should be computed lazily.
- If a commit cannot be labeled (detached/ambiguous), group it under a stable bucket like `(unattributed)`.

## 5. Re-Mine Semantics

### 5.1 Re-Mine (Destructive)

For the effective scope:

- Delete `day_summaries` in scope.
- Delete `days` in scope.
- Delete `commits` in scope.
- Run mining for scope.

This guarantees day aggregates and evidence exactly match the re-mined commit set.

### 5.2 Re-Mine (Keep Summaries & Evidence)

For the effective scope:

- Do not delete commits or summaries.
- Run mining for scope (commit inserts are `INSERT OR IGNORE`).
- Compare day aggregates before vs after:
  - If a day has a summary and its aggregates changed -> set day status to `Mined` (pending).
  - If unchanged -> preserve prior status (`Summarized`/`Approved`).

Rationale:

- This supports active branches where new commits arrive and the user wants to keep older summary text but mark affected days as needing re-summarization.

## 6. Limitations And Known Gaps (Planned Improvements)

### 6.1 Accurate Merge Annotation

Current mining does not record commit parents, so `is_merge` is not reliable.

Planned change:

- Extend commit enumeration format to include parents:
  - `git log --all --pretty=format:%H|%P|%aI|%an|%ae|%s`
- Mark `IsMerge = true` when parent count >= 2.
- Persist parents for graph operations (new table):
  - `commit_parents(session_id, child_sha, parent_sha, parent_order)`

### 6.2 "Integrated Work" (Merge Events) Without Main-Branch Bias

If we want "merged into anything" acknowledgement:

- A merge commit is any commit with 2+ parents.
- For merge commit `M` with parents `P1` (first parent) and `P2` (second parent):
  - Integrated set = commits reachable from `P2` but not reachable from `P1`.

Planned storage:

- `integration_events(session_id, anchor_sha, occurred_at, method, confidence, details_json)`
- `integration_event_commits(session_id, integration_event_id, sha)`

### 6.3 Squash / Rebase / Cherry-Pick Acknowledgement (Patch-ID)

If we want best-effort "work landed" even when SHAs change:

- Compute per-commit patch-id:
  - `git show <sha> --pretty=format: --unified=0 | git patch-id --stable`
- Store `patch_id` per commit; mark "landed" when patch-ids match across history.

This is optional and should be gated for performance.

### 6.4 Branch Snapshots (UI Labels, Not Truth)

Optional enhancement:

- Capture a ref snapshot at mining time:
  - `git for-each-ref refs/heads refs/remotes --format="%(refname:short)|%(objectname)|%(committerdate:iso8601)"`
- Store `branch_snapshots(session_id, captured_at, ref_name, head_sha, head_date, is_remote)`

## 7. Performance Considerations

### 7.1 Current Costs

Mining currently runs `git show --numstat` once per commit, which can be expensive for large ranges.

Planned optimization:

- Use a single-pass log that emits numstat directly:
  - `git log --numstat --pretty=...`
- Or batch commits in fewer subprocesses.
- Cache enrichment by SHA:
  - If `(session_id, sha)` already exists, do not re-run `git show` for that commit during re-mine variants.

### 7.2 Indexing

SQLite indices required for UI + summarization:

- `commits(session_id, author_date)`
- Consider adding:
  - `commits(session_id, author_email, author_date)`
  - `days(session_id, day)`
  - `days(session_id, status, day)`
  - `commit_branch_labels(session_id, branch_name)` (if branch labels are persisted)

### 7.3 Repo Identity And Mining Provenance (Optional But Useful)

To explain "why did results change between runs?" store:

- `origin_url` (best-effort from `git remote get-url origin`)
- `mined_at` timestamp per mining run
- `head_sha` at mining time (e.g., `git rev-parse HEAD`)
- `git_version` (e.g., `git --version`)
- `ref_scope` used for mining
- miner version / prompt version (already tracked in summaries)

## 8. Failure Modes And Logging

- Git fetch/log/show failures:
  - Capture stderr and surface in UI error messages.
  - Log full exception details via `LoggerService`.
- Cancellation:
  - All mining operations accept a `CancellationToken`.

## 9. Validation And Acceptance Tests (Manual)

- Mine (new session):
  - Create a session for a local GitHub clone and verify days + commits exist.
- Re-Mine:
  - Verify DB is cleared in scope and repopulated.
- Re-Mine (Keep Summaries & Evidence):
  - Create a summary for a day, add new commit in that day's range, re-mine keep summaries, and verify day status becomes `Mined` while summary text remains.
