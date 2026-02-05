* **Project name:** DevChronicle (working name)

* **Purpose:** Session-based desktop app that mines Git history across all refs (local + remote-tracking), groups work by day, and generates bullet-only developer diary entries and resume-ready summaries using deterministic Git parsing + cached OpenAI summarization.

* **Primary user:** single developer (you), but design supports multiple authors/sessions.

* **Core requirements:**

  * Session-based: reopen any session and view mined facts + AI summaries.
  * Resumable processing: mine/summarize in chunks (e.g., last 14 days -> backfill).
  * Evidence-based output: summaries must be grounded in commit subjects + file paths (diffs optional).
  * Bullet-only style: no headings; 4-6 bullets/day (cap 10).
  * Fast MVP: Windows-first, minimal deps, predictable execution.

* **Target stack (MVP):**

  * **.NET 8 (C#)**
  * **WPF** UI
  * **SQLite** for sessions, mined facts, summaries, caches
  * **git.exe** subprocess for all Git queries (avoid libgit2 for v1)
  * **OpenAI API** via official .NET client (LLM summarization)
  * Optional later: background worker queue, diff sampling, export templates.

---

* **User stories / functional requirements:**

  * Create session by selecting: repo path, author filters (email/name), date start (or "last N days"), date end (default: today), options (include merges, include diffs toggle).
  * "Mine commits" for a session: scan all refs and store commit facts grouped by day.
  * "Summarize days" for a session: call OpenAI to generate bullet-only daily summaries, cached per day.
  * Resume/continue: if interrupted, continue from last checkpoint without redoing work.
  * Backfill: one-click "Process previous 30 days" (or configurable window size).
  * Review: browse day list -> view bullets -> view evidence (commit SHAs, subjects, file lists).
  * Edit/approve: optionally edit bullets; mark day as approved.
  * Export: generate `DeveloperDiary.md` (bullet-only) and `ResumeBullets.md` for selected date range.

---

* **Non-functional requirements:**

  * **Performance:** handle 1-2 years of commit history by chunking (no full-scan requirement).
  * **Reliability:** deterministic mining; recoverable from crash mid-run; idempotent operations.
  * **Cost control:** avoid repeated OpenAI calls via caching; per-run budget caps (days/calls/tokens).
  * **Security:** never store OpenAI key in plain text by default; use Windows Credential Manager or user-provided per-run env var; do not upload full diffs by default.
  * **Determinism:** summaries must not invent facts; require evidence linkage and conservative phrasing when commit messages are vague.

---

* **Architecture overview:**

  * **UI layer (WPF):** session creation, run controls, progress, review/editor, export.
  * **Core services (C#):**

    * `GitService`: runs git commands, parses outputs.
    * `MiningService`: builds "truth dataset" (commits + day aggregates).
    * `ClusteringService`: compresses many commits into "work units" (for LLM input).
    * `SummarizationService`: OpenAI calls + output validation.
    * `CacheService`: prevents repeated summarization; stores hashes.
    * `ExportService`: produces markdown outputs (bullet-only).
  * **Persistence (SQLite):** all session state, mined data, summaries, checkpoints.
  * **Background execution:** `Task.Run` + cancellation tokens; UI shows progress; operations are resumable.

---

* **Session model (concepts):**

  * **Session:** a named run with fixed repo + author filters + processing options + time range.
  * **Phase A (Mining):** Git -> commits/day facts stored.
  * **Phase B (Summarization):** day facts -> bullet summaries stored + cached.
  * **Phase C (Resume extraction):** range of day summaries -> resume bullets stored.
  * **Checkpoint:** where mining/summarization last stopped (window cursor).

---

* **Data model (SQLite schema, MVP):**

  * `sessions`

    * `id` (PK)
    * `name` (text)
    * `repo_path` (text)
    * `main_branch` (text, default "main")
    * `created_at` (utc)
    * `author_filters_json` (text)
    * `options_json` (text)  // include_merges, include_diffs, window_size_days, caps, etc.
    * `range_start` (date nullable)
    * `range_end` (date nullable)
  * `checkpoints`

    * `session_id` (FK)
    * `phase` (text: "mine" | "summarize" | "resume")
    * `cursor_key` (text: "next_window_start" | "next_day_to_summarize" etc.)
    * `cursor_value` (text)
    * `updated_at` (utc)
  * `commits`

    * `session_id` (FK)
    * `sha` (text)
    * `author_date` (date)
    * `author_name` (text)
    * `author_email` (text)
    * `subject` (text)
    * `additions` (int)
    * `deletions` (int)
    * `files_json` (text)
    * `is_merge` (int)
    * `reachable_from_main` (int nullable, optional v2)
    * PK: (`session_id`, `sha`)
  * `days`

    * `session_id` (FK)
    * `day` (date)
    * `commit_count` (int)
    * `additions` (int)
    * `deletions` (int)
    * `status` (text: "mined" | "summarized" | "approved")
    * PK: (`session_id`, `day`)
  * `day_summaries`

    * `session_id` (FK)
    * `day` (date)
    * `bullets_text` (text)  // newline-separated "- " bullets
    * `model` (text)
    * `prompt_version` (text)
    * `input_hash` (text)
    * `created_at` (utc)
    * PK: (`session_id`, `day`, `prompt_version`)
  * `resume_summaries`

    * `session_id` (FK)
    * `range_start` (date)
    * `range_end` (date)
    * `bullets_text` (text)
    * `model` (text)
    * `prompt_version` (text)
    * `input_hash` (text)
    * `created_at` (utc)

---

* **Git mining specification:**

  * **Pre-step:** `git fetch --all --prune --tags`
  * **Commit selection:**

    * Use `git log --all` to include all reachable commits from all refs.
    * Filter by author via `--author=<filter>` (email/name).
    * Default: `--no-merges` (configurable).
    * Date range filters: `--since=<date>` and `--until=<date>` for chunking windows.
    * Date field: author date formatted as `YYYY-MM-DD`.
  * **Per commit enrichment:**

    * `git show --numstat --pretty=format: <sha>` to get file list and churn.
    * Store `files_json`, `additions`, `deletions`.
  * **Deduplication:**

    * Deduplicate by SHA across all refs (same commit can appear on multiple branches).
  * **Day aggregation:**

    * Group commits by `author_date` into `days` table; compute totals and count.

---

* **Resumable windowing strategy (key to "year+" histories):**

  * Process history in **bounded windows** (default 14 days, configurable 7/30/90).
  * Session stores `next_window_start` checkpoint.
  * "Backfill older" subtracts window size from earliest processed day and runs mining for that window.
  * Overlap strategy: reprocess last 1 day of previous window to avoid off-by-one issues.
  * Mining is idempotent (`INSERT OR IGNORE` by SHA; recompute day aggregates).

---

* **Commit-to-work-unit clustering (pre-LLM compression):**

  * Inputs: commit subjects, file paths, churn.
  * Algorithm (MVP):

    * Compute top-level folder per file (first path segment).
    * Bucket commits by dominant folder; break ties by keywords in subject.
    * Keywords map:

      * fix/bug/crash/null/edge -> "bugfix"
      * perf/speed/cache/memory -> "performance"
      * refactor/cleanup/rename -> "refactor"
      * test/ci/coverage -> "testing/tooling"
      * ui/qml/wpf/toolbar/dialog -> "ui"
    * If commit volume is high: keep only top N commits by churn in each bucket for LLM prompt payload, but preserve counts/totals for day context.

---

* **OpenAI summarization specification (bullet-only):**

  * Model: configurable (default lightweight model for cost; upgradeable).
  * Input payload per day:

    * day, commit_count, additions/deletions
    * top folders touched
    * list of commit facts: short SHA, subject, limited file list, churn
  * Output constraints:

    * Only lines starting with `- `
    * 4-6 bullets default, max 10
    * Must not invent facts; must be conservative if unclear
  * Validation:

    * Post-parse and enforce bullet prefix; drop blank lines.
    * Optional v2: require bullet evidence mapping (store SHAs per bullet).
  * Caching:

    * `input_hash = SHA256(prompt_version + model + sorted(commit_shas) + subjects + top_folders)`
    * If `day_summaries` has same `input_hash`, skip API call.

---

* **Offline / degraded mode (LLM optional):**

  * If API key missing or call fails: produce deterministic bullets like:

    * "Worked on {top folders}; commits: {subjects condensed}"
  * Mark summary as "generated_offline" in metadata; allow re-run later.

---

* **UI specification (MVP screens):**

  * **Sessions screen:** list sessions (name, repo, last updated), create/open.
  * **Session detail:**

    * Controls: repo path, authors, date range, window size, include merges, summarize toggles
    * Buttons: "Mine last N days", "Backfill previous N days", "Summarize pending days", "Stop"
    * Progress: mined days count, summarized days count, remaining queue
  * **Day browser:** list of days with commit_count and status; selecting day shows bullets and evidence list.
  * **Editor:** inline edit bullets; "Approve day".
  * **Export:** select range; export diary/resume markdown.

---

* **Export formats:**

  * `DeveloperDiary.md`

    * Bullet-only, includes a date line bullet followed by that day's bullets.
  * `ResumeBullets.md`

    * Bullet-only, 6-12 bullets extracted from selected range.
  * `SessionArchive.json` (optional)

    * Session settings + day summaries + evidence for portability.

---

* **Error handling and robustness:**

  * All long operations cancellable via `CancellationToken`.
  * On crash, resume from last checkpoint; mined commits are already in DB.
  * Git command failures: surface stderr; suggest "git fetch" or repo path checks.
  * OpenAI failures: exponential backoff + degrade to offline summary; never block UI.

---

* **MVP acceptance criteria:**

  * Create session with range (or no range) -> mine scope -> see correct days/commit counts.
  * Summarize last 5 days -> bullets-only output saved + displayed.
  * Close app -> reopen -> session persists and browsing works.
  * Re-Mine (scope-aware) -> mining reprocesses within scope and app stays responsive.
  * Export diary markdown for selected range.

---

* **Future enhancements (v2/v3):**

  * Detect "merged into main" via `git merge-base --is-ancestor` tagging.
  * Optional diff sampling (top 3 churn commits/day) with redaction and size caps.
  * Evidence mapping per bullet (store SHAs per bullet via structured JSON output).
  * Batch processing mode for large backfills.
  * GitHub PR enrichment (titles, PR numbers) if user supplies token.
  * Multi-repo rollups (one resume from multiple repos).
