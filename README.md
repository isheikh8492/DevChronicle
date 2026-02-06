# DevChronicle

DevChronicle is a Windows desktop app that helps you turn raw Git history into session-based, evidence-backed daily summaries and exportable developer diaries.

The project is intentionally pragmatic:

- sessions own data
- mining is deterministic and resumable
- summarization is evidence-driven and bullet-only
- exports are safe (atomic writes) and updateable (managed diary format)

## Table of contents

- What it does
- How to build/run
- Configuration and settings keys
- Data storage (DB + logs)
- UX walkthrough (Sessions, Session Detail, Export, Settings)
- Mining subsystem (how commits are collected)
- Summarization subsystem (OpenAI, prompts, validation)
- Evidence and branch attribution
- Export subsystem (new export, per-session export, update diary, conversion)
- Managed diary file format (manifest + markers)
- Progress/cancellation guarantees
- Database schema overview
- Troubleshooting
- Limitations and next milestones

## What DevChronicle does

- **Sessions**: Create sessions tied to a repo and options, then revisit them later.
- **Mining**: Scan Git history, parse commit evidence, store into SQLite.
- **Summarization**: Generate bullet-only day summaries using OpenAI, store into SQLite.
- **Review**: Browse days, view summaries and evidence (commits, files, branches).
- **Export**:
  - create a new managed developer diary (Markdown) and/or archive (JSON)
  - update an existing managed diary from DB summaries after re-mine/re-summarize
  - convert unmanaged markdown to a managed copy (never edits unmanaged files in place)

## Build and run

### Requirements

- Windows 10/11
- .NET 8 SDK (`net8.0-windows`)
- Git installed and `git.exe` available on PATH
- Network access for AI summarization (OpenAI)

### Build

```powershell
dotnet restore
dotnet build DevChronicle.csproj -v minimal
```

If output files are locked by a running instance, build to a different output folder:

```powershell
dotnet build DevChronicle.csproj -v minimal -p:OutDir=artifacts\tmpbuild\
```

### Run

```powershell
dotnet run --project DevChronicle.csproj
```

## Tests

Test project: `Tests/DevChronicle.Tests/DevChronicle.Tests.csproj`

Run all tests:

```powershell
dotnet test
```

If the app is running and locks `DevChronicle.exe`, use an alternate output folder:

```powershell
dotnet test Tests/DevChronicle.Tests/DevChronicle.Tests.csproj -p:OutDir=artifacts\tmpbuild\
```

## CI/CD

CI runs on GitHub Actions:

- `.github/workflows/ci.yml`: restore, build, test on `main` pushes and PRs

CD for MSIX:

- `.github/workflows/release-msix.yml`: builds and packages an MSIX on tag pushes `vX.Y.Z`
- Uses a **self-signed** certificate in CI (good for internal testing)
- Output: `DevChronicle.msix` artifact

To trigger a release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## MSIX notes

- Signed MSIX avoids install warnings. Self-signed is fine for internal use.
- For public distribution, replace the self-signed cert with a real code-signing certificate.
- The manifest template lives at `build/msix/AppxManifest.xml`.

## Tech stack

- `.NET 8` + `WPF`
- UI: `WPF-UI` (`Wpf.Ui`)
- Persistence: `SQLite` (`Microsoft.Data.Sqlite`) + `Dapper`
- MVVM: `CommunityToolkit.Mvvm`
- OpenAI: HTTP to `https://api.openai.com/v1/chat/completions`

## Project layout

- `App.xaml.cs`: startup, dependency injection, global exception handlers
- `MainWindow.xaml(.cs)`: shell + navigation host
- `Views/`: pages and dialogs
- `ViewModels/`: state machines and commands for mining/summarization/export
- `Services/`: Git, database, mining, summarization, export, settings, logging
- `Models/`: persistence models and DTOs
- `Converters/`: WPF binding converters
- `docs/`: product and subsystem specs

## Configuration

Settings are stored in SQLite in the `app_settings` table.

### Settings keys

Summarization:

- `openai.api_key`
- `summarization.master_prompt`
- `summarization.max_bullets_per_day`
- `summarization.include_diffs`

Mining:

- `mining.window_size_days`
- `mining.overlap_days`
- `mining.fill_gaps_first`
- `mining.backfill_order`
- `mining.include_merges`

Export:

- `export.default_directory`
- `export.last_dir` (last used export folder)

### OpenAI API key

Priority order used by the app:

1. `openai.api_key` (Settings UI / DB)
2. `OPENAI_API_KEY` environment variable

Set via UI:

- `Settings -> OpenAI API Configuration -> Save API Key`

Or via environment:

```powershell
$env:OPENAI_API_KEY="sk-..."
```

## Data storage

### SQLite DB

- Location: `%LocalAppData%\DevChronicle\devchronicle.db`
- Created automatically on startup
- Schema initialization and migrations live in `Services/DatabaseService.cs`

### Logs

- Location: `<repo>\logs\devchronicle_YYYYMMDD_HHMMSS.log`
- Keeps latest 10 log files
- Captures startup/runtime/unhandled exceptions

## UX walkthrough

### Dashboard

- Start page on app launch.

### Sessions page

Primary actions:

- Create session
- Open session
- Delete session

Session identity:

- sessions are identified by an integer `Id` in the DB
- session name is not an ID; it is display/UX

Create session defaults:

- Session name default format: `Session_MMDDYYYY_HHMMSS`
- Main branch is defaulted to `main` in DB and session creation

### Create Session dialog

Inputs:

- repo path (must contain `.git` folder)
- optional author filters (comma/semicolon separated; email tokens treated as email filters)
- mine all history toggle or date range
- include merges toggle
- ref scope selector (local / local+remotes / all refs)

### Session detail page

Tabs/panels (conceptual):

- day browser (left)
- summarization controls (top bar)
- mining controls and progress (status + progress bar)
- evidence view:
  - commit evidence (sha, subject, files)
  - branch evidence (grouped branch attribution)

Operations:

- Mine initial window (auto-starts if a brand new session has no days)
- Re-mine (clears mined data for the session range and rebuilds)
- Summarize selected day
- Summarize all pending days
- Stop summarization

### Export page

Export is a 3-step flow under one nav item:

1. Export Hub (session checklist)
2. Create New Export (target configuration)
3. Update Existing Diary (browse/preview/apply; unmanaged conversion)

Export Hub:

- session list items are checkable
- `Create New Export...` is enabled only when at least one session is selected
- per-session `...` menu exports that one session using Save dialogs

Create New Export:

- choose output folder
- set diary filename (customizable)
- toggle outputs: Diary / Archive
- format dropdown: `Markdown + JSON`, `Markdown only`, `JSON only`
- options:
  - Hide repo paths in Markdown
  - Include placeholders
- `Export` and `Cancel` (cancellable operation)

Update Existing Diary:

- browse an existing `.md`
- if managed: preview diff counts and allow apply update
- if unmanaged: allow conversion to managed copy, then update
- conversion binds to selected sessions only for unmanaged conversion

### Settings page

- OpenAI API key
- export default directory
- processing options (include merges/diffs, window size, overlap, backfill order, etc.)
- master prompt editor

## Mining subsystem

Mining is implemented by `Services/MiningService.cs` and `Services/GitService.cs`.

High-level behavior:

- optionally fetches remotes (`git fetch --all --prune --tags` equivalent in GitService)
- reads commits in a single pass with numstat and file list
- stores commits and day aggregates in SQLite
- stores evidence per commit:
  - additions/deletions
  - `files_json` with per-file churn
  - commit parents for merge analysis

Ref scope:

- local branches only
- local + remote-tracking branches
- all refs (includes tags, remotes, etc.)

Integration/branch attribution:

- captures branch tips (`branch_snapshots`)
- stores branch labels per commit (`commit_branch_labels`), including:
  - a primary label
  - additional branch containment labels
- can track integrations using:
  - merge commits
  - patch-id matching (when enabled)

Cancellation:

- mining accepts a cancellation token; long steps check cancellation and abort safely

## Summarization subsystem

Summarization is implemented by `Services/SummarizationService.cs` and orchestrated by `ViewModels/SummarizationViewModel.cs`.

Behavior:

- loads commit evidence for a specific day
- clusters commits into work units (`Services/ClusteringService.cs`)
- constructs a strict bullet-only prompt
- calls OpenAI `chat/completions`
- validates response:
  - only keeps lines starting with `- `
  - caps to `max_bullets_per_day`
- persists results to `day_summaries`

Important note about missing key:

- without an API key, summarization fails with a clear message and does not create summaries

## Evidence and day detail

The day detail view is evidence-first:

- commit evidence is stored and displayed as:
  - short sha
  - subject
  - file churn list (from `files_json`)
- branch evidence is derived from stored commit branch labels and displayed grouped by branch name

## Export subsystem

Reference spec: `docs/ExportSystemSpec.md`

Implemented by:

- `Services/ExportService.cs`
- `Services/ExportContracts.cs`
- `ViewModels/ExportViewModel.cs`
- `Views/ExportPage.xaml`

### Export outputs (v1)

Diary (Markdown):

- summary-driven
- multi-session aware
- includes a manifest for managed diaries
- includes stable DC markers for deterministic updates

Archive (JSON):

- evidence-rich
- includes sessions, days, summaries, commits, and parsed file evidence

### Safety guarantees

- exports are written with atomic behavior via temp files
- updates create backups when replacing an existing file
- cancellation leaves final files either:
  - unchanged (update)
  - absent (new export), and temp files are removed

### Per-session quick export

- accessible from Export Hub row `...` menu
- uses Save dialogs so users explicitly pick destination filenames
- exports run through the same `ExportService.ExportAsync` pipeline

## Managed diary file format (v1)

Managed diaries are updateable because they include a manifest header and stable markers.

### Manifest line (first line)

```text
<!-- DC:MANIFEST {"schemaVersion":"1.0", ... } -->
```

Fields (v1):

- `schemaVersion`: `"1.0"`
- `diaryType`: `"single"` or `"multi"`
- `sessionIds`: int[] sorted ascending
- `options.hideRepoPaths`: bool
- `options.includePlaceholders`: bool
- `options.summaryPolicy`: `"latestCreatedAt"`
- `createdAt`: ISO-8601 UTC
- `lastSyncedAt`: ISO-8601 UTC

### Day markers

```text
<!-- DC:DAY day=YYYY-MM-DD -->
## YYYY-MM-DD
...
<!-- /DC:DAY -->
```

### Entry markers (per day+session)

```text
<!-- DC:ENTRY day=YYYY-MM-DD session=123 summaryCreatedAt=2026-02-05T18:00:00Z -->
- bullet
- bullet
<!-- /DC:ENTRY -->
```

Update rules:

- only rewrite content inside DC markers
- preserve user-authored content outside markers
- order deterministically:
  - days ascending
  - entries within a day by repo name, then session name, then session id

## Progress and cancellation model

Export and summarization share a small operation state model:

- `ViewModels/OperationStatus.cs`
  - `OperationState` (`Idle`, `Running`, `Success`, `Canceled`, `Error`, `NeedsInput`)
  - `OperationStatusFormatter`

Progress correctness:

- UI counters are clamped at VM boundary so `(current/total)` never becomes invalid
- if a service emits `current > total`, VM clamps and logs a warning to debug output

Cancellation semantics:

- cancel requests are cooperative and use cancellation tokens
- cancel is treated as non-error (clear messaging)

## Database schema overview

Schema is created (and lightly migrated) by `Services/DatabaseService.cs` at startup.

Key tables:

- `sessions`: session metadata and options
- `days`: aggregated per-day stats + status
- `commits`: commit evidence + file churn (`files_json`)
- `day_summaries`: bullet summaries per (session, day, prompt_version)
- `commit_branch_labels`: branch attribution snapshots per commit
- `integration_events`: integration tracking anchors and details
- `branch_snapshots`: branch tips captured during mining
- `app_settings`: persisted app preferences and secrets (note: API key is stored here)

## Troubleshooting

### AI summarization does not work

- set API key in Settings or via `OPENAI_API_KEY`
- check logs under `logs/`
- if OpenAI returns non-2xx, the error includes the response payload

### Export update says unmanaged

- file does not contain `DC:MANIFEST`
- use `Convert to managed...` in the update flow

### Build issues due to locked output

- close running app instances
- build to alternate output folder:
  - `dotnet build DevChronicle.csproj -p:OutDir=artifacts\tmpbuild\`

### UI binding exceptions

- check the latest file in `logs/`
- bindings failing at runtime usually show the binding property name and stack trace

## Security and privacy notes

- API key is stored in `app_settings` (SQLite). This is not equivalent to OS credential storage.
- repo paths may appear in the app UI; markdown export can hide repo paths by default.
- archive exports contain commit evidence (subjects and file paths) and should be treated as sensitive.

## Known limitations

- Windows-first app; not cross-platform
- no automated tests yet
- unmanaged markdown is never edited in place
- caching of summarization by input hash is not implemented yet (placeholder TODO exists)

## Specs and docs

- `docs/HighLevelSpecs.md`
- `docs/MiningSystemSpec.md`
- `docs/ExportSystemSpec.md`

## Next milestones

- add automated integration tests (mining/export/update)
- add summarization caching via `input_hash`
- harden secret storage for API keys (Windows Credential Manager or DPAPI)
- add packaging/distribution (installer, signed builds)

## Contributing

Contributions are welcome.

- Open an issue for bugs or feature requests.
- Prefer small PRs that focus on one change.
- Include reproduction steps for bug fixes.
- Keep UI changes consistent with the existing WPF-UI visual language.
- Avoid schema changes unless discussed first.

If you’re making larger changes (export format/schema, mining strategy, or diary update rules), update the corresponding spec in `docs/`.

## License

This repository currently does not include a license file. By default, that means you should assume all rights are reserved.

If you want this project to be open source, add a `LICENSE` file (common choice: MIT) and update this section to match.
