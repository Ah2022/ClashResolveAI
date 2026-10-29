# Changelog

This is the single release history for the cleaned repository. Earlier notes are summarized here rather than retained as separate versioned source trees or change documents.

## 9.4 coordination source update — 2026-10-29

- Full Scan profiles: Quick, Detailed and Advanced with a reviewed estimate required before Start.
- Immutable plans, cached explicit classification, project mapping review, candidate exclusions and compiled discipline/category filtering before spatial queries.
- Bounded workload estimates, historical timing ranges or an explicit unavailable result, and removal of expensive model relationships.
- Detailed saves its hard-clash pass before localized selected-rule clearance. Link-to-link and insulation remain explicit separate jobs. Cancellation preserves completed stages.
- Coordinator progress exposes relationships, floors, sources, candidate/geometry/Boolean counts, exclusions, unknowns and a source-rate ETA.
- Area-aware coverage retains untested clearance findings and prevents partial scope from certifying whole-host freshness. Saved history chooses a comparable configuration baseline across staged jobs.
- Full Scan settings and link display records remain separate from Live preferences and the shared Live link cache.
- Added native staged/cancellation fixtures, loaded-link benchmarks, reproducible release checks and a portable package script.
- Expanded README with a sourced Navisworks Manage comparison and an editable visual workflow/usage guide. Production acceptance and native check status are documented separately in `docs/FULL_SCAN_PHASE7_8.md`.

## 9.4.0 Full Scan dashboard renovation â€” 2026-10-07

- Added immutable scan history, coverage-aware comparisons, migration backups and audited lifecycle changes.
- Added Overview, Issues, Groups, History and Analytics with stable group identity, ownership defaults and split/merge lineage.
- Added current/historical issue/group BCF, Excel and Word export using copied scope/evidence/workflow data.
- Added automatic embedded 2D/3D clash previews with stale, historical and late-callback guards.
- Passed 395 automated checks (including a private optional recorded fixture) and 168 controlled native checks, including manual Undo/Redo, linked models, failed-save rollback and full/live isolation.
- Kept production-model performance, Word page visual QA and normal installation acceptance explicitly open. See `docs/DASHBOARD_RENOVATION.md`.
## 9.4.0 Live Monitor upgrade — 2026-10-06

- Fixed local scan starvation when input fingerprint validation exceeded the preferred slice target. Environment checks are polled, while completed results receive fresh validation after DTO capture. The hard responsiveness limit remains enforced.

- Document and session generation guard every queued live operation; one Revit gateway owns execution and coalesces requests.
- ChangeAccumulator and LiveScanScheduler preserve changes across cancellation and Full Scan, debounce local work, and pause after excessive API slice duration.
- Radar stores immutable DTOs. Navigation and inspection resolve current UniqueIds, geometry revisions and link environments inside Revit handlers.
- Registered dockable Radar uses commands and explicit Live, Trigger and Off modes, scan status and queue indicators.
- Correlated timestamps and scan counters appear in Radar diagnostics and bounded asynchronous JSONL logs. Diagnostic snapshots can be exported.
- Clean version-checked deployment backs up owned files and verifies every installed DLL against the fresh build.

## 9.4.0 — 2026-10-01

### Detection and result lifecycle

- Hard-only Full Scan defaults to a 1 mm³ overlap threshold; combined hard/clearance mode remains available.
- Unverified geometry is retained with its reason rather than promoted to a confirmed clash.
- Host-centric scanning defaults to excluding link-to-link pairs.
- Scoped reconciliation preserves unaffected issues and stable ClashIds.
- Full Scan completion clears pending restored-dependant and target bookkeeping after Undo/Redo.

### Live Monitor and Dashboard

- Dashboard receives only Full Scan results; Radar receives only live results.
- Drawn-element session tracking supports local rechecks, removal, restoration and Undo/Redo.
- Live completion cannot certify an older Dashboard snapshot for reporting.
- Type changes recheck up to 500 in-scope instances; broader input changes show a Full Scan notice.
- Radar filters, status changes and cancellation remain separate from Full Scan work.

### Cleanup and release verification

- Removed unused SimpleClashDetector, ClashPlacementUpdater, SpatialHashGrid, Core/SpatialOctree, and WinForms SettingsDialog.
- Updated assembly/manifest version to 9.4.0.0 and ribbon panel to ClashResolve AI 9.4.
- Passed 84 standalone checks, 136 Revit integration checks, inspector layouts and BCF validation. All 1,419 compared baseline result identities agree.
- Consolidated distribution into one source tree, one changelog, and current build/install instructions; excluded build archives, models and private verification evidence from Git.

## 9.3.0 — 2026-09-28

- Established the scan baseline and native geometry inspection workflow.
- Verified live movement, recurrence, deletion, restart and refresh behavior.
- Added responsive inspector layouts, floating/docked views and saved-view navigation.

## Earlier releases

- Added Revit coordination, rule-based clearance checks, reporting, optional AI text, and live monitoring.
- Addressed live-monitor startup and Revit API event-context issues. Historical implementation details referring to removed components do not describe the current release.
