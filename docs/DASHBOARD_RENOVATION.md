# Full Scan dashboard renovation

October 7, 2026. Phases 0–6 are implemented; Phase 7 controlled native acceptance, rollback checks and release documentation are complete. Representative production-model performance, Word page visual QA and normal installation acceptance remain open.

## Workspace and persistence

- Five modeless views: Overview, Issues, Groups, History and Analytics.
- Schema version 2 stores immutable completed scan observations, attempts, captured scope/configuration/link metadata, lifecycle events and group membership. Existing databases receive a migration backup. Failed publication rolls back current state and retains the failed attempt.
- Shared metric and comparison policies distinguish confirmed physical findings, uncertainty, workflow state and coverage. Partial scans, unloaded links and changed settings cannot silently imply resolution outside evaluated scope.
- Group identity/default ownership survives rescans; split/merge lineage, mixed member states and audited bulk transitions are explicit.
- Historical facts remain read-only. Navigation resolves available current components and explains missing/deleted/unloaded endpoints.

## Analytics, exports and previews

Analytics uses the selected version and issue filters. Charts support exact-identity drill-down, age/status, uncertainty, persistence, component involvement and scan trends. Resolution throughput discloses its baseline cohort and period and uses dated lifecycle events; missing baselines and unknown time provenance are explicit.

BCF, Excel and Word support filtered issues or selected groups in the shown current/historical state. Copied export requests retain evidence, workflow, ownership and comparison context. Historical exports do not replace captured facts with current workflow. BCF retains context in topic descriptions and `dashboard-context.json`.

The Issues panel automatically loads copied geometry through a dedicated Revit ExternalEvent. Its embedded inspector defaults to side-by-side Plan/3D and retains cuts, pan/zoom, framing, floating inspection and permanent-view pinning. Document/selection generations reject late callbacks. Historical/current-stale rows explain why current geometry cannot reconstruct their captured state. Stored snapshot references remain external files.

## Verification

The authoritative Windows/Revit 2024 checkout passed a Release build with zero warnings/errors, **395 automated checks** and **168 controlled native checks**:

| Suite | Passed |
|---|---:|
| Dashboard SQLite/comparison/group/history/Analytics/export/preview | 186 |
| Identity/core/inspector/serialization | 142 |
| Live scheduler/resolver/view-model | 46 |
| Gateway dispatch | 21 |
| Native engine/scope/lifecycle/ledger/Undo/Redo/full-live isolation | 115 |
| Native dashboard/preview/history/export/rollback | 28 |
| Native Live Monitor release | 25 |

Additional checks inspected ten inspector layouts and validated current/historical issue/group BCF against the BCF 2.1 schemas. Word passed OpenXML schema/content validation. A copied migration backup reopened with its original schema and safely remigrated issue, event and group data.

All three final native suites used assembly SHA256 `EA3867943E05186E80766A08AC55BF649EC5365A2E3F1583856B187F24311414`. This identifies the locally verified candidate; rebuilding from another checkout/path can produce a different assembly hash. Native scripts temporarily stage the candidate registration, verify startup and restore the installed registration byte-for-byte. Installed binaries were not replaced. The user performed native Undo/Redo; its ledger effects were independently checked. The broad suite's engine-only entry omits the older native image-export sequence. Raw model-specific evidence and build outputs remain local and are excluded from Git.

The automated total includes an authorized 1,419-issue recorded fixture that is deliberately not published. The dashboard harness explicitly skips that optional workload when the fixture is absent. The publication checkout separately passed a zero-warning/error Release build and 389 reproducible checks without that fixture (180 dashboard, 142 identity, 46 live and 21 gateway), plus four BCF schema validations. See [Testing](TESTING.md) for reproducible commands.

## Performance and acceptance limits

Synthetic 20,000-row measurements: ranking 54 ms, window construction/layout 779 ms, group lineage 43 ms and Analytics 204 ms. The prior window baseline was 793 ms. Recorded 1,419-issue copied-data publication/rescan measured 1,375/1,392 ms; comparison/read 512 ms and filtering below 1 ms. Retained managed-memory growth after collection was 192 bytes in the final run and approximately 38.7 MiB in an earlier run. This measures net managed retention, not peak/native process memory.

These measurements exclude native production geometry collection. Large representative model performance remains an acceptance gate. Word page visual QA could not run because LibreOffice was unavailable; schema/content validation does not establish page-layout acceptance. Installation and loaded-assembly acceptance in the normal user workflow remain pending. Optional dashboard docking is not included; the supported workspace is modeless.

Read the [Dashboard user guide](DASHBOARD_USER_GUIDE.md) for views, filters, ownership, historical state, previews and export scopes.
