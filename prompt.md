🧠 UNIVERSAL DEVELOPER DIARY MASTER PROMPT (Evidence-Driven, Max Detail)

You are generating a maximally exhaustive Developer Diary entry from the provided day-scoped mining evidence. Your goal is to capture as much technical detail as possible while staying grounded in the inputs.

Follow these instructions exactly.

✍️ FORMAT RULES

- Output plain bullet points only (each line starts with "- ").
- No headings. No numbered lists. No emojis.
- No blank lines between bullets.
- Use nested bullets only when logically necessary.
- Use inline backticks for code identifiers, files, commands, classes, services, APIs, config keys.

📌 COVERAGE AUDIT (DO NOT SKIP)
Before writing, scan ALL provided inputs. After drafting, do a second pass and ensure you included bullets for each category below IF relevant evidence exists. If there is no evidence for a category, omit it (do not invent).

📘 INCLUDE ALL OF THE FOLLOWING IF PRESENT

- What was worked on (features/bugs/maintenance), with concrete technical actions.
- Commit-level intent: subjects, themes, and notable changes.
- File-level evidence: key paths changed, churn, renames, risk areas.
- Architecture/design decisions: rationale + tradeoffs + impact.
- UI/UX behavior changes: interactions, layout, state, bindings, regressions.
- Backend/service/data changes: DB, migrations, queries, APIs, performance.
- Integration/branch context: merges, branch containment, integrations, patch matches.
- Testing/debugging/validation: test results, repro steps, logs, CI notes.
- Problems/frustrations/blockers and how they were handled.
- Experiments/prototypes/abandoned directions.
- Unresolved items and next steps.

🧾 ACCURACY / INFERENCE POLICY (MAX DETAIL, NO FABRICATION)

- Prefer over-including details that are present in the inputs.
- If you infer, tag the bullet with "[inferred]".
- If something is unclear/missing, tag with "[uncertain]" and state what would confirm it.
- Do not claim completion unless evidence shows completion.

🔍 WRITING STYLE

- Dense, technical, and traceable.
- Each bullet must be standalone and specific: what changed + where + outcome/impact.
- Avoid vague bullets ("worked on UI", "fixed bugs").

✅ OUTPUT CONTRACT

- Output only the bullet list. No preface. No closing text. No code fences.

INPUTS (EVIDENCE_BUNDLE):

- Session metadata (repo, session settings, range)
- Day date
- Day stats/status (commits/additions/deletions, mined/summarized/approved)
- Commit evidence (sha, subject, author/committer dates/names/emails if present)
- Per-commit file churn (paths, additions/deletions; include `files_json` if provided)
- Branch/integration evidence (branch labels, merges, integration events) if provided
- Test/log/CI snippets if provided

{{EVIDENCE_BUNDLE}}
