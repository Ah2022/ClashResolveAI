# Testing

Run commands from the repository root in PowerShell. Local outputs go under ignored `artifacts` or `verification` folders. Do not commit customer models or scan evidence.

## Standalone checks

```powershell
dotnet run --project tests/IdentityTests.csproj -c Release
dotnet run --project tests/LiveMonitorHarness/LiveMonitorHarness.csproj -c Release
dotnet run --project tests/LiveGatewayHarness/LiveGatewayHarness.csproj -c Release
dotnet run --project tests/RadarUiHarness/RadarUiHarness.csproj -c Release
.\tests\Test-ScanBaselineComparator.ps1
.\tests\Test-HardOnlyComparator.ps1
dotnet run --project tests/InspectorHarness/InspectorHarness.csproj -c Release -- artifacts/inspector
```

The two comparator self-tests use `tests/fixtures/synthetic-baseline.json`, which contains invented records and no project data. These tests verify comparison logic, not Revit geometry. The WPF harness requires Windows/.NET Framework 4.8; inspect its generated layouts as well as its checks file.

Validate an exported BCF archive with:

```powershell
.\tests\Validate-Bcf.ps1 -Path 'C:\path\to\export.bcf'
```

## Revit integration and production-copy comparison

For the installed Live Monitor 9.4 build, close Revit and run `./tests/Run-LiveReleaseVerification.ps1`. Approve the unsigned add-in prompt if you trust the build. This creates synthetic host and linked models in a new ignored verification folder. Wait for `complete.txt`; any `failed.txt` is a failure. Tests cover modes, placement, movement, deletion, dockable pane, navigation/inspection, stale rejection, Full Scan handoff and diagnostics. Actual UI Undo/Redo and large-model performance remain separate acceptance checks; they must not be inferred from these synthetic checks.

Build and install the add-in first. Put an authorized, disposable model copy under `verification\production`. Never point the harness at an original project. The harness rejects production model paths outside this directory.

```powershell
.\tests\Run-DisposableRevitVerification.ps1 -ModelCopy "$PWD\verification\production\model-copy.rvt"
```

This stages the release into a new `verification\run-*\addin` folder, backs up the installed manifest, starts a separate Revit process, confirms the assembly loaded, and restores the original manifest. It does not replace the installed binaries. Revit may require you to approve the unsigned add-in. Keep the disposable window idle during automated steps.

At `phase4-awaiting-undo.txt`, perform exactly one Undo for **Phase4 place segment 2**, wait five seconds, then perform exactly one Redo. Do not continue pressing Undo/Redo after that checkpoint. Revit's public API cannot perform this action for the test.

Wait for **both** `complete.txt` and `production-complete.txt`. Any `failed.txt` or `production-failed.txt` is a failure. The startup script returning successfully only confirms startup and manifest restoration, not completion of the suite.

Compare a completed run against your own saved, authorized baseline:

```powershell
.\tests\Compare-ScanBaseline.ps1 -Actual verification/run-NEW/production-scan.json -Baseline verification/baseline/production-scan.json
.\tests\Compare-HardOnlyBaseline.ps1 -Actual verification/run-NEW/production-hard-only.json -Baseline verification/baseline/production-scan.json
.\tests\Compare-HostScopeExperiment.ps1 -Actual verification/run-NEW/phase3-fixture.json
.\tests\Compare-LedgerVerification.ps1 -RunFolder verification/run-NEW
```

`run-NEW` is a placeholder for the actual run directory. Production baselines are deliberately not supplied in Git. `Run-RevitVerification.ps1` is the alternative installed-build entry point and requires all Revit sessions closed first.

## Version 9.4 release evidence

The pre-publication release verification on 1 October 2026 passed:

- Release compilation with zero warnings/errors.
- 84 standalone checks and 136 in-Revit integration assertions.
- Ten inspector layouts and official BCF 2.1 schema/reference checks.
- Full/targeted result agreement for ten sampled source elements.
- Host-centric equivalence against loaded synthetic linked models.
- Separation of Dashboard/Full Scan and Radar/Live Monitor, including independent cancellation and lifecycle state.
- Drawn-element ledger, Undo/Redo, scoped deletion and full-scope readiness checks.

| Measurement | Prior combined baseline | 9.4 combined | 9.4 hard-only |
|---|---:|---:|---:|
| Elapsed seconds | 18.962 | 19.011 | 0.961 |
| Sources | 751 | 751 | 751 |
| Candidate pairs | 2,588 | 2,588 | 954 |
| Tested pairs | 1,419 | 1,419 | 384 |
| Hard result rows | 28 | 28 | 28 |
| Clearance result rows | 739 | 739 | 0 |
| Unverified result rows | 652 | 652 | 9 |

All 1,419 baseline identities and compared result fields agreed. Hard-only preserved the 28 hard and 9 Boolean-failure identities with zero surface-distance calls/time. These are single-run measurements on one model copy, not a general performance promise. Raw model-specific evidence remains local.

The installed release DLL matched the tested staged DLL byte-for-byte. The repository cleanup reorganizes build/test paths and documentation; the production source is unchanged from that verified release. A newly compiled DLL can have a different hash due to build metadata and source paths.

## Not separately verified

- Interactive multi-segment drawing and automatically generated fittings; the scripted elbow fixture could not insert a fitting.
- Live link-transform/unload events, material/diameter type edits, and exactly 500 affected type instances. Two and 501 instances were tested.
- The production copy's 13 unloaded links; loaded links were covered by synthetic fixtures.
- An additional Revit integration run after repository-only reorganization, or AI API requests as part of the offline regression suite.

No optional Phase 7 performance changes are included.
