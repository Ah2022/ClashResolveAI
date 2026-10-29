# Full Scan feedback — Phases 3 and 4

Implementation date: October 29, 2026. Source: `ClashResolveAI_v9_4`.

## Phase 3 — filtering before spatial queries

Full Scan now builds separate indexes for each model instance and classified discipline/category partition. Each source uses a compiled metadata rule decision before querying a target partition. Unsupported or ignored groups receive no bounding-box query, solid extraction or Boolean test.

The frozen plan captures the predefined discipline matrix. Generic fallback applies only to supported pairs, explicitly requested discipline pairs, or pairs enabled by an explicit category/discipline rule. Generic fallback no longer enables every possible discipline combination. Explicit user category/discipline restrictions constrain both endpoints and cannot be widened by an override.

Category rules retain precedence over named matrix rules, followed by discipline rules and supported generic fallback. Numeric category IDs, native category names and legacy friendly aliases remain supported. Rule decisions are cached by metadata partition pair, and results identify the actual resolved rule. Rule objects returned for geometry verification cannot mutate the compiled policy.

`ScanPlan.DisciplinePairs`, `CategoryIds` and `ClearanceRuleKeys` are now executable restrictions rather than unsupported placeholders. In a combined hard/clearance scan, clearance selection controls clearance testing; valid hard clashes from other allowed pairs are still tested. A clearance-only stage skips unselected rules. Reciprocal category/discipline rule selections work in either endpoint order. The later profile workflow will expose the full rule-selection controls.

Each allowed partition uses its own effective clearance radius. Full-scan floor/zone filtering caches level bands and reduces indexed elements using a conservative envelope of the complete selected source bounds plus the maximum required clearance. Boundary-crossing sources retain nearby targets beyond the nominal floor band. Workset/link source restrictions remain part of source coverage. Grid-range zones remain explicitly unsupported.

Coverage records eligible endpoint partitions and separate hard/clearance permissions. A previously observed issue from a pruned group or an unselected clearance rule remains unevaluated instead of being marked resolved. Restricted jobs do not certify whole-host freshness.

Statistics add actual spatial queries, partition checks/rejections and exclusions before indexing. `ExcludedRelationshipsBeforeSpatial` counts directed potential relationships rejected from metadata partitions; it is not a measured geometric-candidate or unique-pair count.

The existing targeted/Live query pipeline remains unchanged.

## Phase 4 — coordinator review and project mappings

The existing Full Scan dialog now has **Review classification and project mappings…**. The review window inventories only the host/link documents participating in the selected relationships, using copied metadata and short API-thread batches. It does not request geometry. Repeated instances of one source document share inventory metadata; counts describe unique elements in source documents.

The classification table displays category, family/type, current classification, confidence, suggested classification, system type/classification, evidence, inclusion/exclusion reason and source models. Search and an Unknown/lower-confidence filter help review the inventory.

Actions:

- **Keep:** acknowledges a known current classification without creating an override.
- **Confirm / Confirm suggestion:** creates an explicit draft mapping. A suggestion never silently changes a generic model's current Unknown classification.
- **Remap:** opens a condition editor and requires a known discipline or explicit exclusion.
- **Exclude:** previews an explicit exclusion and its source in the table.

Confirmations use exact family/type, category and system context. They do not use a broad substring that could accidentally match another longer type name or an unrelated system. General reusable mappings can use **Contains** or **Exact**, an optional category, optional exact system constraints, a target discipline/exclusion and priority. Higher priority wins, then match specificity; conflicting equal matches remain Unknown with a review explanation.

The **Project mappings** tab supports adding, editing, removing, reloading, importing and exporting a draft. **Save project mappings** applies changes. Closing without saving leaves persisted mappings unchanged. Import validates before replacing the draft; it does not silently overwrite another project's stored configuration. The **Keyword fallbacks** tab exposes the legacy keyword lists and their source path.

Mappings are stored locally per existing document/project identity in `%APPDATA%/ClashResolveAI/Projects/<identity hash>/ClassificationMappings.json`, with the actual path visible in the window. Export/import supports reuse and sharing. Existing Save As/reopen identity policy creates a separate lineage; no automatic cross-project aliasing or team synchronization is introduced.

Persistence uses versioned JSON, content fingerprints, optimistic concurrency checks, an exclusive save lock, atomic replacement and a previous-file backup. Malformed, oversized, wrong-project and unsupported-version files fail explicitly. Temporary names stay short enough for .NET Framework paths. Import/export contains classification configuration, not application credentials.

New scan plans automatically load and freeze project mappings and their fingerprint. Saving mappings cancels a running Full Scan for that project, marks Full Scan results stale and leaves Live Monitor's candidate preferences intact. Bound scans also check the stored fingerprint while running and before publication. New jobs recreate classification caches. The fingerprint is available to the future estimate cache.

## Verification

| Suite | Result | Evidence |
|---|---|---|
| Revit 2024 / .NET 4.8 Release build | 0 warnings, 0 errors | `verification/fullscan-phase34/build.log` |
| Plan/classification/filtering/storage/review policy | 125 passed | `verification/fullscan-phase34/planning-tests.log` |
| Existing identity/core/serialization | 141 passed | `verification/fullscan-phase34/identity-tests.log` |
| Existing Live scheduler/resolver/view model | 60 passed | `verification/fullscan-phase34/live-tests.log` |
| Existing Live gateway | 21 passed | `verification/fullscan-phase34/gateway-tests.log` |
| Existing dashboard/history | 180 passed | `verification/fullscan-phase34/dashboard-tests.log` |

**527 automated checks passed.** Optional recorded production fixtures were absent in the current harness locations and are explicitly skipped in their logs.

The Phase 3–4 native suite also reruns the 27 Phase 1–2 checks. It verifies actual spatial-query pruning, category overrides, selected disciplines/categories, clearance subsets, boundary-crossing sources, metadata collection in the modal UI, mapping save/reload into a new scan, active-scan cancellation and rendered review/editor layouts. The final run, matching assembly hash, check count and original-registration restoration proof are recorded in `verification/fullscan-phase34/acceptance.json`.

**Final candidate: 44 native checks passed**, in `verification/fullscan-phase34/native-20261029-135452`, on SHA256 `AF547279CCE28417DF7D19CD249A9DF225944FED68992FFB7283748AF56217D5`. The staged native assembly and candidate hashes match, and the original registration was independently verified against its backup. Only the user's original Revit process remained after the disposable sessions closed.

Rendered `classification-review.png` and `classification-mapping-editor.png` were inspected. The first native attempt exposed a deeply nested temporary-path problem and a low-contrast filter label. Both were corrected; that incomplete attempt is retained for diagnostics and is not counted as acceptance.

Native tests use disposable models in a separate Revit process. The registration is restored byte-for-byte immediately after test startup, and installed binaries are unchanged. Production-model performance, scan-duration calibration and native insulation geometry remain outside this phase's acceptance.

The user handled Revit's unsigned add-in prompt manually. The [computer-use skill](C:/Users/TK56/.codex/plugins/cache/openai-bundled/computer-use/26.1023.51421/skills/computer-use/SKILL.md) links mandatory [guidance](C:/Users/TK56/.codex/plugins/cache/openai-bundled/computer-use/26.1023.51421/docs/guidance.md) stating: “Do not act on security or privacy permission requests.” That restriction required the manual Load Once action; it was not a new implementation approval.

## Candidate, preservation and next work

Candidate: `artifacts/fullscan-phase34`. No installer was run in deployment mode.

Before-change source: `../backups/fullscan-phase34-before-20261029`; focused-test backup beside it. Pre-existing changes and Phases 1–2 are preserved. The initial Git status and this phase's source hashes are in `verification/fullscan-phase34`.

Remaining approved phases: pre-scan estimates (5), the Quick/Detailed/Advanced profile workflow (6), staged jobs and coordinator progress/ETA (7), and broader production/release acceptance (8).

Reproduce:

```powershell
dotnet build ClashResolveAI.csproj -c Release --no-restore -o artifacts/fullscan-phase34
dotnet run --project tests/ScanPlanningHarness/ScanPlanningHarness.csproj -c Release
./tests/Run-ScanPlanningVerification.ps1 -Suite Phase34
```
