# Full Scan coordination dashboard guide

The renovated dashboard keeps Full Scan history separate from Live Monitor results. Open **Full Scan Dashboard**, run a scan, and use the five workspace tabs to review and coordinate the saved findings.

## Select the state you want to inspect

The current workspace shows the latest saved physical evidence with current workflow assignments and status. The header shows scan completion time, rules, scope, comparison baseline and freshness. Model edits can make the evidence stale; run Full Scan before preview, current navigation or current export when prompted.

In **History**, select a completed attempt and choose **Browse this completed version** to freeze its capture-time state, including the latest version. Historical rows are read-only. **Open current issue for editing** explicitly returns to the current identity; an archived identity cannot be edited through its old snapshot. Failed and cancelled attempts preserve diagnostics and never replace the last completed scan.

## Review a clash and its preview

In **Issues**, select a row. The right panel automatically loads geometry through Revit's API queue and displays **Plan and 3D** together. Orange/teal identify the components; red indicates computed current intersection. A clearance finding can legitimately have no red overlap. A geometry error or incomplete overlap is explained in the preview notice.

- **Plan**, **3D**, **Cut** change the view. The 2-up/3-up selector shows multiple views together.
- Drag to pan and scroll to zoom. **Fit**, **Turn**, and **A/B** adjust framing or component alignment.
- **Context** adds surrounding geometry. **Focus** frames the clash point; the slider adjusts the framing depth.
- **Revit** opens the corresponding native view, **Float** opens the existing geometry inspector, and **Pin** creates a permanent Revit 3D inspection view.
- **Reload preview** retries the selected finding. Switching rows cancels the relevance of earlier completions; an earlier callback cannot replace the new selection.

Historical rows do not render today's geometry as if it were historical. **Viewpoint** displays a stored snapshot file when it is still available. **Select current components** resolves the saved component identities in the current model and explains unavailable components. External snapshot files are references, not a durable media archive.

The detail tabs show Summary, Elements, Evidence, History, Comments and Viewpoint. Owner/due/comment edits and allowed next workflow actions use audited commands. Resolution requires a reason; workflow resolution by itself does not prove physical repair.

## Coordinate a group

**Groups** shows exact-membership group versions, mixed workflow/evidence distributions, default and effective owners, due date, oldest actionable time and lineage. A changed membership creates a new group version with parent/child links. Group comparison projections may overlap; do not sum parent and child totals as unique issue counts.

Group defaults apply to members that have no manual override. Individual owner/due edits remain overrides across rescans. Splits inherit defaults; conflicting merge defaults require review and retain existing issue assignments. Bulk status actions apply only to current members and must satisfy every member's transition rules. Archived/baseline members retain captured state in the member list. A member link for a group that exists only in an earlier version opens its saved membership snapshot.

## Read Analytics

**Analytics** uses the selected version and the current Issues filters/type toggles. Clear filters to restore the full scope. Charts show actionable issues by discipline pair, confirmed critical by level, uncertainty reasons, age/status, scan persistence and component involvement. Component involvement buckets overlap because one finding involves two components. The largest 30 buckets are shown where needed; the underlying issue scope remains complete.

Click a bar to inspect exactly its issue identities. The saved-scan trend compares consecutive versions for the selected identity cohort and retains coverage/configuration warnings. Select a trend row and browse its immutable version. If coverage text is clipped, widen that grid column or read the full comparison context above.

The resolution rate states its denominator and period: baseline actionable issues that entered Resolved through dated lifecycle events. Reopened issues may already have contributed to that throughput measure. Duration uses dated lifecycle episode starts and resolution events. Unknown-timezone events and unavailable baselines are reported explicitly. Historical ages use the selected scan's completion time rather than today's date.

## Export the shown state

Choose **Filtered issues (shown state)** or **Selected group (shown state)** in the toolbar, then BCF, Excel or Word. The shown current/historical state determines the export scope; the report records document, scan, baseline, coverage, rules, links and filters. Choose a destination folder. Generation runs on copied data so later edits cannot substitute today's workflow into a historical export.

For a comparison-only older group, browse that group's saved version before exporting it. Group BCF uses its default owner and includes effective member owners/comments. Mixed workflow exports Open plus the exact distribution in the description. Excel and Word retain the selected member rows. Snapshot files are referenced where available; the export does not recreate historical geometry.

## Installation and recovery

Build from the repository root with `build.ps1`; output is staged under `artifacts/release`. Close Revit, review `deploy-clean.ps1 -DryRun`, then run `deploy-clean.ps1` to install. The deployment tool backs up the owned add-in registration and binaries; preserve its reported backup for rollback. Database migrations retain a pre-migration backup. Preserve scan databases when reverting binaries, and keep a copy if returning to a build that predates scan-history support.

Controlled native acceptance and remaining production-model/Word page-rendering limits are recorded in [Dashboard renovation verification](DASHBOARD_RENOVATION.md). The modeless workspace is the supported layout; optional dashboard docking has not been added.
