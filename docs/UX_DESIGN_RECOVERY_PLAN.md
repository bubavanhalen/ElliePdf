# ElliePdf: product, UX and design recovery

Status: execution authorized; R0/R1/R2 in progress. Visual direction still requires user approval before broad UI implementation. Current evidence: [UX_RECOVERY_PROGRESS.md](UX_RECOVERY_PROGRESS.md).
Date: 2026-09-07. Reviewed branch: `codex/production-ready-reader`, HEAD `4184526`, including existing uncommitted development fixes.

## 1. The corrected goal

ElliePdf must be fast, immediately understandable, and visually excellent. **Speed, UX and design are equal, independent acceptance gates.** A fast but confusing reader fails. An elegant but unreliable reader fails. A usable but sluggish reader fails. Scores cannot be averaged to compensate for a failed pillar.

Correctness, document integrity, security and accessibility are prerequisites across all three pillars. Passing builds and unit tests proves only part of correctness; it does not prove product quality. The recent desktop test is not a successful product acceptance test.

Keep native WinUI 3, .NET 11 preview and the newest compatible, exactly pinned preview dependencies. Preserve worker isolation, bounded rendering and transactional saving. Do not start another engine rewrite or downgrade the toolchain to address UI problems.

This plan supplements `EXECUTION_SPEC.md`, especially WP-09, WP-10 and WP-11. Its product acceptance and review checkpoints are mandatory additions, not replacements for existing performance, security or release requirements. `LOCAL_DEVELOPMENT_PLAN.md` covers local launch readiness only.

## 2. What the evidence actually shows

| Finding | Evidence and confidence | Required response |
|---|---|---|
| The result looks worse and functions are hard to find | Direct user acceptance failure | Reopen design acceptance; compare with the earlier experience before proposing changes |
| Commands lack visible labels and important actions are in overflow | Current theme sets `DefaultLabelPosition=Collapsed`; Reader command hierarchy confirmed in XAML | Define command priority and visible labels; test discovery without coaching |
| Single-document identity is weak | Tab strip appears only when `TabCount > 1` | Always show document name and relevant dirty/locked state |
| Closing a pane could close the application | Observed caption/pane hit-target collision; a 50-pixel margin was added as a stopgap | Replace magic offsets with a coordinated shell and real caption insets; retest all scales |
| Text drag/double-click and copy did not work in the smoke test | Observed on the vector fixture; root cause not established | Reproduce selection, hit testing, focus and input ownership before choosing a fix |
| Encrypted open silently retained the prior document without a password prompt | Observed; static audit found a UI-thread-affinity hazard in password prompting, but the precise runtime path is unproven | Make prompt dispatch and open outcomes explicit; test cancellation, retry and concurrent opens |
| Initial F5 launch crashed during XAML initialization | Observed; title initialization fix subsequently allowed launch | Keep a fresh-install/F5 regression check; do not equate that fix with product readiness |
| Rendering, page navigation, zoom and one search worked | Limited vector-fixture smoke evidence | Preserve these behaviors and broaden coverage to realistic reading sessions |
| Forms/save-copy, corrupt files, multi-tab workflows and long-document interaction are not fully validated | Incomplete desktop acceptance coverage | Mark unverified, not passed; complete journey tests before readiness claims |

The pre-rewrite commit `363243e` is a candidate comparison baseline, not an assumption about the user's preferred version. Its source includes rounded floating toolbars, themed surfaces and a combined page label. Those are design inputs, not proof that every old interaction should return. Compare actual screenshots and behavior before deciding what to retain.

## 3. Proposed experience contract

This is a proposal to prototype and approve, not permission to replace the UI immediately.

### Structure and command discovery

- A coherent native window header owns document identity, tabs and caption/drag regions. The document name remains visible with one file open. Long names truncate predictably and expose the full name.
- A stable, compact command row below the header groups **Open / Save / Print**, **Pages / Outline / Find**, and **page / zoom / fit controls**. Use visible text for important actions at ordinary desktop widths; tooltips include shortcuts but are not the primary explanation.
- Show editable **page N of M**, zoom percentage and an understandable fit mode. Their positions do not move when status messages appear or a second document opens.
- Pages and Outline share a left navigation pane. Find has a stable search surface with query, result count, next/previous and clear/close. Prototype a right results pane; do not silently move the same navigation pane between sides. At narrow widths use one intentional transient pane with explicit focus return.
- Reading content occupies a structural layout region rather than sitting under permanent floating chrome. Opening a docked pane preserves the reading anchor; transient overlays close predictably with Escape. A document click must not unexpectedly dismiss a docked pane.
- Use a visible, named menu for secondary commands and settings. Do not require users to discover a hamburger plus anonymous overflow to perform ordinary reading tasks.
- Focus mode is an optional, reversible reading preference, not the default remedy for an obstructive shell. It has an obvious exit and preserves document state.
- At narrow widths collapse explicitly ranked command groups into a labeled menu, keeping document identity, navigation and Find readily reachable. Validate the existing 500×320 minimum and enlarged text; do not rely solely on automatic overflow.

### Visual identity

- Recover the original app's strongest visual qualities through side-by-side review: warmth, restrained accent, surface treatment and document focus. Avoid substituting a generic toolbar-heavy utility or adding decoration to hide layout problems.
- Define shared tokens for spacing, typography, icon size, control geometry, radius, elevation, colors and motion. Use one hierarchy and one icon family throughout reader, panes, dialogs and settings.
- Use quiet page surroundings, consistent page spacing, crisp text at settled zoom, restrained shadows and purposeful contrast. Avoid wasted margins, excessive translucent layers and notifications sitting over readable text.
- Specify empty, opening, password, error, no-results, permission-denied, dirty, saving and recovered states as carefully as the ideal open-document screen.
- Design light, dark and High Contrast together. Preserve focus visibility, system text scaling, reduced motion and touch targets; compact must not mean difficult to hit.
- Use native caption reservations and supported title-bar integration rather than hard-coded exclusion margins. Microsoft's [title-bar guidance](https://learn.microsoft.com/en-us/windows/apps/design/basics/titlebar-design) and [command-bar guidance](https://learn.microsoft.com/en-us/windows/apps/design/controls/command-bar) inform platform behavior; user-approved comparisons determine ElliePdf's appearance.

### Interaction reliability

- Ordinary mouse selection and copy work without entering an editing mode. Verify word, line, multi-line and cross-page selection, Unicode, zoom, scrolling and permission restrictions. Preserve standard text-field editing behavior.
- Separate mouse selection, touch scrolling, pinch, links and form interaction explicitly. Diagnose transforms, hit-test layers, focus and virtualized lifetimes; do not assume larger invisible hit targets solve selection.
- Password prompting owns UI dispatch of root access and dialog creation. Distinguish missing UI host, wrong password, user cancel and open failure. Do not hold a workspace-wide lock across user interaction; concurrent opens must not race dialogs or lose results.
- Open, search, save, print and recovery expose meaningful progress, cancellation and outcomes. Success messages are unobtrusive; actionable failures remain accessible. No silent failure and no modal success interruption.
- Basic form Save/Save As is discoverable outside Labs. Audit older annotation, signature and Organizer workflows explicitly: retain, repair, or defer with the user's agreement. Do not declare lost functionality solved by hiding it.

## 4. Execution work packages and checkpoints

### R0 — Freeze regression growth and establish the baseline

Deliverables: side-by-side baseline/current captures using identical PDFs, viewport, theme and scale; feature inventory; reproducible defect register with severity, expected/actual behavior and evidence; classification of existing uncommitted changes as retain/rework/unverified.

Include the user's real reading workflows and representative local PDFs with consent; retain privacy-safe synthetic fixtures for CI. Record which original version is being compared. Keep originals read-only during save/export testing.

Exit: every known regression has an owner and reproduction; removed or hidden features have an explicit disposition. No blind reset, feature expansion or unrelated refactoring. Local-launch fixes are separated from product-quality claims.

### R1 — Approve design and interaction before broad implementation

Deliverables: command map; shell layout and responsive rules; design tokens; focus/input contract; high-fidelity comparisons of an evolved original design and the proposed structured shell using actual PDF content. Show empty, single-tab, multi-tab, navigation, Find, form, password and error states in light/dark, wide/narrow layouts.

Validate the preferred direction with a small native WinUI prototype so title-bar hit testing, density, text scaling and input behavior are real, not just attractive images. This prototype is a design test, not another full rewrite.

Exit: user approves a direction and identifies what to preserve; unresolved primary-command discovery or visual objections block broad shell implementation. Deliver screenshots plus a short interaction recording and a concise tradeoff list. Design approval cannot be inferred from silence.

### R2 — Repair core behavior independently of shell styling

Deliverables: selection/copy reproduction and fix; explicit password/open state machine; coherent pane/focus/Escape behavior; tab identity/activation/close correctness; error and permission handling. Add regression tests at the relevant engine, view-model and real-window layers.

Exit: known selection, password and accidental-window-close failures have passing real-app reproductions. Test the actual path from user input to visible result, not only direct view-model calls. Each repair has a narrow reviewable diff.

R2 diagnosis and tests can run alongside R1 design. Shared UI changes wait for the approved shell contract; one integration owner controls input routing and window layout.

### R3 — Finish one exemplary reading slice

Implement the approved shell for: launch → open PDF → identify document → read/scroll → select/copy → Find → go to page → zoom/fit → close. Include empty/loading/error states, keyboard operation and both primary themes immediately.

Exit: this whole slice passes design review, unassisted task testing and performance checks in the installed application. Show it to the user before expanding. Do not scatter half-finished styling across all screens.

### R4 — Complete the remaining journeys and feature parity decisions

Integrate multi-document workflows, outline/thumbnails, links, encrypted files, forms/save/reopen, print/cancel, restore/recovery and settings using the same approved components. Implement agreed repairs to previous workflows; keep explicitly deferred features out of completion claims.

Exit: the journey matrix below passes; commands and state transitions remain consistent across documents, permissions and window sizes. Re-audit discoverability after adding commands.

### R5 — Prove the three pillars on the release candidate

Run performance benchmarks, visual comparisons, accessibility and unassisted usability tests against the same candidate commit and installed package. Include supported x64 and ARM64 hardware, DPI changes, mixed monitors, narrow windows and realistic long reading sessions.

Exit: all independent gates below pass, no critical/high-severity product defects remain, and the user accepts the actual application. CI green, F5 success or an attractive prototype alone cannot close this package. Existing signing/security/canary requirements remain mandatory for production.

### Parallel work and review ownership

After authorization to execute, use lower-cost agents for bounded fixture creation, regression tests, feature inventories, localization audits and documentation. Run behavior diagnosis and design preparation in parallel. Keep one lead responsible for design judgment, shared-shell architecture, interaction integration and final evidence review.

Agents work in disjoint files where practical; agree on contracts before dependent implementation. No concurrent independent redesigns of ReaderPage, MainPage or theme resources. Integrate and test small commits per defect or vertical slice. Do not batch-push unreviewed parallel changes and call the combined result complete.

## 5. Mandatory real-app journey matrix

| Journey | Acceptance evidence |
|---|---|
| Fresh launch and open | F5 and installed launch; picker, drag/drop and file activation; readable first page; clear identity; cancel leaves prior state intact |
| Read and navigate | Wheel/touchpad/touch, page jump, thumbnails, outline, single/continuous, zoom/fit; correct page count and stable reading anchor |
| Select and copy | Drag/word/line/multi-line/cross-page Unicode selection; exact clipboard content; after zoom, virtualization and tab switches; denied copy explained |
| Find | Query, progressive results, next/previous, no results, clear, Escape and cancel; stale results never replace a newer query or another document |
| Multiple files | Open two and duplicate paths; switch/reorder/close; per-file page/zoom/search retained; correct dirty prompts; single-file name stays visible |
| Password and failure | Correct/wrong/empty password, cancel/retry, concurrent open, corrupt/missing/denied file and worker exit; clear outcome without freezing or losing existing tabs |
| Links and forms | Internal navigation, external-link confirmation policy, keyboard traversal, edit/reset, read-only permissions and unsupported-content notices |
| Save and reopen | Save/Save As, cancellation, destination failure, dirty close and recovery; reopen and compare values/content; interrupted save does not damage source |
| Print | Current/all/range, orientation, permission denial and cancellation; verify output and responsiveness without submitting unintended physical jobs |
| Keyboard and accessibility | Complete reader journey without mouse; predictable focus and Escape; Narrator and UIA checks; text scaling, High Contrast and reduced motion |
| Long session and recovery | Large/scanned/mixed-size/rotated PDFs, rapid open/close, long scroll, sleep/resume and worker restart; bounded memory and recoverable state |

Each result records fixture, commit, package/configuration, hardware, steps, expected outcome, actual outcome and evidence. Include unsuccessful attempts. An automation tool failing to inspect the WinUI tree is an investigation item, not proof of either accessibility success or failure. Manual evidence is required where automation cannot yet observe the outcome.

## 6. Equal release gates

### Speed gate

Retain the execution specification's reference hardware, methodology and SLOs: cold shell p95 ≤600 ms; representative 10–20 MB first-page cold p95 ≤800 ms; cached navigation ≤50 ms; scrolling p95 frame ≤16.7 ms and dropped frames <1%; bounded long-document memory. Apply the full existing SLO table, not only these examples.

Measure input-to-visible feedback as well as render throughput. No greater than 5% median regression against the accepted baseline without repeat measurements and explicit investigation; passing this relative check never excuses missing an absolute SLO. Publish distributions and configuration, not cherry-picked runs. Do not claim “fastest” without reproducible competitor measurements.

### UX gate

All mandatory journeys pass without data loss, silent failures, wrong-document actions or accidental app closure. No unresolved defect that blocks a core task or requires a workaround.

Proposed formative usability targets: at least five people unfamiliar with the new UI; at least 90% unassisted completion across core tasks and at least four of five participants completing each core task. At least four of five can locate Open, Find, Pages and zoom/fit within five seconds per command. Report counts, errors and time separately; this small pilot is a design check, not population-level statistical proof. No coaching or shortcut hints during discovery tests.

### Design gate

The user approves both the design prototype and the integrated release candidate. All specified states share the approved tokens and hierarchy. Side-by-side review finds no unresolved clipping, overlap, incoherent spacing, unreadable contrast, missing interaction states or unapproved visual regressions. Validate real PDF content, not only an empty window.

Capture light/dark/High Contrast, wide/narrow/minimum supported size, 100/150/200% display scale and 200/400% text scaling with a targeted risk-based matrix. Review mixed-DPI transitions and long/localized labels. Automated image diffs flag changes for review; they cannot decide whether a design is good.

### Completion evidence

A candidate is **ready for local testing** only when its documented smoke journey passes and remaining limitations are clearly listed. It is **product-accepted** only when all three gates pass. It is **production-ready** only when product acceptance and all existing safety, packaging, signing, architecture and release gates pass on the final artifact.

Publish one evidence index with links to screenshots/recordings, journey results, defects, benchmark data, accessibility results and CI/package identity. Never substitute the number of completed work packages or passing unit tests for these outcomes.

## 7. Immediate next milestone

On approval to execute: complete R0, present the R1 baseline comparison and preferred native prototype, and reproduce the R2 blockers in parallel. The first milestone is **a trustworthy design decision and repaired core interaction**, not another claim that the whole application is finished. Re-estimate remaining work after those results; do not promise a completion date before the scope of regressions is known.
