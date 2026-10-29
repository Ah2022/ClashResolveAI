# Full Scan feedback — Phases 1 and 2

Implementation date: October 29, 2026. Source: `ClashResolveAI_v9_4`.

Later update: [Phases 3 and 4](FULL_SCAN_PHASE3_4.md) are now implemented and verified. The notes below retain the Phase 1–2 implementation and acceptance history.

## Implemented

### Phase 1 — immutable plan and independent scan relationships

- `ScanPlan` freezes the project identity, profile/stage, mode, selected link **instance** IDs, explicit link pairs, floor/zone snapshots, rule-set content, keyword lists, candidate policy, mappings and numeric tolerances. Its collections are defensive read-only copies. It contains no API handles or application credentials.
- Full Scan executes only `ScanPlan.Relationships()`. Host–host, host–selected links, inside selected links, and explicit link–link pairs are independent. No production path generates all link pairs. The legacy `includeLinkToLink` argument alone cannot create pair selections.
- The existing options dialog exposes four independent switches, a loaded-instance checklist and an explicit two-link pair picker. This is the transitional UI; the three-profile workflow remains Phase 6.
- `CreatePlannedJob` validates project ownership, loaded selections, a frozen rule snapshot and floor existence. Unsupported restrictions fail explicitly instead of silently widening scope. A missing selected floor never becomes a whole-model scan.
- Settings migration turns internal-link/link-to-link scanning off and establishes separate economical **Full Scan** candidate preferences. Live Monitor mode and its existing generic-model/insulation/support preferences are preserved. Migration is in memory until settings are saved.
- Scan capture/history includes the frozen plan and exact relationships. Eligible endpoint coverage prevents a newly excluded target from being marked resolved merely because its former partner was scanned. Partial selected-link jobs cannot certify whole-host freshness; complete whole-host coordination retains certification.

### Phase 2 — cached classification and policy before indexing

- `ElementClassification` records discipline, stable category ID/name, system type/classification, family/type, candidate eligibility/exclusion reason, confidence, evidence and mapping identity.
- `RevitElementClassifier` reads category and locale-independent `MEPSystemType.SystemClassification` enum values. Pipe services use system classification before keywords. Categories with their own coordination identity, including conduit, retain that identity. Type keywords precede family/type fallback. Conflicting connected systems or conflicting mappings require review.
- A job caches classification by stable document identity and element ID. Repeated instances share classification but keep separate transformed boxes and result identities. A new job discards classification and geometry caches.
- Full Scan collection uses a physical-category allowlist. Its candidate policy rejects construction aids/support names and unclassified or disallowed generic/specialty models before bounding-box extraction/index insertion. Unsupported categories cannot be admitted by a mapping.
- Framing and columns remain eligible by default. Walls/floors/ceilings enter only when the selected rule set has an explicit positive requirement for them. Generic fallback alone does not include them.
- Insulation/lining stays out of ordinary hard and coordination passes, even if inclusion is enabled. The policy admits it only for an explicitly enabled insulation-clearance stage. Stage orchestration/UI follows later.
- Classification results feed candidate verification and result metadata, avoiding repeated service/family classification per pair. Statistics add unique classifications, unknown classifications and exclusions before indexing.
- Immutable mapping records support category and family/type conditions, priority, classification overrides and explicit exclusions. Project persistence and coordinator editing remain Phase 4.

The optimized policy applies to Full Scan. The existing targeted/Live query path and its saved candidate preferences are retained.

## Verification

| Suite | Passed | Evidence |
|---|---:|---|
| Revit 2024 / .NET 4.8 Release build | 0 warnings, 0 errors | `verification/fullscan-phase12/build.log` |
| Scan plan, migration, classification, exclusions, scope/lifecycle | 66 | `verification/fullscan-phase12/planning-tests.log` |
| Existing identity/core/serialization | 141 | `verification/fullscan-phase12/identity-tests.log` |
| Existing Live scheduler/resolver/view model | 60 | `verification/fullscan-phase12/live-tests.log` |
| Existing Live gateway | 21 | `verification/fullscan-phase12/gateway-tests.log` |
| Existing dashboard/history | 180 | `verification/fullscan-phase12/dashboard-tests.log` |

Total automated checks: **468**. Optional recorded production fixtures were absent in the current harness locations and are explicitly skipped in their logs. These numbers are not production scan measurements.

Native verification is opt-in via `tests/Run-ScanPlanningVerification.ps1`. It creates disposable fixture documents in a separate hidden Revit process, records its loaded assembly path, restores the original add-in registration byte-for-byte immediately after startup, and closes only its own process after success. It does not replace installed binaries or open the user's project. The final accepted native run and assembly checksum are recorded in `verification/fullscan-phase12/acceptance.json`.

Native checks cover zero indexing/geometry/Boolean work for excluded-only geometry, repeated/transformed links, cached sanitary classification, generic-model mappings, selected pair execution, independent internal-link scans, rule-required floors, scope validation, transitional controls, cache reset, plan/history capture and separate Full Scan settings.

**Final candidate native acceptance: 27 passed checks**, in `verification/fullscan-phase12/native-20261029-122904`, on assembly SHA256 `63A4CCCF508E77875C05936EAB2068C82FF350D24E77F2D550B81A07347A7D60`. Registration restoration and matching staged/candidate hashes were independently checked. Earlier 20-check and 23-check runs also passed. One run of the final assembly exited during new-document initialization before scan execution; its evidence is retained in `native-20261029-122517`. The unchanged assembly passed all 27 checks on retry. The cause of that Revit process exit is not established.

The stock metric fixture has no `PipeInsulationType`; the dedicated-insulation eligibility policy is tested with plain inputs. Native insulation geometry belongs to the later staged-clearance verification. Large production-model performance, estimate calibration and final profile UX are not claimed here.

## Next phases

- **Phase 3:** compile allowed discipline/category pairs and partition spatial indexes; move pair pruning ahead of `Candidates()`. Until then nonempty discipline/category/rule-subset selections in a plan are explicitly rejected.
- **Phase 4:** project mapping persistence, classification review UI and coordinator actions.
- **Phase 5:** inventory, bounded spatial estimates, duration calibration and expensive-pair removal based on workload.
- **Phase 6:** Quick/Detailed/Advanced profile workflow, replacing this transitional dialog.
- **Phase 7:** stage orchestration, confirmed clearance areas, dedicated insulation job, coordinator progress/ETA and complete coverage reconciliation.
- **Phase 8:** broader native regression and representative loaded production-model performance/release acceptance.

Floor/level and workset zones retain the existing implementation. Grid-range boundaries are explicitly rejected; silently treating them as unbounded scope would be unsafe. Multi-floor/zone lists are supported by the plan and execution model, while the current picker still exposes its existing floor/active-zone controls.

## Candidate and preservation

Candidate binaries: `artifacts/fullscan-phase12`. No installer was run in deployment mode. Normal installed binaries remain unchanged.

Before-change source: `../backups/fullscan-phase12-before-20261029` (outside the project compile tree). Pre-existing uncommitted changes were preserved. `verification/fullscan-phase12/baseline-status.txt` records their initial Git status; `source-changes.json` records this work relative to that source snapshot.

Reproduce the focused checks:

```powershell
dotnet build ClashResolveAI.csproj -c Release --no-restore -o artifacts/fullscan-phase12
dotnet run --project tests/ScanPlanningHarness/ScanPlanningHarness.csproj -c Release
./tests/Run-ScanPlanningVerification.ps1
```
