# Live Monitor recovery and verification

Live Monitor retains its batch and iterator across ordinary native API overruns, yields between callbacks, and preserves pending work during hard protection pauses. Input refresh and source preparation advance incrementally. Previous clash partners beyond the 500-source job limit are queued for later local jobs instead of requiring a session clear. Returning from Off revalidates monitored sources. Publication still checks current inputs, document revision, generation, identity and coverage.

Live diagnostics expose input validation, refresh, preparation, DTO capture and reconciliation timings, together with the existing engine timings. Live Monitor and Clash Radar remain independent of Full Scan and Dashboard. The earlier separation changes included in this commit remove Full Scan callbacks that renewed or acknowledged live work and give the two modes distinct input fingerprints.

The standalone LiveMonitorHarness now catches failures, reports the exception to stderr and exits with code 1. Its controlled failure option is `--verify-failure-exit`. The obsolete Full Scan prerequisite assertions were updated. Test binaries are not deployed with the add-in.

Validation of the deployed build:

- Production build: zero warnings and errors.
- 60 scheduler/resolver/view-model checks passed.
- Previous unchanged regression suites: 141 identity/lifecycle/reliability/diagnostics, 21 gateway, and 16 Radar WPF/provider/writer checks passed.
- 37 native Revit checks passed against the exact installed DLL; all 15 live batches completed. Maximum recorded API callback on the small fixture was 51.36 ms.
- Native checks covered committed pipes and cable trays, toast notification, actual inspector solids, native 3D/2D navigation, Ignore, recheck, CSV output, Undo/Redo, repeated/rotated links, link unload/reload, modes, resize, bursts, deletion, Save As, document isolation and cancelled close recovery. Full Scan stayed idle and Dashboard stayed empty.
- All 14 installed DLLs matched the final build. Normal startup registration was restored after isolated fixture testing.

Native fixture tests use the opt-in `LiveMonitorAcceptanceApplication`; normal `App` startup never invokes it. CSV verification calls the same writer used by the product export command. The save picker and external CSV application were not automated. Native commands were executed programmatically in Revit, not manually clicked.

Installed/native-tested assembly SHA256: `9250026C22F959E655F9FDFB27EACE6F4A7D83919BE6CCA58779D3C83B34E453`.

Large production-model performance and the user's particular pipe pair remain unverified. A local model-copy attempt exited during native model opening before Live Monitor started. Fixture results do not establish large-model responsiveness.
