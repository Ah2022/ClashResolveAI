# ClashResolveAI

**Clash coordination for Autodesk Revit 2024 · Version 9.4.0**

ClashResolveAI helps BIM coordinators and MEP designers find physical clashes, inspect conflicting elements, track issues, and prepare coordination reports inside Revit. It provides two separate workflows: a project scan for coordination and local monitoring while you work.

| Your task | Use | Results appear in |
|---|---|---|
| Review the configured project scope | **Full Scan** | **Dashboard** |
| Check selected, newly placed, or edited host elements | **Live Monitor** | **Clash Radar** |

**Radar contains only Live Monitor results. Dashboard contains only Full Scan results.** Completing live checks does not update or certify an older Dashboard scan.

## What it does

- Confirms hard clashes using Revit solid intersections. Bounding boxes find candidates; they do not confirm clashes.
- Defaults to **hard-only** detection with a **1 mm³ minimum overlap**. Optional combined mode also checks clearance rules.
- Checks host elements against the host and configured loaded links. **Include link-to-link is off by default.** Unloaded links cannot be checked.
- Lets you navigate to issues in 2D/3D, inspect element and overlap geometry, and manage issue status.
- Exports Dashboard coordination results to Excel, Word, and BCF 2.1. Radar has a separate CSV export.
- Offers optional AI-assisted suggestions and RFI text. Geometry detection works without an API key.

The add-in supports coordination decisions; a scan is not a guarantee that a model is clash-free. Unsupported geometry or failed Boolean operations remain **unverified**, not confirmed hard clashes.

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
2. Review **Settings**, then choose **Full Scan**. Keep hard-only for physical intersections; choose combined mode when clearance checks are needed.
3. Review results in **Dashboard**. Inspect the geometry and navigate to the affected elements before deciding how to resolve an issue.
4. After model changes, run Full Scan again before generating reports from Dashboard.
5. Use **Generate RFIs** for coordination exports.

### 2. Monitor while modelling

1. Click **Live Monitor** to start; review its results in **Clash Radar**.
2. Select, place, or edit supported host elements. Local checks update the affected live results.
3. Use **Re-check drawn** to recheck the current session's tracked elements. This does not run a project-wide Full Scan.
4. Use Radar's result-type and session filters to control what you see. Stopping and restarting monitoring begins a new live session.
5. Click **Live Monitor** again to stop.

Live monitoring is intentionally local. A type edit can recheck up to 500 in-scope instances. Larger type edits and changes such as levels or links show a **Full Scan needed** notice instead of silently scanning the entire model. Clearing local work does not remove the need to refresh Dashboard.

## AI and project data

AI suggestions are optional. When enabled and invoked with a configured API key, the add-in sends clash descriptions to OpenAI, including element IDs/categories, disciplines, level/grid/location text, rule information, and available link filenames. It does not upload an entire RVT file through that integration. Use this feature only where your project permits sharing those details; review generated suggestions before use.

Keep API keys, settings, models, result databases, and exported project reports out of Git. The repository's ignore rules exclude these local files. No API key is included.

## Validation and known limits

Version 9.4 passed **84 standalone checks**, **136 in-Revit integration checks**, the WPF inspector harness, and BCF schema validation during release verification. The production-copy comparison preserved all **1,419 baseline result identities**, including **28 hard clashes**. Hard-only retained those 28 hard clashes and 9 unverified Boolean-failure pairs with no surface-distance calls.

Interactive multi-segment drawing, automatically created fittings, live link transform/unload behavior, material/diameter type edits, and exactly 500 affected instances were not separately verified. Scripted pipe placements, Undo/Redo, and type edits affecting 2 and 501 instances were exercised. See [testing documentation](docs/TESTING.md) for reproducible checks and evidence limits.

## Project layout

```text
src/                      One production source tree
Resources/                Ribbon icons
tests/                    Regression checks, schemas, and Revit harness scripts
docs/TESTING.md            Validation instructions
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
