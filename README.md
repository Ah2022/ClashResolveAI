# ClashResolveAI

**Clash coordination for Autodesk Revit 2024 · Version 9.4.0**

ClashResolveAI helps BIM coordinators and MEP designers find physical clashes, inspect conflicting elements, track issues, and prepare coordination reports inside Revit. It provides two separate workflows: a project scan for coordination and local monitoring while you work.

| Your task | Use | Results appear in |
|---|---|---|
| Review the configured project scope | **Full Scan** | **Dashboard** |
| Check selected, newly placed, or edited host elements | **Live Monitor** | **Clash Radar** |

**Radar contains only Live Monitor results. Dashboard contains only Full Scan results.** Completing live checks does not update or certify an older Dashboard scan.

![ClashResolveAI coordination workflow](docs/images/coordination-workflow.svg)

[Quick-start manual](docs/QUICK_START.md) · [Step-by-step usage map](docs/USAGE_MAP.md) · [Full Scan implementation and release gates](docs/FULL_SCAN_PHASE7_8.md)

## What it does

- Confirms hard clashes using Revit solid intersections. Bounding boxes find candidates; they do not confirm clashes.
- Defaults to **hard-only** detection with a **1 mm³ minimum overlap**. Optional combined mode also checks clearance rules.
- Offers **Quick**, **Detailed** and **Advanced** profiles. Quick starts with one floor or bounded zones, host-host and selected loaded links, standard discipline pairs, and generic/support/insulation exclusions.
- Reviews category/system classification, evidence and confidence. Project mappings explicitly classify uncertain families or exclude construction aids; unmapped generic models remain Unknown.
- Estimates selected relationships using metadata and bounding boxes before geometry. Remove expensive pairs and recalculate; Start requires a current reviewed estimate. Duration remains unavailable without comparable measured timings.
- Runs Detailed coordination in two saved passes: hard clashes first, then selected clearance tests for elements touching confirmed overlap areas. Cancelling clearance retains the saved hard results.
- Keeps internal-link scans, explicit link-to-link jobs and dedicated insulation clearance separate. **No automatic every-link-against-every-link scan.** Unloaded links cannot be checked.
- Shows stage, model relationship, floor, source counts, bounding-box candidates, geometry/Boolean tests, exclusions, unknown classifications and a source-rate ETA.
- Lets you navigate to issues in 2D/3D, inspect element and overlap geometry, and manage issue status.
- Embeds automatic side-by-side 2D/3D previews in the Issues panel.
- Exports the shown current/historical issue or group scope to Excel, Word and BCF 2.1. Radar has a separate CSV export.
- Offers optional AI-assisted suggestions and RFI text. Geometry detection works without an API key.
- Keeps saved scan versions, issue history, recurrence, owners, due dates, groups and coordination analytics. Exports respect the selected current/historical view and filters.

The add-in supports coordination decisions; a scan is not a guarantee that a model is clash-free. Unsupported geometry or failed Boolean operations remain **unverified**, not confirmed hard clashes.

Read the [dashboard user guide](docs/DASHBOARD_USER_GUIDE.md) and [renovation verification](docs/DASHBOARD_RENOVATION.md) for workspace details and earlier acceptance limits.

## Where it helps compared with Navisworks Manage

ClashResolveAI's main advantage is its short feedback loop **inside the Revit authoring model**. Designers can check local changes, inspect native elements, fix the model and recheck without preparing a new coordination export for each local iteration. Its predefined profiles and visible classification mappings also help coordinators control scope before an expensive scan.

| Coordination need | ClashResolveAI | Navisworks Manage |
|---|---|---|
| Local design feedback | Live Monitor checks changed/selected host elements and nearby loaded-link geometry; Radar follows the current Revit session. | Clash Detective provides configurable project tests; SwitchBack can locate objects in Revit. |
| Physical evidence | Native Revit solid intersection and overlap volume; clearance evidence is labelled, and uncertain geometry stays Unverified. | Hard, conservative hard, clearance and duplicate tests; supports triangulated geometry and point clouds. |
| Scope control | Ready-to-use profiles, explicit link-instance pairs, classification review, pre-geometry discipline pruning and a workload review gate. | Selection A/B, selection/search sets and test rules provide configurable federated test scope. |
| Coordination follow-through | In-Revit 2D/3D inspection, issue ownership/due dates, saved scans, recurrence, groups, analytics and scoped BCF/Excel/Word exports. | Established clash review, statuses, grouping, viewpoints and reporting workflows. |
| Project breadth | Focused on supported Revit 2024 elements and loaded RVT links. | Stronger fit for broad model federation, point clouds, moving-object and TimeLiner-based coordination. |

These are workflow tradeoffs, not a benchmark showing that one engine is universally faster or more accurate. ClashResolveAI's mm³ overlap threshold and Navisworks' distance tolerance are different measures. Production performance and duration calibration must be validated on the project's models. For federated deliverables and 4D review, Navisworks remains complementary.

Autodesk references: [Clash Detective overview](https://help.autodesk.com/cloudhelp/2026/ENU/Navisworks-Clash-Detective/files/GUID-36D9904E-12F3-4F82-8DD3-C2103DB0BC29.htm), [test types and tolerances](https://www.autodesk.com/support/technical/article/caas/tsarticles/ts/kmN9PtAkAZozDGi5UPN5W.html), [selection, review and reports](https://www.autodesk.com/support/technical/article/caas/tsarticles/ts/4T3o6YMrs32IPvWzn4VOEP.html), [Revit navigation and SwitchBack](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/How-to-find-Revit-elements-from-a-clash-in-Navisworks.html). Reviewed October 29, 2026.

## Requirements

- Windows x64 with **Autodesk Revit 2024** installed.
- For building: the .NET SDK, .NET Framework **4.8 targeting pack**, and access to the project's NuGet dependencies. The standalone checks target .NET 8.
- Revit API assemblies are referenced from `C:\Program Files\Autodesk\Revit 2024`. Adjust the two HintPath entries in `ClashResolveAI.csproj` if your installation differs.

Other Revit releases have not been verified. This repository contains source code; compiled binaries and Autodesk assemblies are not committed.

## Build and install

Clone or extract the project into a folder such as `ClashResolveAI_v9_4`. In PowerShell, from that folder:

```powershell
.\build.ps1
```

The script restores dependencies and builds to `artifacts\release`. After a successful build, **close all Revit sessions**, then review and run the installer:

```powershell
.\deploy-clean.ps1 -DryRun
.\deploy-clean.ps1
```

The installer checks version 9.4.0.0, backs up existing ClashResolveAI files under `backups`, preserves existing rules/settings, installs the add-in for the current Windows user, and verifies installed DLL hashes. It refuses to replace an add-in loaded by Revit. Deployment backups are local and excluded from Git.

Restart Revit. Open **MEP AI Tools → ClashResolve AI 9.4**. If Revit displays an unsigned publisher prompt, review and approve the add-in yourself if you trust the build.

## Everyday workflow

### 1. Run a coordination scan

1. Open your project and load the links you intend to coordinate.
2. Choose **Full Scan → Quick coordination** for routine physical checks. Select one floor or bounded zones and the needed loaded link instances.
3. Review classification and project mappings, calculate the estimate, remove expensive relationships and recalculate. Start only after reviewing the current workload.
4. Use **Detailed coordination** when selected clearance rules are needed around confirmed hard-clash areas. Review its area radius. Each completed pass is saved; no confirmed areas means clearance is skipped. Advanced allows a broader clearance job or a separate explicit cross-link/insulation job.
5. In **Dashboard**, inspect issues in 2D/3D, review groups, assign owners/due dates, add comments and follow History/Analytics. Findings outside the current reliable scope remain unevaluated.
6. Fix the authoring model and rerun the relevant scope. Select the intended saved/current view and filters before exporting BCF, Excel or Word. Live checks do not refresh Dashboard.

### 2. Monitor while modelling

1. Click **Live Monitor** to create a session for the active host document and open the dockable **Clash Radar**.
2. Choose **Live** for automatic local checks after a 300–700 ms debounce, **Trigger** to accumulate edits until **Check Changes**, or **Off** to stop processing changes and clear queued work.
3. Place or edit supported host elements. Checks compare affected sources with nearby host elements and configured loaded links, then reconcile new and resolved issues by stable identity.
4. Review scan status, queue depth and the **Diagnostics** expander. Export diagnostic snapshots when investigating performance or stale requests.
5. Run **Full Scan** when Radar requests it. A successful current Full Scan renews the session generation and resumes pending local work. Responsiveness protection requires an explicit recheck.

Radar stores immutable snapshots. Navigation and inspection validate the current document, session, element UniqueIds, geometry revisions and linked environment before resolving Revit elements. Stale snapshots cannot be used to inspect or navigate old geometry.

Live monitoring is intentionally local. A type edit can recheck up to 500 in-scope instances. Larger type edits and changes such as levels or links show a **Full Scan needed** notice instead of silently scanning the entire model. Clearing local work does not remove the need to refresh Dashboard.

## AI and project data

AI suggestions are optional. When enabled and invoked with a configured API key, the add-in sends clash descriptions to OpenAI, including element IDs/categories, disciplines, level/grid/location text, rule information, and available link filenames. It does not upload an entire RVT file through that integration. Use this feature only where your project permits sharing those details; review generated suggestions before use.

Keep API keys, settings, models, result databases, and exported project reports out of Git. The repository's ignore rules exclude these local files. No API key is included.

## Validation and known limits

The Full Scan source update includes automated policy/storage/lifecycle regressions, native disposable-model suites, loaded-link fixture benchmarks and a reproducible release gate. Current results and outstanding gates are recorded in [the Phase 7–8 report](docs/FULL_SCAN_PHASE7_8.md); fixture measurements do not certify production-model performance. The source push does not install or replace the add-in in an existing Revit session.

Detailed clearance covers **elements touching confirmed hard-clash areas**; it does not certify clearance elsewhere. Use Advanced for a deliberately broader clearance scope. Floor/workset zones are supported; grid-range zones fail explicitly. A single Revit geometry call can exceed a preferred work slice. Curved-surface distances and failed geometry are not promoted to exact evidence.

The October 6 Live Monitor upgrade passed **223 automated checks** across identity/diagnostics, scheduler/resolver/view-model, gateway and WPF Radar harnesses. Native verification and remaining acceptance limits are recorded in [the release verification report](docs/LIVE_MONITOR_9_4_VERIFICATION.md). Earlier October 1 results (84 standalone and 136 native checks, plus a 1,419-result production comparison) describe the previous build and are not evidence for this upgrade.

Interactive multi-segment drawing, automatically created fittings, live link transform/unload behavior, material/diameter type edits, and exactly 500 affected instances were not separately verified. Scripted pipe placements, Undo/Redo, and type edits affecting 2 and 501 instances were exercised. See [testing documentation](docs/TESTING.md) for reproducible checks and evidence limits.

## Project layout

```text
src/                      One production source tree
Resources/                Ribbon icons
tests/                    Regression checks, schemas, and Revit harness scripts
docs/TESTING.md            Validation instructions
docs/USAGE_MAP.md          Coordinator/designer workflow and decision map
docs/images/              Editable visual usage map
scripts/                  Release checks and portable package creation
ClashResolveAI.csproj      Revit 2024 / .NET Framework 4.8 project
ClashResolveAI.addin       Manifest template
build.ps1                 Build the current release
deploy-clean.ps1          Guarded installation with backup and hash checks
CHANGELOG.md              One release history
```

Generated builds, deployment backups, local verification evidence, and model files are excluded from the repository. See [CHANGELOG.md](CHANGELOG.md) for release changes.

## Reporting a problem

Include the add-in version, Revit version, which workflow you used, expected versus actual behavior, and minimal reproduction steps. Share a sanitized example only when authorized; do not attach confidential models or credentials.

## License

Copyright © 2026 BIM Coordination Engineering. All rights reserved, as stated in the existing project documentation. No open-source license is granted by this repository.
