# Full Scan phases 7–8: staged coordination and release verification

Source update: October 29, 2026. Revit 2024, .NET Framework 4.8, assembly version 9.4.0.0. This source update does not install into an existing Revit session.

## Phase 7 implementation

Detailed coordination orchestrates two independent jobs. First it completes and saves a hard-only scan for the reviewed host/link/floor scope. Then it derives immutable coordination areas from confirmed solid-overlap findings and runs selected clearance rules for elements touching the same confirmed area. The coordinator reviews the area radius before estimating and starting; the default is 2,000 mm. Unverified or clearance-only findings cannot seed areas. With no confirmed overlaps, clearance is skipped explicitly.

Each completed pass has its own persisted scan version and shares a workflow identifier in statistics. A cancelled/failed clearance pass never publishes partial results and does not erase the completed hard pass. Model, link, rule or mapping changes invalidate running work. Quick remains a hard-only job. Advanced can deliberately request broad clearance, explicit link-to-link pairs, or a dedicated insulation pass; neither cross-link nor insulation is appended silently to Detailed.

Area limits constrain sources before queries and require both candidate boxes to touch the same area before geometry. Coverage records the area policy and endpoint bounds. Absence outside that tested pair scope cannot resolve an old clearance issue or certify whole-host freshness. These are element-based areas, not geometric clipping of long elements: a long element touching an area can participate with another element touching that area.

Evaluation-policy fingerprints exclude changing confirmed-area boundaries, while complete plans and area evidence remain in captured configuration/scope JSON. Dashboard reconciliation selects a prior comparable policy even when another stage was saved in between. Actual current coverage, endpoint identity, model/link comparability and trustworthy physical evidence still govern absence. Changed or untested scope remains unevaluated.

Detailed's preflight estimate is a conservative two-pass upper bound because confirmed areas are determined only after the hard pass. Completed clearance-pass timing can calibrate a future Detailed estimate; absence of comparable timings remains explicit. The progress window shows stage, model relationship, floor, total and relationship sources, bounding-box candidates, geometry tests, Boolean calls, exclusions, unclassified counts and a source-rate ETA. ETA is a planning aid and resets per stage.

Full Scan uses detached link display records with proper Revit ModelPath conversion. It does not change the shared Live link cache. Full Scan settings and environment invalidation use Full Scan preferences. The targeted Live pipeline and its source files are outside these feature changes.

## Phase 8 reproducible verification and packaging

Run from the repository root:

```powershell
./scripts/Test-Release.ps1
./scripts/Test-Release.ps1 -Native
./scripts/Package-Release.ps1 -BuildOutput ./artifacts/fullscan-phase78
```

The automated gate builds production and runs identity/core, scan planning/classification/filtering/mappings/estimates/staging, Live scheduler, Live gateway, Dashboard history, Radar UI and inspector harnesses. It fails on a nonzero tool exit. Native verification uses a fresh disposable Revit process and newly created host/link fixtures; it restores registration byte-for-byte immediately after startup, never replaces installed DLLs, records the loaded assembly and retains failed attempts for diagnostics. Revit's unsigned add-in decision requires a manual Load Once action.

The native release suite includes prior scan planning/filtering checks, all three profile screens, estimates, relationship removal, stale approval rejection, actual Live environment hash isolation, the real command cancellation route, two-stage persistence, cancellation during clearance and a 250-source/two-loaded-instance spatial benchmark. Complete bounding-box estimates are compared with native and octree candidate counts. Fixture measurements are labelled as fixtures.

The package script creates a local ZIP with the add-in, deployment dependencies, manifest, rules, installer, usage map and SHA256 file inventory. It excludes Autodesk binaries, credentials, models and result databases. Packages remain under ignored `artifacts/`; source, rules, ribbon resources, build/deploy scripts and tests belong in Git. The installer requires all Revit sessions closed and performs its existing backup/hash validation.

## Current validation status

| Check | Result |
|---|---|
| Revit 2024 Release build | 0 warnings, 0 errors |
| Planning, classification, filtering, mappings, estimates and staging | 189 passed |
| Identity/core/serialization | 141 passed |
| Live scheduler/resolver/view model | 60 passed |
| Live gateway | 21 passed |
| Dashboard/history | 180 passed |
| Radar WPF/provider/diagnostic writer | 16 passed |
| Inspector visual/layout harness | Passed; 10 rendered layouts |
| Native Revit release suite | 81 passed |

**607 numbered automated checks plus the inspector visual harness passed. All 81 final-candidate native checks passed.** Final native evidence: `verification/fullscan-phase78/native-20261029-155127`. The staged assembly and candidate checksums match. The original registration was restored byte-for-byte and installed binaries were not replaced. Local detailed evidence and checksums are under ignored `verification/fullscan-phase78/`; source pushes remain distinct from installation or full production acceptance.

Validated assembly SHA256: `7D71F652E93ECA28A63061D26A292EF86E419C7D6DF01BD9EF670A1AD443316C`. All 23 Live Monitor/event/alert/app files match their pre-phase source snapshot and the incoming remote version. The earlier Phase 5–6 native gate is covered by this complete release suite.

Rendered Quick, Detailed, Advanced, classification and coordinator-progress screens were inspected. The 250-host-source/two-loaded-instance benchmark produced complete estimates agreeing with both query implementations. Its timings describe only that controlled fixture; no production speed claim is inferred.

Incomplete attempts exposed a test-window discovery issue and a missing null guard in streaming traversal of subdivided spatial trees. Both were corrected; a dispersed-element regression now exercises the tree case. One disposable Revit process exited during initial document creation before scan execution; that attempt is not counted. Staged persistence, localized clearance and cancellation passed before the streaming benchmark correction. Only a complete run on the final assembly can certify the candidate.

**Production release gate:** representative, authorized models with intended links loaded still need measured accuracy, wall time, memory and estimate-calibration acceptance. The fixture benchmark and historic evidence do not substitute for that gate. Dedicated native insulation geometry also depends on a template providing insulation types; eligibility and separate-job policy are covered independently. No universal performance or accuracy advantage over Navisworks is claimed.

## Documentation and repository publication

The README describes detection, classification, estimates, staged jobs, Dashboard coordination, Live Monitor, exports, requirements and limitations. Its Navisworks comparison cites Autodesk and distinguishes the Revit authoring feedback advantage from Navisworks' federation, point-cloud and time/animation strengths. `docs/USAGE_MAP.md` and `docs/images/coordination-workflow.svg` provide an editable visual and step-by-step map.

The repository audit covers all production source files, rule JSON, embedded ribbon PNGs, the add-in manifest, project file, installation/build/package scripts and tests. Generated builds, recovery backups, local evidence, user settings, API keys, models, databases and reports are excluded. Newer remote commits are integrated without force-pushing.
