# Live Monitor 9.4 verification — October 6, 2026

The Phase 1–5 Live Monitor upgrade was built for Revit 2024 as assembly **9.4.0.0**, installed using the clean deployment script, and tested in a separate Revit process on newly generated disposable host and linked models. User settings/rules were preserved; the native test used isolated settings and diagnostics.

## Results

| Verification | Passed |
|---|---:|
| Identity, change accumulation, DTOs, diagnostics | 142 |
| Scheduler, API resolver, view model and monitoring modes | 44 |
| Gateway dispatch, coalescing, document/session guards and wake retry | 21 |
| WPF Radar, dockable provider, bindings and structured log writer | 16 |
| Native Revit release integration | 25 |

The production build completed with zero warnings and zero errors. The installed DLL SHA-256 matched the fresh build:

`AFDCFEF798501047C98E93059CC5962701DC715F73999DCAB171A59CC036E995`

Native checks exercised host/repeated loaded-link overlaps, the registered dockable pane, current geometry inspection, queued navigation, pane hide/reopen, Trigger accumulation and explicit checking, Off event suppression, automatic Live edits/placement, confirmed deletion, global-change Full Scan requirements, stale navigation/inspection rejection, queued edits during an invalidated Full Scan, successful Full Scan generation renewal, link transform invalidation, and asynchronous structured diagnostics.

The synthetic run recorded a maximum live API callback of **27.089 ms** and **two rejected stale requests**. These measurements do not predict large-model performance.

The first native attempt failed because the test called the host-only scan method while expecting linked results. The test was corrected to use the linked scan entry point. The fresh second build passed all 25 checks. No engine change was needed for that failure.

## Diagnostics and completeness

Diagnostics record committed-change, batch, scan-start, first-detected-result, first-publication and completion timestamps; changed elements, candidates, tested pairs, Boolean failures, queue depth, maximum API callback duration, new/resolved clashes and stale requests. Request/batch/document/session identities correlate records. Empty-result scans have no first-result timestamp. Cancelled or paused batches have a terminal timestamp without falsely claiming scan completion.

The in-memory history holds 512 immutable records. A bounded background queue writes rotating JSONL logs; dropped records are counted. Radar displays counters and offers diagnostic JSON export. Live work remains local and cannot certify the whole project. Global input changes require Full Scan; excessive API duration preserves work and pauses checking until explicit recheck. Individual Revit geometry calls cannot be interrupted mid-call.

## Remaining acceptance limits

- Actual UI Undo/Redo was not exercised by this native release run. Automated tests verify same-UniqueId restoration and reconciliation; those tests do not replace the manual Revit Undo/Redo checkpoint documented in TESTING.md.
- Large production models, linked reload/unload in native Revit, and sustained performance under other add-ins require acceptance testing on authorized disposable copies. Stub tests cover link unload/replacement guards.
- Only Revit 2024 was built and tested. Earlier production baseline results belong to the previous build and were not rerun for this upgrade.

Native models, logs, settings, backups and binaries are ignored local evidence and are not committed to the public repository.
