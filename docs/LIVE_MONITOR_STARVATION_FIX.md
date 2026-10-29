# Live scan starvation correction — October 6, 2026

Runtime diagnostics showed repeated `Preparing 7 elements` with no scan-start timestamp or tested pairs. Input fingerprint validation was executed before every scan slice. On the affected host it consumed more than the preferred 25 ms slice, so callbacks repeatedly returned before advancing geometry. A later 130.8 ms callback crossed the 100 ms protection threshold and correctly retained the batch while pausing. The most recent runtime mode was Off, which also suppresses checking; reopening the pane intentionally does not change the mode.

The scheduler now polls the environment once per second and uses that fingerprint between polls. Host document revision and session guards remain active on each callback. DTO capture proceeds in slices before a fresh environment validation immediately prior to publication. Changed rules or loaded-link inputs reject publication and require Full Scan. The hard responsiveness limit still applies; this correction does not increase it or add blind automatic retries.

Regression checks simulate input validation slower than the preferred scan slice and verify that geometry and DTO publication complete. A separate check changes inputs during a job and verifies that cached polling cannot publish stale results. Real model logs and names are not committed.

All 46 scheduler/resolver/view-model checks passed. The slow-input regression was also run against the original scheduler and failed at the expected starvation assertion, confirming the test exercises the reported defect. Production compilation succeeded. Native deployment and testing require Revit to be closed; previous native checks apply to the earlier build.
