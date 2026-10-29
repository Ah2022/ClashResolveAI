# Full Scan feedback — Phases 5 and 6

Implementation date: October 29, 2026. Source: `ClashResolveAI_v9_4`. Candidate: `artifacts/fullscan-phase56`.

## Phase 5 — review workload before starting

The estimator collects classification metadata and transformed bounding boxes in API-thread slices. It shares the frozen `ScanPlan`, candidate policy, compiled partition-pair rules and floor/zone logic with execution. It does not request solids, Boolean intersections or surface-distance calculations.

The review shows all loaded/unloaded link instances, non-type element counts per loaded source document, policy candidates/exclusions/unknowns for participating instances, selected floors/zones, candidate discipline pairs, possible source relationships, estimated bounding-box pairs and duration ranges. Repeated instances retain separate transformed workloads while raw counts reuse document inventory. Unloaded element counts are unavailable.

Possible source relationships count directed, metadata-allowed source/target combinations before spatial retrieval, excluding self identities. They are not unique clashes. Bounding-box counts remove reciprocal duplicates within the same model. Geometry candidate estimates remain upper estimates because connected elements, insulation owners, missing solids and actual shape complexity are not verified here.

Sampling uses a deterministic source reservoir, at most 512 completed source samples and a global 100,000 candidate-visit budget. Estimate queries stream results so a dense query does not first materialize every match. Small jobs can obtain complete bounding-box counts. Dense, capped or unsampled relationships have visibly broader ranges; capped upper bounds retain the full metadata potential. Relationship arithmetic uses 64-bit integers.

Duration uses comparable completed scans from the current project's history, matching mode, stage, rule content, tolerances, clearance selection and candidate policy. Collection/index, spatial-query and geometry-verification rates contribute separately, with a broad range and a warning about geometry complexity. Rows needing geometry rates remain unavailable if previous measurements contain none. No timing history means **Unavailable — insufficient comparable timings**. The tool does not invent an hours estimate for a new project, and the range is not a statistically calibrated confidence interval.

Uncheck an expensive model relationship in the workload table and recalculate. Removed rows remain visible for restoring the selection; their old costs describe the previous estimate and are excluded from new totals. This removal changes executable relationships, not just the display.

Start remains disabled until an estimate completes. Approval rebuilds the configuration and compares its full fingerprint and current model/link environment with the estimated snapshot. Model edits, link changes, rule/keyword content changes, saved project mappings, scope changes and relationship changes require recalculation. The command executes the exact approved immutable plan.

## Phase 6 — profile workflow

The ribbon Full Scan command and dashboard scan entry now use one profile dialog with Scope, Models & relationships, Rules & classification, and Estimate & start tabs.

| Profile | Behavior |
|---|---|
| Quick coordination — default | Hard only; host-host and host-selected-loaded-links; one floor or bounded floor/workset zones; standard discipline matrix; structural framing/columns included; generic models, insulation and support families excluded. No internal/cross-link scanning. |
| Detailed coordination | Hard plus explicitly selected clearance rules; selected floors/zones; host relationships and optional inside-link scanning; no cross-link or generic/insulation scan. |
| Advanced | Explicit links and link pairs; discipline/category subsets; mapped generic/support choices; numeric tolerances; optional entire-model scope. Cross-link is an explicit-pair job on its own. Insulation requires its dedicated clearance pass. |

The current view's floor is selected when available, otherwise the first floor. Zones can constrain floor, user workset and optional linked model. An entire-model scope is available only in Advanced. Grid-range zones still fail explicitly; no unsupported grid boundaries are inferred.

Detailed/Advanced clearance modes require at least one selected clearance rule; an empty selection never silently means every rule. Detailed also requires at least one selected rule applicable to the actual estimated metadata partitions. Advanced discipline/category restrictions constrain both endpoints and cannot enable unsupported discipline pairs.

Classification review and project mapping editing remain accessible within the workflow. Quick is selected each time the dialog opens; advanced options are deliberate per-job selections. This phase does not introduce an automatic multi-stage queue or the later coordinator progress redesign.

## Live Monitor isolation

All 23 files in `src/LiveMonitor`, `src/Events`, `src/Alert`, and `src/App.cs` are byte-identical to the Phase 5–6 backup. `verification/fullscan-phase56/live-source-audit.json` records the comparison. Existing dashboard source files are also unchanged in this phase; its scan action reaches the updated shared Full Scan command.

Full Scan has separate rule, tolerance, structural, connected-joint, linked-model and internal-link preferences. Existing legacy values remain available to Live Monitor. Migration copies shared values once into Full Scan fields without resetting the legacy `ScanWithinLinks` value. The Settings window again exposes Live Monitor's existing generic/insulation/support controls, labels its shared numeric values as Live settings, and separates the Full Scan loaded-link default. Profile approval saves only Full Scan fields.

Shared files changed where Full Scan needs them: settings, plan/pair policy, full-scan coordinator entry, spatial scope and engine plan execution. The existing targeted Live scan branch and existing octree `Query()` behavior are retained. The estimator adds a streaming spatial iterator used only by estimation.

## Verification and release state

The Release build and automated suites are recorded under `verification/fullscan-phase56`. Native verification uses a separate Revit process and newly created disposable host/link models. The registration is restored byte-for-byte immediately after startup, and installed binaries are not replaced.

| Check | Result |
|---|---|
| Revit 2024 / .NET Framework 4.8 Release build | 0 warnings, 0 errors |
| Plan, classification, filtering, mappings, profiles and estimates | 169 passed |
| Existing identity/core/serialization | 141 passed |
| Existing Live scheduler/resolver/view model | 60 passed |
| Existing Live gateway | 21 passed |
| Existing dashboard/history | 180 passed |

**571 automated checks passed.** Optional recorded production fixtures were absent and explicitly skipped in their logs. Counts and candidate checksum are recorded in `verification/fullscan-phase56/acceptance.json`.

**Native UI/geometry verification is pending.** The first disposable run, `verification/fullscan-phase56/native-20261029-150230`, waited for manual Load Once at Revit's unsigned add-in prompt and timed out before add-in startup. No native checks ran and no rendered UI acceptance is claimed. The registration was restored byte-for-byte, and only that unused disposable process was terminated after checking its exact PID and start time. The staged first attempt predates the final clearance-selection guard and display refinements; it cannot certify the final candidate.

The [computer-use skill](C:/Users/TK56/.codex/plugins/cache/openai-bundled/computer-use/26.1023.51421/skills/computer-use/SKILL.md) requires: “Do not act on security or privacy permission requests.” The manual Load Once action is therefore required for a future native run. It is not another implementation-plan approval.

This candidate is not installed. Production-model accuracy/performance, calibrated duration quality, automatic staged execution and the later progress UI remain outside this phase's acceptance.
