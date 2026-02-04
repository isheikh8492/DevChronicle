# Session Detail Page Design Ideas

Below are multiple distinct directions. Each includes layout, key elements, and visual tone. Pick one or mix elements.

## 1) Session As Story
Sketch:
```
┌──────────────────────────────────────────────────────────────┐
│ Session 2026-02-04  •  Repo  •  3.2 hrs  •  Focus: High      │
└──────────────────────────────────────────────────────────────┘
  ● Day 1 ──┐  ┌─────────────────────────────────────────────┐
  ● Day 2 ──┼─▶│ Day 2 Summary                               │
  ● Day 3 ──┘  │ Metrics | Top Files | Action                │
              └─────────────────────────────────────────────┘
               ┌────────────────────────────────────────────┐
               │ Day 1 Summary                              │
               │ Metrics | Top Files | Action               │
               └────────────────────────────────────────────┘
```
Layout:
1. Full-width hero band with session title, repo path, date range, and status pill.
2. Left vertical timeline rail with day markers.
3. Right content column with stacked day cards.
4. Each day card: summary paragraph, key metrics row, top files list, primary action.

Key elements:
- “Story” header: Session 2026-02-04 with a short subtitle (“Focus run, 3.2 hrs, 12 files”).
- Timeline dots in accent color; today highlighted.
- Cards with a soft gradient header strip.

Tone:
- Narrative and reflective, like a timeline journal.

## 2) Analytics First
Sketch:
```
┌────────────┬────────────┬────────────┬────────────┐
│ Active     │ Commits    │ Files      │ Focus      │
│ 3.2 hrs    │ 12         │ 24         │ 82         │
└────────────┴────────────┴────────────┴────────────┘
┌───────────────────────────────────────────────────┐
│                  Activity Chart                   │
└───────────────────────────────────────────────────┘
┌─────────────────────────────┬─────────────────────┐
│ Summaries / Mining / Export │ Key Events          │
│ content                     │ timeline + badges   │
└─────────────────────────────┴─────────────────────┘
```
Layout:
1. Top row of metric tiles (Active Time, Commits, Files Touched, Focus Score).
2. Wide activity chart (time series or heatmap) spanning the page.
3. Right sidebar for key events and milestones.
4. Bottom section for Summaries / Mining / Export as tabs or segmented control.

Key elements:
- Crisp data hierarchy and clear numbers.
- Compact action bar aligned with metrics.

Tone:
- Productive, data-driven dashboard.

## 3) Command Deck
Sketch:
```
┌───────────────────────────────┬───────────────────┐
│ Session 2026-02-04            │ Summarize         │
│ Repo • 08:03–11:15            │ Export            │
└───────────────────────────────┴───────────────────┘
┌────────────────┬──────────────────────────────────┐
│ Day List       │ Day Detail                        │
│ ● 02-01 (done) │ Summary / Files / Commands / Notes│
│ ● 02-02 (todo) │                                   │
│ ● 02-03 (todo) │                                   │
└────────────────┴──────────────────────────────────┘
```
Layout:
1. Header split: left metadata, right CTA stack.
2. Two-pane workspace:
   - Left list of days with badges for status.
   - Right detail panel for the selected day.
3. Detail panel tabs: Summary, Files, Commands, Notes.

Key elements:
- Sticky actions: Summarize, Export, Open Repo.
- Clear list selection states.

Tone:
- Efficient and focused, like a control center.

## 4) Contextual Sections (No Top Tabs)
Sketch:
```
┌───────────────────────────────────────────────────┐
│ Session 2026-02-04  • Repo • 3.2 hrs  [Summarize]  │
└───────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────┐
│ Summaries  [All | Pending]                         │
│ content + CTA                                      │
└───────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────┐
│ Mining  [Basic | Advanced]                         │
│ content + CTA                                      │
└───────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────┐
│ Export  [MD | PDF | ZIP]                            │
│ content + CTA                                      │
└───────────────────────────────────────────────────┘
```
Layout:
1. Header with session metadata and a small inline action row.
2. Three stacked section cards: Summaries, Mining, Export.
3. Each section has its own mini-toggle and CTA.

Key elements:
- Each section card has a bold title and short helper text.
- Keep white space tight; avoid giant empty panels.

Tone:
- Calm, structured, easy to scan.

## 5) Minimal + Bold Typography
Sketch:
```
┌───────────────────────────────────────────────────┐
│  02 / 04 / 2026            Repo • 3.2 hrs         │
│  SESSION DETAIL             Focus: High           │
│                             [Summarize] [Export]  │
└───────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────┐
│ Selected Day Summary + Metrics + Key Files        │
└───────────────────────────────────────────────────┘
```
Layout:
1. Large date as typographic anchor on the left.
2. Right column with key stats and actions.
3. Single main content area with selected day details.

Key elements:
- Strong type scale, low-chroma background, one accent color.
- Understated dividers instead of boxes.

Tone:
- Editorial, confident, modern.

## 6) Timeline + Map View
Sketch:
```
┌───────────────────────────────────────────────────┐
│ Mini-map: ●──●──●──●──● (scrubber)                 │
└───────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────┐
│ Selected Day Details                               │
│ Summary | Metrics | Files                          │
└───────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────┐
│ Deep Dive (expand)                                 │
└───────────────────────────────────────────────────┘
```
Layout:
1. Top mini-map: condensed vertical timeline for all days.
2. Main area shows the selected day details.
3. Bottom expandable “Deep Dive” section for files/commands.

Key elements:
- Mini-map acts like a scrubber.
- Quick filter chips for “Only unsummarized,” “High activity,” etc.

Tone:
- Exploratory, fast navigation.

## 7) Session Builder (Progressive Reveal)
Sketch:
```
┌───────────────────────────────────────────────────┐
│ Session Overview (compact)   [Expand]             │
└───────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────┐
│ Summaries (expanded when needed)                  │
└───────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────┐
│ Mining (collapsed by default)                     │
└───────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────┐
│ Export (collapsed)                                │
└───────────────────────────────────────────────────┘
```
Layout:
1. Start with a compact overview card.
2. “Expand” reveals the summary and mining panels inline.
3. Export section stays collapsed until used.

Key elements:
- Keeps the view uncluttered.
- Expansion animations for delight.

Tone:
- Minimal and purposeful, lowers cognitive load.

## 8) Modular Cards Grid
Sketch:
```
┌───────────────────────────────────────────────────┐
│ Session 2026-02-04  • Repo • 3.2 hrs               │
└───────────────────────────────────────────────────┘
┌────────────────┬────────────────┐
│ Summary Card   │ Activity Card  │
└────────────────┴────────────────┘
┌────────────────┬────────────────┐
│ Files Card     │ Export Card    │
└────────────────┴────────────────┘
```
Layout:
1. Top hero.
2. Card grid below: Summary card, Activity card, Files card, Export card.
3. Cards can expand to full width.

Key elements:
- Each card has a distinct accent stripe.
- Clear per-card CTAs.

Tone:
- Flexible and contemporary.

## 9) “Session Summary + Drawer”
Sketch:
```
┌───────────────────────────────────────────────────┐
│ Session Summary + Metrics                          │
│ Narrative overview                                 │
└───────────────────────────────────────────────────┘
                            ┌──────────────────────┐
                            │ Tools Drawer          │
                            │ Summaries / Mining    │
                            │ Export                │
                            └──────────────────────┘
```
Layout:
1. Main page shows summary + key metrics.
2. Right drawer slides in for Summaries/Mining/Export (like tools).

Key elements:
- Keeps the main surface clean.
- Tool drawer opens when needed.

Tone:
- Professional, slightly enterprise.

## 10) Split Horizon
Sketch:
```
┌───────────────────────────────────────────────────┐
│ Overview + Metrics + Chart                         │
└───────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────┐
│ Sticky Action Bar                                  │
└───────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────┐
│ Day List + Selected Day Details                    │
└───────────────────────────────────────────────────┘
```
Layout:
1. Top half: overview and activity chart.
2. Bottom half: day list + details.
3. Sticky action bar between halves.

Key elements:
- Balanced visual weight.
- Avoids the long single column.

Tone:
- Grounded, balanced.

## Common Visual Tweaks (Apply To Any)
- Replace large white panels with cards that have visible headers.
- Tighten vertical spacing; avoid double padding between sections.
- Turn “Summarize All Pending Days” into a compact action bar near section header.
- Add a subtle background gradient or texture to the main content area.
- Use one accent color for CTAs; keep everything else neutral.
